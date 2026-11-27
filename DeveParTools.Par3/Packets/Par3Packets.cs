using System.Buffers.Binary;
using System.Text;
using DeveParTools.Core.Hashing;

namespace DeveParTools.Par3.Packets;

/// <summary>A PAR3 "fingerprint hash": the first 16 bytes of a BLAKE3 hash.</summary>
public readonly record struct Fingerprint(UInt128 Value)
{
    public static Fingerprint Zero => default;

    public bool IsZero => Value == UInt128.Zero;

    public static Fingerprint FromBytes(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt128LittleEndian(bytes));

    public static Fingerprint Compute(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[16];
        Blake3.Hash(data, h);
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
    /// Sort key for the "numerical order" of checksums in Directory and Root packets: the 16 bytes compared like
    /// <c>memcmp</c> (first byte most significant), as par3cmdline sorts them. <see cref="Value"/> is the
    /// little-endian reading of the bytes, so reversing its byte order gives that big-endian order.
    /// </summary>
    /// <example>Bytes <c>01 00 .. 00</c> sort after <c>00 FF .. FF</c>, although their little-endian values are 1 and 2^128 − 256.</example>
    public UInt128 SortKey => BinaryPrimitives.ReverseEndianness(Value);

    public override string ToString() => Convert.ToHexStringLower(ToArray());
}

/// <summary>8-byte InputSetID.</summary>
public readonly record struct InputSetId(ulong Value)
{
    public static InputSetId FromBytes(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt64LittleEndian(bytes));

    public override string ToString() => Value.ToString("x16");
}

public static class Par3PacketTypes
{
    public static readonly byte[] Magic = "PAR3\0PKT"u8.ToArray();

    /// <summary>magic (8) + checksum (16) + length (8) + InputSetID (8) + type (8).</summary>
    public const int HeaderSize = 48;

    public const ulong Creator = 0x00455243_20524150; // "PAR CRE\0"
    public const ulong Comment = 0x004D4F43_20524150; // "PAR COM\0"
    public const ulong Start = 0x00415453_20524150; // "PAR STA\0"
    public const ulong Data = 0x00544144_20524150; // "PAR DAT\0"
    public const ulong ExternalData = 0x00545845_20524150; // "PAR EXT\0"
    public const ulong CauchyMatrix = 0x00554143_20524150; // "PAR CAU\0"
    public const ulong SparseMatrix = 0x00415053_20524150; // "PAR SPA\0"
    public const ulong ExplicitMatrix = 0x00505845_20524150; // "PAR EXP\0"
    public const ulong FftMatrix = 0x00544646_20524150; // "PAR FFT\0" (par3cmdline extension)
    public const ulong RecoveryData = 0x00434552_20524150; // "PAR REC\0"
    public const ulong RecoveryExternalData = 0x00445245_20524150; // "PAR ERD\0"
    public const ulong File = 0x004C4946_20524150; // "PAR FIL\0"
    public const ulong Directory = 0x00524944_20524150; // "PAR DIR\0"
    public const ulong Root = 0x004F4F52_20524150; // "PAR ROO\0"
    public const ulong Link = 0x004B4E4C_20524150; // "PAR LNK\0"
    public const ulong UnixPermissions = 0x00584E55_20524150; // "PAR UNX\0"
    public const ulong FatPermissions = 0x00544146_20524150; // "PAR FAT\0"

    public static ulong FromAscii(string eight) => BinaryPrimitives.ReadUInt64LittleEndian(Encoding.ASCII.GetBytes(eight.PadRight(8, '\0')));

    public static string ToAscii(ulong type)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, type);
        return Encoding.ASCII.GetString(b).TrimEnd('\0');
    }
}

/// <summary>Base of every parsed packet: header fields plus the packet's own checksum (used for references).</summary>
public abstract record Par3Packet
{
    public InputSetId SetId { get; init; }

    /// <summary>Checksum from the header: references between packets (Directory → File, ...) use this value.</summary>
    public Fingerprint Checksum { get; init; }

    /// <summary>Where the packet was read from (for diagnostics).</summary>
    public string? SourceFile { get; init; }

    public long SourceOffset { get; init; }
}

public sealed record Par3CreatorPacket(string Text) : Par3Packet;

public sealed record Par3CommentPacket(string Text) : Par3Packet;

public sealed record Par3StartPacket(ulong UniqueNumber, InputSetId ParentSetId, Fingerprint ParentRootChecksum, ulong BlockSize, byte[] GeneratorBytes) : Par3Packet
{
    /// <summary>Galois field size in bytes (0 = plain XOR, a par3cmdline special case).</summary>
    public int GfSize => GeneratorBytes.Length;
}

/// <summary>One input block stored inside the PAR3 file. Data stays on disk.</summary>
public sealed record Par3DataPacket(ulong BlockIndex, long DataOffset, long DataLength) : Par3Packet;

public readonly record struct BlockChecksum(ulong Crc64, Fingerprint Hash);

