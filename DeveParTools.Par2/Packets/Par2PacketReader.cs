using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeveParTools.Core.IO;
using Microsoft.Win32.SafeHandles;

namespace DeveParTools.Par2.Packets;

/// <summary>Result of reading one .par2 file.</summary>
public sealed class Par2FileReadResult
{
    public required string Path { get; init; }

    public List<Par2Packet> Packets { get; } = new();

    /// <summary>
    /// Number of byte ranges that looked like packets but could not be used: a failed checksum, an impossible
    /// length, or a body that does not fit its packet type.
    /// </summary>
    public int DamagedPackets { get; set; }
}

/// <summary>
/// Reads packets from a PAR2 file. Damaged files are handled by searching for the next magic sequence, so every
/// intact packet is recovered even when parts of the file are corrupt.
/// </summary>
/// <remarks>
/// <para>The file is walked packet by packet. A packet whose MD5 matches is trusted, including its length field, so
/// the walk jumps over its whole body: packets of unknown types (e.g. from newer clients) are ignored that way, as
/// the specification requires, and so is a packet of a known type whose body cannot be parsed (it is counted in
/// <see cref="Par2FileReadResult.DamagedPackets"/>).</para>
/// <para>Anything else (garbage, a damaged packet, a truncated packet at the end) is skipped by searching for the
/// next magic sequence from the following byte on, like par2cmdline does:</para>
/// <code>
/// [packet A ok][packet B, damaged    ][junk][packet C ok]
///  ^ jump      ^ MD5 fails → search from B+1 ...  ^ found magic
/// </code>
/// </remarks>
public static class Par2PacketReader
{
    private const int SearchChunk = 1 << 20;
    private const long MaxNonDataPacket = 64L << 20;

    private enum ReadOutcome
    {
        /// <summary>MD5 ok and parsed.</summary>
        Packet,

        /// <summary>MD5 ok, but the type is unknown: skipped.</summary>
        Unknown,

        /// <summary>MD5 ok, but the body does not fit the type's layout.</summary>
        Malformed,

        /// <summary>MD5 mismatch, or too large to be a non-recovery packet.</summary>
        Damaged,
    }

    /// <param name="reached">Called with how far into the file reading has got (about every 4 MiB, and at the end), for progress.</param>
    public static Par2FileReadResult ReadFile(string path, CancellationToken cancellationToken = default, Action<long>? reached = null)
    {
        var result = new Par2FileReadResult { Path = path };
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        long fileLen = RandomAccess.GetLength(handle);
        long pos = 0;
        Span<byte> header = stackalloc byte[Par2PacketTypes.HeaderSize];
        long reported = 0;
        while (pos + Par2PacketTypes.HeaderSize <= fileLen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pos - reported >= 4 << 20)
            {
                reached?.Invoke(pos);
                reported = pos;
            }

            FileHandleCache.ReadPadded(handle, pos, header);
            if (header[..8].SequenceEqual(Par2PacketTypes.Magic))
            {
                // Compared as unsigned and against the bytes left: a damaged length of 2^63 or more must neither turn
                // negative nor overflow "pos + length".
                ulong length = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
                Par2Packet? packet = null;
                var outcome = length >= Par2PacketTypes.HeaderSize && length % 4 == 0 && length <= (ulong)(fileLen - pos)
                    ? TryReadPacket(handle, path, pos, (long)length, header, out packet)
                    : ReadOutcome.Damaged;
                if (packet != null)
                {
                    result.Packets.Add(packet);
                }
                else if (outcome != ReadOutcome.Unknown)
                {
                    result.DamagedPackets++;
                }

                if (outcome != ReadOutcome.Damaged)
                {
                    pos += (long)length;
                    continue;
                }
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

    private static ReadOutcome TryReadPacket(SafeFileHandle handle, string path, long pos, long length, ReadOnlySpan<byte> header, out Par2Packet? packet)
    {
        packet = null;
        var expectedMd5 = header.Slice(16, 16);
        var setId = Md5Hash.FromBytes(header.Slice(32, 16));
        var type = header.Slice(48, 16);
        long bodyLen = length - Par2PacketTypes.HeaderSize;

        bool isRecovery = type.SequenceEqual(Par2PacketTypes.RecoverySlice);
        if (!isRecovery && bodyLen > MaxNonDataPacket)
        {
            return ReadOutcome.Damaged;
        }

        // Verify the packet MD5 over [setId .. end of body], streaming for (large) recovery packets, whose data
        // stays on disk.
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(header[32..]);
        byte[]? body = null;
        if (!isRecovery)
        {
            body = new byte[bodyLen];
            FileHandleCache.ReadPadded(handle, pos + Par2PacketTypes.HeaderSize, body);
            md5.AppendData(body);
        }
        else
        {
            var buf = new byte[(int)Math.Min(bodyLen, SearchChunk)];
            long off = pos + Par2PacketTypes.HeaderSize;
            for (long left = bodyLen; left > 0;)
            {
                int n = (int)Math.Min(left, buf.Length);
                FileHandleCache.ReadPadded(handle, off, buf.AsSpan(0, n));
                md5.AppendData(buf.AsSpan(0, n));
                off += n;
                left -= n;
            }
        }

        Span<byte> actual = stackalloc byte[16];
        md5.GetHashAndReset(actual);
        if (!actual.SequenceEqual(expectedMd5))
        {
            return ReadOutcome.Damaged;
        }

        if (isRecovery)
        {
            if (bodyLen < 4)
            {
                return ReadOutcome.Malformed;
            }

            Span<byte> exp = stackalloc byte[4];
            FileHandleCache.ReadPadded(handle, pos + Par2PacketTypes.HeaderSize, exp);
            packet = new Par2RecoverySlicePacket(setId, BinaryPrimitives.ReadUInt32LittleEndian(exp), path, pos + Par2PacketTypes.HeaderSize + 4, bodyLen - 4);
            return ReadOutcome.Packet;
        }

        if (!IsKnownType(type))
        {
            return ReadOutcome.Unknown;
        }

        packet = Parse(setId, type, body!);
        return packet != null ? ReadOutcome.Packet : ReadOutcome.Malformed;
    }

    private static bool IsKnownType(ReadOnlySpan<byte> type)
        => type.SequenceEqual(Par2PacketTypes.Main) || type.SequenceEqual(Par2PacketTypes.FileDescription) || type.SequenceEqual(Par2PacketTypes.SliceChecksum)
           || type.SequenceEqual(Par2PacketTypes.Creator) || type.SequenceEqual(Par2PacketTypes.UnicodeFilename) || type.SequenceEqual(Par2PacketTypes.AsciiComment)
           || type.SequenceEqual(Par2PacketTypes.UnicodeComment);

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

    /// <summary>
    /// Offset of the next magic sequence at or after <paramref name="start"/>, or -1. The file is searched in chunks
    /// that overlap by 7 bytes, so a magic sequence across a chunk boundary is found as well.
    /// </summary>
    private static long FindMagic(SafeFileHandle handle, long start, long fileLen)
    {
        var buf = new byte[SearchChunk + 7];
        for (long pos = start; ; )
        {
            int got = FileHandleCache.ReadPadded(handle, pos, buf.AsSpan(0, (int)Math.Clamp(fileLen - pos, 0, buf.Length)));
            int idx = buf.AsSpan(0, got).IndexOf(Par2PacketTypes.Magic);
            if (idx >= 0)
            {
                return pos + idx;
            }

            if (got < buf.Length)
            {
                // That was the end of the file.
                return -1;
            }

            pos += got - 7;
        }
    }
}
