using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeveParTools.Par2.Packets;

/// <summary>Result of reading one .par2 file.</summary>
public sealed class Par2FileReadResult
{
    public required string Path { get; init; }

    public List<Par2Packet> Packets { get; } = new();

    /// <summary>Number of byte ranges that looked like packets but failed their checksum.</summary>
    public int DamagedPackets { get; set; }
}

/// <summary>
/// Reads packets from a PAR2 file. Damaged files are handled by searching for the next magic sequence, so every
/// intact packet is recovered even when parts of the file are corrupt.
/// </summary>
public static class Par2PacketReader
{
    private const int SearchChunk = 1 << 20;
    private const long MaxNonDataPacket = 64L << 20;

    public static Par2FileReadResult ReadFile(string path, CancellationToken cancellationToken = default)
    {
        var result = new Par2FileReadResult { Path = path };
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        long fileLen = RandomAccess.GetLength(handle);
        long pos = 0;
        Span<byte> header = stackalloc byte[Par2PacketTypes.HeaderSize];
        while (pos + Par2PacketTypes.HeaderSize <= fileLen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadExactly(handle, pos, header);
            if (header[..8].SequenceEqual(Par2PacketTypes.Magic))
            {
                long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
                if (length >= Par2PacketTypes.HeaderSize && length % 4 == 0 && pos + length <= fileLen)
                {
                    var packet = TryReadPacket(handle, path, pos, length, header);
                    if (packet != null)
                    {
                        result.Packets.Add(packet);
                        pos += length;
                        continue;
                    }
                }

                result.DamagedPackets++;
            }

            long next = FindMagic(handle, pos + 1, fileLen);
            if (next < 0)
            {
                break;
            }

            pos = next;
        }

        return result;
    }

    private static Par2Packet? TryReadPacket(SafeFileHandle handle, string path, long pos, long length, ReadOnlySpan<byte> header)
    {
        var expectedMd5 = header.Slice(16, 16);
        var setId = Md5Hash.FromBytes(header.Slice(32, 16));
        var type = header.Slice(48, 16);
        long bodyLen = length - Par2PacketTypes.HeaderSize;

        bool isRecovery = type.SequenceEqual(Par2PacketTypes.RecoverySlice);
        if (!isRecovery && bodyLen > MaxNonDataPacket)
        {
            return null;
        }

        // Verify the packet MD5 over [setId .. end of body], streaming for large packets.
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(header[32..]);
        byte[]? body = null;
        if (!isRecovery)
        {
            body = new byte[bodyLen];
            ReadExactly(handle, pos + Par2PacketTypes.HeaderSize, body);
            md5.AppendData(body);
        }
        else
        {
            var buf = new byte[(int)Math.Min(bodyLen, 1 << 20)];
            long off = pos + Par2PacketTypes.HeaderSize;
            long left = bodyLen;
            while (left > 0)
            {
                int n = (int)Math.Min(left, buf.Length);
                ReadExactly(handle, off, buf.AsSpan(0, n));
                md5.AppendData(buf.AsSpan(0, n));
                off += n;
                left -= n;
            }
        }

        Span<byte> actual = stackalloc byte[16];
        md5.GetHashAndReset(actual);
        if (!actual.SequenceEqual(expectedMd5))
        {
            return null;
        }

        if (isRecovery)
        {
            if (bodyLen < 4)
            {
                return null;
            }

            Span<byte> exp = stackalloc byte[4];
            ReadExactly(handle, pos + Par2PacketTypes.HeaderSize, exp);
            return new Par2RecoverySlicePacket(setId, BinaryPrimitives.ReadUInt32LittleEndian(exp), path, pos + Par2PacketTypes.HeaderSize + 4, bodyLen - 4);
        }

        return Parse(setId, type, body!);
    }