public sealed record Par3ExternalDataPacket(ulong FirstBlockIndex, IReadOnlyList<BlockChecksum> Checksums) : Par3Packet;

public abstract record Par3MatrixPacket : Par3Packet;

/// <summary>Cauchy matrix over input blocks [First, LastPlus1) (0, 0 = all).</summary>
public sealed record Par3CauchyMatrixPacket(ulong First, ulong LastPlus1, ulong RecoveryHint) : Par3MatrixPacket;

public sealed record Par3SparseMatrixPacket(ulong First, ulong LastPlus1, ulong MaxRecovery, ulong NonZeroPerInput, ulong Seed) : Par3MatrixPacket;

/// <summary>One explicit row: (input index, GF value bytes) pairs. Parsed with the field size of the Start packet.</summary>
public sealed record Par3ExplicitMatrixPacket(byte[] RawBody) : Par3MatrixPacket;

/// <summary>
/// par3cmdline's FFT matrix packet: Leopard-RS code with m = 2^<see cref="MaxRecoveryLog2"/> recovery blocks per
/// cohort and <see cref="Interleave"/> + 1 cohorts.
/// </summary>
public sealed record Par3FftMatrixPacket(ulong First, ulong LastPlus1, byte MaxRecoveryLog2, uint Interleave) : Par3MatrixPacket;

public sealed record Par3RecoveryDataPacket(Fingerprint RootChecksum, Fingerprint MatrixChecksum, ulong RecoveryIndex, long DataOffset, long DataLength) : Par3Packet;

public sealed record Par3RecoveryExternalDataPacket(Fingerprint RootChecksum, Fingerprint MatrixChecksum, ulong FirstRecoveryIndex, IReadOnlyList<BlockChecksum> Checksums) : Par3Packet;

/// <summary>File packet; chunk descriptions need the block size to parse, so the raw body is kept.</summary>
public sealed record Par3FilePacket(string Name, ulong Hash16kCrc, Fingerprint FileHash, IReadOnlyList<Fingerprint> Options, byte[] ChunkBytes) : Par3Packet;

public sealed record Par3DirectoryPacket(string Name, IReadOnlyList<Fingerprint> Options, IReadOnlyList<Fingerprint> Children) : Par3Packet;

public sealed record Par3RootPacket(ulong LowestUnusedBlock, byte Attributes, IReadOnlyList<Fingerprint> Options, IReadOnlyList<Fingerprint> Children) : Par3Packet
{
    public bool IsAbsolute => (Attributes & 1) != 0;
}

public sealed record Par3LinkPacket(bool IsSymbolic, string Name, string[] TargetPath, bool TargetIsAbsolute, IReadOnlyList<Fingerprint> Options) : Par3Packet;

/// <summary>UNIX Permissions packet ("PAR UNX").</summary>
/// <remarks>
/// <para>Times are nanoseconds since 1970-01-01 UTC according to the specification, but par3cmdline writes whole
/// seconds into the same field; <see cref="Par3Metadata.UnixTimeToUtc"/> reads both.</para>
/// <para><see cref="Mode"/> holds the low 12 bits of <c>st_mode</c> (rwx for user/group/other, plus set-uid,
/// set-gid and sticky); the top 4 bits must be zero. par3cmdline writes 0xFFFF when it did not store the mode.</para>
/// </remarks>
public sealed record Par3UnixPermissionsPacket(long Atime, long Ctime, long Mtime, uint Uid, uint Gid, ushort Mode, string Owner, string Group, IReadOnlyList<(string Name, string Value)> Xattrs) : Par3Packet
{
    public const long UnsetTime = -1; // 2^64-1 as signed
    public const uint UnsetId = uint.MaxValue;
    public const ushort DefaultMode = 0x0180;
}

/// <summary>FAT Permissions packet ("PAR FAT").</summary>
/// <remarks>
/// Timestamps are Windows FILETIME values: 100 ns ticks since 1601-01-01 UTC (the specification text says 1600,
/// but par3cmdline and the Windows API it is based on use 1601). <see cref="Attributes"/> may only use the bits
/// ReadOnly (0x1), Hidden (0x2), System (0x4), Archive (0x20) and NotContentIndexed (0x2000); par3cmdline writes
/// 0xFFFF when it did not store them.
/// </remarks>
public sealed record Par3FatPermissionsPacket(ulong Creation, ulong LastAccess, ulong LastWrite, ushort Attributes) : Par3Packet
{
    public const ulong UnsetTime = ulong.MaxValue;

    /// <summary>The attribute bits the specification defines.</summary>
    public const ushort KnownAttributes = 0x2027;
}

