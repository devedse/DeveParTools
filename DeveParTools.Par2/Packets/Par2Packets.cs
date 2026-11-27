using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeveParTools.Par2.Packets;

/// <summary>16-byte MD5 value used for packet checksums, file IDs and the recovery set ID.</summary>
public readonly record struct Md5Hash(UInt128 Value) : IComparable<Md5Hash>
{
    public static Md5Hash FromBytes(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt128LittleEndian(bytes));

    public static Md5Hash Compute(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[16];
        MD5.HashData(data, h);
        return FromBytes(h);
    }

    public void WriteTo(Span<byte> destination) => BinaryPrimitives.WriteUInt128LittleEndian(destination, Value);

    public byte[] ToArray()
    {
        var b = new byte[16];
        WriteTo(b);
        return b;
    }

    /// <summary>
    /// PAR2 sorts hashes "as 16-byte unsigned integers". The reference client (par2cmdline) compares from byte 15
    /// down to byte 0, i.e. little-endian. Reading the bytes as a little-endian UInt128 gives the same order.
    /// </summary>
    public int CompareTo(Md5Hash other) => Value.CompareTo(other.Value);

    public override string ToString() => Convert.ToHexStringLower(ToArray());
}

/// <summary>The 16-byte packet type identifiers of PAR 2.0.</summary>
public static class Par2PacketTypes
{
    public static readonly byte[] Magic = "PAR2\0PKT"u8.ToArray();
    public static readonly byte[] Main = "PAR 2.0\0Main\0\0\0\0"u8.ToArray();
    public static readonly byte[] FileDescription = "PAR 2.0\0FileDesc"u8.ToArray();
    public static readonly byte[] SliceChecksum = "PAR 2.0\0IFSC\0\0\0\0"u8.ToArray();
    public static readonly byte[] RecoverySlice = "PAR 2.0\0RecvSlic"u8.ToArray();
    public static readonly byte[] Creator = "PAR 2.0\0Creator\0"u8.ToArray();
    public static readonly byte[] UnicodeFilename = "PAR 2.0\0UniFileN"u8.ToArray();
    public static readonly byte[] AsciiComment = "PAR 2.0\0CommASCI"u8.ToArray();
    public static readonly byte[] UnicodeComment = "PAR 2.0\0CommUni\0"u8.ToArray();

    /// <summary>Header: magic (8) + length (8) + packet MD5 (16) + set id (16) + type (16).</summary>
    public const int HeaderSize = 64;
}

public abstract record Par2Packet(Md5Hash SetId);

/// <summary>Slice size and the file IDs of the recovery set (sorted) and the non-recovery set.</summary>
public sealed record Par2MainPacket(Md5Hash SetId, long SliceSize, IReadOnlyList<Md5Hash> RecoveryFileIds, IReadOnlyList<Md5Hash> NonRecoveryFileIds) : Par2Packet(SetId);

public sealed record Par2FileDescriptionPacket(Md5Hash SetId, Md5Hash FileId, Md5Hash FileHash, Md5Hash Hash16k, long Length, string Name) : Par2Packet(SetId);

/// <summary>MD5 + CRC32 of every slice of one file (last slice zero-padded).</summary>
public sealed record Par2SliceChecksumPacket(Md5Hash SetId, Md5Hash FileId, IReadOnlyList<(Md5Hash Md5, uint Crc32)> Slices) : Par2Packet(SetId);

/// <summary>A recovery slice. The data itself stays on disk: <see cref="DataFile"/> at <see cref="DataOffset"/>.</summary>
public sealed record Par2RecoverySlicePacket(Md5Hash SetId, uint Exponent, string DataFile, long DataOffset, long DataLength) : Par2Packet(SetId);

public sealed record Par2CreatorPacket(Md5Hash SetId, string Text) : Par2Packet(SetId);

public sealed record Par2UnicodeFilenamePacket(Md5Hash SetId, Md5Hash FileId, string Name) : Par2Packet(SetId);

public sealed record Par2CommentPacket(Md5Hash SetId, string Text, bool IsUnicode) : Par2Packet(SetId);