    /// <summary>Parse the body of a non-recovery packet. Unknown types return null (they are ignored).</summary>
    internal static Par2Packet? Parse(Md5Hash setId, ReadOnlySpan<byte> type, byte[] body)
    {
        if (type.SequenceEqual(Par2PacketTypes.Main))
        {
            if (body.Length < 12)
            {
                return null;
            }

            long sliceSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(body);
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
            int total = (body.Length - 12) / 16;
            if (count > total || sliceSize <= 0 || sliceSize % 4 != 0)
            {
                return null;
            }

            var rec = new List<Md5Hash>(count);
            var non = new List<Md5Hash>(total - count);
            for (int i = 0; i < total; i++)
            {
                var h = Md5Hash.FromBytes(body.AsSpan(12 + 16 * i, 16));
                (i < count ? rec : non).Add(h);
            }

            return new Par2MainPacket(setId, sliceSize, rec, non);
        }

        if (type.SequenceEqual(Par2PacketTypes.FileDescription))
        {
            if (body.Length < 56)
            {
                return null;
            }

            var name = DecodeName(body.AsSpan(56), unicode: false);
            return new Par2FileDescriptionPacket(setId, Md5Hash.FromBytes(body), Md5Hash.FromBytes(body.AsSpan(16)),
                Md5Hash.FromBytes(body.AsSpan(32)), (long)BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(48)), name);
        }

        if (type.SequenceEqual(Par2PacketTypes.SliceChecksum))
        {
            if (body.Length < 16 || (body.Length - 16) % 20 != 0)
            {
                return null;
            }

            int n = (body.Length - 16) / 20;
            var slices = new (Md5Hash, uint)[n];
            for (int i = 0; i < n; i++)
            {
                slices[i] = (Md5Hash.FromBytes(body.AsSpan(16 + 20 * i, 16)), BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(32 + 20 * i)));
            }

            return new Par2SliceChecksumPacket(setId, Md5Hash.FromBytes(body), slices);
        }

        if (type.SequenceEqual(Par2PacketTypes.Creator))
        {
            return new Par2CreatorPacket(setId, DecodeName(body, unicode: false));
        }

        if (type.SequenceEqual(Par2PacketTypes.UnicodeFilename))
        {
            if (body.Length < 16)
            {
                return null;
            }

            return new Par2UnicodeFilenamePacket(setId, Md5Hash.FromBytes(body), DecodeName(body.AsSpan(16), unicode: true));
        }

        if (type.SequenceEqual(Par2PacketTypes.AsciiComment))
        {
            return new Par2CommentPacket(setId, DecodeName(body, unicode: false), false);
        }

        if (type.SequenceEqual(Par2PacketTypes.UnicodeComment))
        {
            if (body.Length < 16)
            {
                return null;
            }

            return new Par2CommentPacket(setId, DecodeName(body.AsSpan(16), unicode: true), true);
        }

        return null;
    }

    /// <summary>Strip zero padding and decode. "ASCII" names are decoded as UTF-8 (what real clients write).</summary>
    internal static string DecodeName(ReadOnlySpan<byte> bytes, bool unicode)
    {
        if (unicode)
        {
            int len = bytes.Length & ~1;
            while (len >= 2 && bytes[len - 1] == 0 && bytes[len - 2] == 0)
            {
                len -= 2;
            }

            return Encoding.Unicode.GetString(bytes[..len]);
        }

        int end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0)
        {
            end--;
        }

        return Encoding.UTF8.GetString(bytes[..end]);
    }

    private static long FindMagic(SafeFileHandle handle, long start, long fileLen)
    {
        var buf = new byte[SearchChunk + 7];
        long pos = start;
        while (pos < fileLen)
        {
            int want = (int)Math.Min(buf.Length, fileLen - pos);
            int got = RandomAccess.Read(handle, buf.AsSpan(0, want), pos);
            if (got <= 0)
            {
                return -1;
            }

            int idx = buf.AsSpan(0, got).IndexOf(Par2PacketTypes.Magic);
            if (idx >= 0)
            {
                return pos + idx;
            }

            if (got < 8)
            {
                return -1;
            }

            pos += got - 7;
        }

        return -1;
    }

    private static void ReadExactly(SafeFileHandle handle, long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = RandomAccess.Read(handle, destination[total..], offset + total);
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }

            total += n;
        }
    }
}