/// <summary>
/// A protected or unprotected run of a file. Protected chunks are mapped to input blocks: full blocks start at
/// <see cref="FirstBlock"/>; a remaining tail is either inline (&lt; 40 bytes) or stored at
/// (<see cref="TailBlock"/>, <see cref="TailOffset"/>).
/// </summary>
/// <remarks>
/// Which optional fields follow the length depends only on the length and the block size. Examples with a block
/// size of 1000 (field sizes in bytes in parentheses):
/// <code>
/// 6000 unprotected bytes:              [0 (8)] [6000 (8)]
/// 3000 bytes in blocks 4, 5, 6:        [3000 (8)] [first block 4 (8)]
/// 1010 bytes, 10-byte tail (&lt; 40):     [1010 (8)] [first block (8)] [the 10 tail bytes]
/// 30 bytes (tiny file, inline only):   [30 (8)] [the 30 bytes]
/// 2500 bytes, 500-byte tail stored     [2500 (8)] [first block (8)] [CRC-64 of the tail's first 40 bytes (8)]
///   at offset 300 of block 9:          [BLAKE3 of the tail (16)] [tail block 9 (8)] [tail offset 300 (8)]
/// 700 bytes (one tail, no full block): [700 (8)] [CRC-64 (8)] [BLAKE3 (16)] [tail block (8)] [tail offset (8)]
/// </code>
/// </remarks>
public sealed record Par3Chunk
{
    public bool Protected { get; init; } = true;

    public ulong Length { get; init; }

    public ulong FirstBlock { get; init; }

    public byte[]? InlineTail { get; init; }

    public ulong TailCrc { get; init; }

    public Fingerprint TailHash { get; init; }

    public ulong TailBlock { get; init; }

    public ulong TailOffset { get; init; }

    public ulong FullBlocks(ulong blockSize) => Protected ? Length / blockSize : 0;

    public ulong TailLength(ulong blockSize) => Protected ? Length % blockSize : 0;

    public const int InlineLimit = 40;

    /// <summary>Parse a sequence of chunk descriptions.</summary>
    public static List<Par3Chunk> ParseAll(ReadOnlySpan<byte> bytes, ulong blockSize)
    {
        var list = new List<Par3Chunk>();
        int p = 0;
        while (p < bytes.Length)
        {
            if (p + 8 > bytes.Length)
            {
                throw new InvalidDataException("Truncated chunk description.");
            }

            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(bytes[p..]);
            p += 8;
            if (length == 0)
            {
                if (p + 8 > bytes.Length)
                {
                    throw new InvalidDataException("Truncated unprotected chunk description.");
                }

                list.Add(new Par3Chunk { Protected = false, Length = BinaryPrimitives.ReadUInt64LittleEndian(bytes[p..]) });
                p += 8;
                continue;
            }

            ulong first = 0;
            if (length >= blockSize)
            {
                if (p + 8 > bytes.Length)
                {
                    throw new InvalidDataException("Truncated chunk description.");
                }

                first = BinaryPrimitives.ReadUInt64LittleEndian(bytes[p..]);
                p += 8;
            }

            ulong tail = length % blockSize;
            if (tail >= InlineLimit)
            {
                if (p + 40 > bytes.Length)
                {
                    throw new InvalidDataException("Truncated tail description.");
                }

                list.Add(new Par3Chunk
                {
                    Length = length,
                    FirstBlock = first,
                    TailCrc = BinaryPrimitives.ReadUInt64LittleEndian(bytes[p..]),
                    TailHash = Fingerprint.FromBytes(bytes[(p + 8)..]),
                    TailBlock = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(p + 24)..]),
                    TailOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(p + 32)..]),
                });
                p += 40;
            }
            else if (tail > 0)
            {
                if (p + (int)tail > bytes.Length)
                {
                    throw new InvalidDataException("Truncated inline tail.");
                }

                list.Add(new Par3Chunk { Length = length, FirstBlock = first, InlineTail = bytes.Slice(p, (int)tail).ToArray() });
                p += (int)tail;
            }
            else
            {
                list.Add(new Par3Chunk { Length = length, FirstBlock = first });
            }
        }

        return list;
    }

    public void WriteTo(List<byte> output, ulong blockSize)
    {
        Span<byte> b = stackalloc byte[8];
        if (!Protected)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b, 0);
            output.AddRange(b.ToArray());
            BinaryPrimitives.WriteUInt64LittleEndian(b, Length);
            output.AddRange(b.ToArray());
            return;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(b, Length);
        output.AddRange(b.ToArray());
        if (Length >= blockSize)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b, FirstBlock);
            output.AddRange(b.ToArray());
        }

        ulong tail = Length % blockSize;
        if (tail >= InlineLimit)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b, TailCrc);
            output.AddRange(b.ToArray());
            output.AddRange(TailHash.ToArray());
            BinaryPrimitives.WriteUInt64LittleEndian(b, TailBlock);
            output.AddRange(b.ToArray());
            BinaryPrimitives.WriteUInt64LittleEndian(b, TailOffset);
            output.AddRange(b.ToArray());
        }
        else if (tail > 0)
        {
            output.AddRange(InlineTail!);
        }
    }
}