/// <summary>Serialises packets (header + body + packet MD5).</summary>
public static class Par2PacketWriter
{
    /// <summary>Full packet bytes for a type and body (body is zero-padded to a multiple of 4).</summary>
    public static byte[] Build(Md5Hash setId, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        int bodyLen = (body.Length + 3) & ~3;
        var packet = new byte[Par2PacketTypes.HeaderSize + bodyLen];
        Par2PacketTypes.Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), (ulong)packet.Length);
        setId.WriteTo(packet.AsSpan(32));
        type.CopyTo(packet.AsSpan(48));
        body.CopyTo(packet.AsSpan(64));
        MD5.HashData(packet.AsSpan(32), packet.AsSpan(16, 16));
        return packet;
    }

    public static byte[] MainBody(long sliceSize, IReadOnlyList<Md5Hash> recoveryIds, IReadOnlyList<Md5Hash> nonRecoveryIds)
    {
        var body = new byte[12 + 16 * (recoveryIds.Count + nonRecoveryIds.Count)];
        BinaryPrimitives.WriteUInt64LittleEndian(body, (ulong)sliceSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)recoveryIds.Count);
        int o = 12;
        foreach (var id in recoveryIds.Concat(nonRecoveryIds))
        {
            id.WriteTo(body.AsSpan(o));
            o += 16;
        }

        return body;
    }

    public static byte[] Main(Md5Hash setId, long sliceSize, IReadOnlyList<Md5Hash> recoveryIds, IReadOnlyList<Md5Hash> nonRecoveryIds)
        => Build(setId, Par2PacketTypes.Main, MainBody(sliceSize, recoveryIds, nonRecoveryIds));

    public static byte[] FileDescription(Md5Hash setId, Md5Hash fileId, Md5Hash fileHash, Md5Hash hash16k, long length, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var body = new byte[56 + nameBytes.Length];
        fileId.WriteTo(body);
        fileHash.WriteTo(body.AsSpan(16));
        hash16k.WriteTo(body.AsSpan(32));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(48), (ulong)length);
        nameBytes.CopyTo(body, 56);
        return Build(setId, Par2PacketTypes.FileDescription, body);
    }

    public static byte[] SliceChecksum(Md5Hash setId, Md5Hash fileId, IReadOnlyList<(Md5Hash Md5, uint Crc32)> slices)
    {
        var body = new byte[16 + 20 * slices.Count];
        fileId.WriteTo(body);
        for (int i = 0; i < slices.Count; i++)
        {
            slices[i].Md5.WriteTo(body.AsSpan(16 + 20 * i));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32 + 20 * i), slices[i].Crc32);
        }

        return Build(setId, Par2PacketTypes.SliceChecksum, body);
    }

    public static byte[] Creator(Md5Hash setId, string text) => Build(setId, Par2PacketTypes.Creator, Encoding.ASCII.GetBytes(text));

    public static byte[] AsciiComment(Md5Hash setId, string text) => Build(setId, Par2PacketTypes.AsciiComment, Encoding.UTF8.GetBytes(text));

    public static byte[] UnicodeFilename(Md5Hash setId, Md5Hash fileId, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var body = new byte[16 + nameBytes.Length];
        fileId.WriteTo(body);
        nameBytes.CopyTo(body, 16);
        return Build(setId, Par2PacketTypes.UnicodeFilename, body);
    }

    /// <summary>Header (with a placeholder MD5) of a recovery slice packet; the caller appends the data.</summary>
    public static byte[] RecoverySliceHeader(Md5Hash setId, uint exponent, long sliceSize)
    {
        var header = new byte[Par2PacketTypes.HeaderSize + 4];
        Par2PacketTypes.Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)(header.Length + sliceSize));
        setId.WriteTo(header.AsSpan(32));
        Par2PacketTypes.RecoverySlice.CopyTo(header, 48);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), exponent);
        return header;
    }

    /// <summary>File ID = MD5(MD5-16k ‖ length (8 bytes LE) ‖ name bytes), as in par2cmdline.</summary>
    public static Md5Hash ComputeFileId(Md5Hash hash16k, long length, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var buf = new byte[24 + nameBytes.Length];
        hash16k.WriteTo(buf);
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(16), (ulong)length);
        nameBytes.CopyTo(buf, 24);
        return Md5Hash.Compute(buf);
    }
}
