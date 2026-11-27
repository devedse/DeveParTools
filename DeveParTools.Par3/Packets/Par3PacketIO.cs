using System.Buffers.Binary;
using System.Text;
using DeveParTools.Core.Hashing;
using Microsoft.Win32.SafeHandles;

namespace DeveParTools.Par3.Packets;

/// <summary>Serialises PAR3 packets. The checksum covers everything from the length field to the end of the body.</summary>
public static class Par3PacketWriter
{
    public static byte[] Build(InputSetId setId, ulong type, ReadOnlySpan<byte> body)
    {
        var packet = new byte[Par3PacketTypes.HeaderSize + body.Length];
        Par3PacketTypes.Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24), (ulong)packet.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(32), setId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(40), type);
        body.CopyTo(packet.AsSpan(48));
        Blake3.Hash(packet.AsSpan(24), packet.AsSpan(8, 16));
        return packet;
    }

    /// <summary>The checksum of a built packet (bytes 8..24).</summary>
    public static Fingerprint ChecksumOf(ReadOnlySpan<byte> packet) => Fingerprint.FromBytes(packet[8..]);

    /// <summary>
    /// Start packet body in the current layout used by par3cmdline: parent InputSetID (8) + parent Root checksum (16)
    /// + block size (8) + field size (1) + generator. (The older draft also stored the 8-byte unique number first;
    /// the reader accepts both.)
    /// </summary>
    public static byte[] StartBody(InputSetId parent, Fingerprint parentRoot, ulong blockSize, ReadOnlySpan<byte> generator)
    {
        var b = new byte[33 + generator.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(b, parent.Value);
        parentRoot.WriteTo(b.AsSpan(8));
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), blockSize);
        b[32] = (byte)generator.Length;
        generator.CopyTo(b.AsSpan(33));
        return b;
    }

    /// <summary>InputSetID = first 8 bytes of BLAKE3(unique number ‖ Start packet body), as par3cmdline computes it.</summary>
    public static InputSetId ComputeSetId(ulong uniqueNumber, ReadOnlySpan<byte> startBody)
    {
        var hasher = new Blake3Hasher();
        hasher.Update(BitConverter.GetBytes(uniqueNumber));
        hasher.Update(startBody);
        Span<byte> h = stackalloc byte[8];
        hasher.Finalize(h);
        return InputSetId.FromBytes(h);
    }

    public static byte[] Text(InputSetId setId, ulong type, string text) => Build(setId, type, Encoding.UTF8.GetBytes(text));

    public static byte[] ExternalData(InputSetId setId, ulong firstBlock, IReadOnlyList<BlockChecksum> checksums)
    {
        var b = new byte[8 + 24 * checksums.Count];
        BinaryPrimitives.WriteUInt64LittleEndian(b, firstBlock);
        for (int i = 0; i < checksums.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8 + 24 * i), checksums[i].Crc64);
            checksums[i].Hash.WriteTo(b.AsSpan(16 + 24 * i));
        }

        return Build(setId, Par3PacketTypes.ExternalData, b);
    }

    public static byte[] Cauchy(InputSetId setId, ulong first, ulong lastPlus1, ulong hint)
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(b, first);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), lastPlus1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), hint);
        return Build(setId, Par3PacketTypes.CauchyMatrix, b);
    }

    public static byte[] Sparse(InputSetId setId, ulong first, ulong lastPlus1, ulong maxRecovery, ulong nonZero, ulong seed)
    {
        var b = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(b, first);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), lastPlus1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), maxRecovery);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), nonZero);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), seed);
        return Build(setId, Par3PacketTypes.SparseMatrix, b);
    }

    public static byte[] Explicit(InputSetId setId, IReadOnlyList<(ulong Index, byte[] Value)> entries)
    {
        var body = new List<byte>();
        Span<byte> b = stackalloc byte[8];
        foreach (var (index, value) in entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b, index);
            body.AddRange(b.ToArray());
            body.AddRange(value);
        }

        return Build(setId, Par3PacketTypes.ExplicitMatrix, body.ToArray());
    }

    /// <summary>
    /// par3cmdline-compatible FFT matrix packet: first (8) + last+1 (8) + log2(max recovery per cohort) (1) +
    /// interleave (0..4 bytes, as small as possible).
    /// </summary>
    public static byte[] Fft(InputSetId setId, ulong first, ulong lastPlus1, byte maxRecoveryLog2, uint interleave)
    {
        int extra = interleave == 0 ? 0 : interleave < 256 ? 1 : interleave < 65536 ? 2 : interleave < 16777216 ? 3 : 4;
        var b = new byte[17 + extra];
        BinaryPrimitives.WriteUInt64LittleEndian(b, first);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), lastPlus1);
        b[16] = maxRecoveryLog2;
        for (int i = 0; i < extra; i++)
        {
            b[17 + i] = (byte)(interleave >> (8 * i));
        }

        return Build(setId, Par3PacketTypes.FftMatrix, b);
    }

    /// <summary>Header + fixed fields of a recovery data packet with a placeholder checksum; data is appended by the caller.</summary>
    public static byte[] RecoveryDataHeader(InputSetId setId, Fingerprint root, Fingerprint matrix, ulong index, long dataLength)
    {
        var h = new byte[Par3PacketTypes.HeaderSize + 40];
        Par3PacketTypes.Magic.CopyTo(h, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(24), (ulong)(h.Length + dataLength));
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), setId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), Par3PacketTypes.RecoveryData);
        root.WriteTo(h.AsSpan(48));
        matrix.WriteTo(h.AsSpan(64));
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(80), index);
        return h;
    }

    /// <summary>Header + index of a data packet with a placeholder checksum; data is appended by the caller.</summary>
    public static byte[] DataHeader(InputSetId setId, ulong blockIndex, long dataLength)
    {
        var h = new byte[Par3PacketTypes.HeaderSize + 8];
        Par3PacketTypes.Magic.CopyTo(h, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(24), (ulong)(h.Length + dataLength));
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), setId.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), Par3PacketTypes.Data);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(48), blockIndex);
        return h;
    }

    public static byte[] File(InputSetId setId, string name, ulong hash16k, Fingerprint fileHash, IReadOnlyList<Fingerprint> options, IReadOnlyList<Par3Chunk> chunks, ulong blockSize)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var body = new List<byte>();
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)nameBytes.Length);
        body.AddRange(b[..2].ToArray());
        body.AddRange(nameBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(b, hash16k);
        body.AddRange(b.ToArray());
        body.AddRange(fileHash.ToArray());
        body.Add((byte)options.Count);
        foreach (var o in options)
        {
            body.AddRange(o.ToArray());
        }

        foreach (var c in chunks)
        {
            c.WriteTo(body, blockSize);
        }

        return Build(setId, Par3PacketTypes.File, body.ToArray());
    }

    public static byte[] Directory(InputSetId setId, string name, IReadOnlyList<Fingerprint> options, IReadOnlyList<Fingerprint> children)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var body = new List<byte>();
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)nameBytes.Length);
        body.AddRange(b[..2].ToArray());
        body.AddRange(nameBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)options.Count);
        body.AddRange(b.ToArray());
        foreach (var o in options.OrderBy(o => o.Value))
        {
            body.AddRange(o.ToArray());
        }

        foreach (var c in children.OrderBy(c => c.Value))
        {
            body.AddRange(c.ToArray());
        }

        return Build(setId, Par3PacketTypes.Directory, body.ToArray());
    }

    public static byte[] Root(InputSetId setId, ulong lowestUnused, bool absolute, IReadOnlyList<Fingerprint> options, IReadOnlyList<Fingerprint> children)
    {
        var body = new List<byte>();
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, lowestUnused);
        body.AddRange(b.ToArray());
        body.Add(absolute ? (byte)1 : (byte)0);
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)options.Count);
        body.AddRange(b[..4].ToArray());
        foreach (var o in options.OrderBy(o => o.Value))
        {
            body.AddRange(o.ToArray());
        }

        foreach (var c in children.OrderBy(c => c.Value))
        {
            body.AddRange(c.ToArray());
        }

        return Build(setId, Par3PacketTypes.Root, body.ToArray());
    }

    public static byte[] Link(InputSetId setId, bool symbolic, string name, IReadOnlyList<string> path, bool absolute, IReadOnlyList<Fingerprint> options)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var pathBytes = Encoding.UTF8.GetBytes((absolute ? "\0" : string.Empty) + string.Join('\0', path));
        var body = new List<byte> { symbolic ? (byte)1 : (byte)0 };
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)nameBytes.Length);
        body.AddRange(b[..2].ToArray());
        body.AddRange(nameBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)pathBytes.Length);
        body.AddRange(b.ToArray());
        body.AddRange(pathBytes);
        body.Add((byte)options.Count);
        foreach (var o in options)
        {
            body.AddRange(o.ToArray());
        }

        return Build(setId, Par3PacketTypes.Link, body.ToArray());
    }

    public static byte[] UnixPermissions(InputSetId setId, long atime, long ctime, long mtime, uint uid, uint gid, ushort mode, string owner, string group, IReadOnlyList<(string Name, string Value)>? xattrs = null)
    {
        var body = new List<byte>();
        Span<byte> b = stackalloc byte[8];
        foreach (var t in new[] { atime, ctime, mtime })
        {
            BinaryPrimitives.WriteInt64LittleEndian(b, t);
            body.AddRange(b.ToArray());
        }

        BinaryPrimitives.WriteUInt32LittleEndian(b, uid);
        body.AddRange(b[..4].ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(b, gid);
        body.AddRange(b[..4].ToArray());
        BinaryPrimitives.WriteUInt16LittleEndian(b, mode);
        body.AddRange(b[..2].ToArray());
        var ownerBytes = Encoding.UTF8.GetBytes(owner);
        var groupBytes = Encoding.UTF8.GetBytes(group);
        body.Add((byte)ownerBytes.Length);
        body.AddRange(ownerBytes);
        body.Add((byte)groupBytes.Length);
        body.AddRange(groupBytes);
        foreach (var (n, v) in xattrs ?? [])
        {
            var nb = Encoding.UTF8.GetBytes(n);
            var vb = Encoding.UTF8.GetBytes(v);
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)nb.Length);
            body.AddRange(b[..2].ToArray());
            body.AddRange(nb);
            BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)vb.Length);
            body.AddRange(b[..2].ToArray());
            body.AddRange(vb);
        }

        return Build(setId, Par3PacketTypes.UnixPermissions, body.ToArray());
    }

    public static byte[] FatPermissions(InputSetId setId, ulong creation, ulong access, ulong write, ushort attributes)
    {
        var b = new byte[26];
        BinaryPrimitives.WriteUInt64LittleEndian(b, creation);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), access);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), write);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(24), attributes);
        return Build(setId, Par3PacketTypes.FatPermissions, b);
    }
}

public sealed class Par3FileReadResult
{
    public required string Path { get; init; }

    public List<Par3Packet> Packets { get; } = new();

    public int DamagedPackets { get; set; }
}

/// <summary>Reads PAR3 packets from any file (also from inside other files, e.g. "PAR inside ZIP").</summary>
public static class Par3PacketReader
{
    private const long MaxMetadataPacket = 256L << 20;

    public static Par3FileReadResult ReadFile(string path, CancellationToken cancellationToken = default)
    {
        var result = new Par3FileReadResult { Path = path };
        using var handle = System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        long fileLen = RandomAccess.GetLength(handle);
        long pos = 0;
        Span<byte> header = stackalloc byte[Par3PacketTypes.HeaderSize];
        while (pos + Par3PacketTypes.HeaderSize <= fileLen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadExactly(handle, pos, header);
            if (header[..8].SequenceEqual(Par3PacketTypes.Magic))
            {
                ulong length = BinaryPrimitives.ReadUInt64LittleEndian(header[24..]);
                if (length >= Par3PacketTypes.HeaderSize && (long)length <= fileLen - pos)
                {
                    var packet = TryRead(handle, path, pos, (long)length, header);
                    if (packet != null)
                    {
                        result.Packets.Add(packet);
                        pos += (long)length;
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

    private static Par3Packet? TryRead(SafeFileHandle handle, string path, long pos, long length, ReadOnlySpan<byte> header)
    {
        ulong type = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
        bool isBulk = type == Par3PacketTypes.Data || type == Par3PacketTypes.RecoveryData;
        long bodyLen = length - Par3PacketTypes.HeaderSize;
        if (!isBulk && bodyLen > MaxMetadataPacket)
        {
            return null;
        }

        var hasher = new Blake3Hasher();
        hasher.Update(header[24..]);
        byte[] body;
        if (isBulk)
        {
            int fixedLen = type == Par3PacketTypes.Data ? 8 : 40;
            if (bodyLen < fixedLen)
            {
                return null;
            }

            body = new byte[fixedLen];
            var buf = new byte[(int)Math.Min(bodyLen, 1 << 20)];
            long off = pos + Par3PacketTypes.HeaderSize;
            long left = bodyLen;
            bool first = true;
            while (left > 0)
            {
                int n = (int)Math.Min(left, buf.Length);
                ReadExactly(handle, off, buf.AsSpan(0, n));
                if (first)
                {
                    buf.AsSpan(0, fixedLen).CopyTo(body);
                    first = false;
                }

                hasher.Update(buf.AsSpan(0, n));
                off += n;
                left -= n;
            }
        }
        else
        {
            body = new byte[bodyLen];
            ReadExactly(handle, pos + Par3PacketTypes.HeaderSize, body);
            hasher.Update(body);
        }

        Span<byte> actual = stackalloc byte[16];
        hasher.Finalize(actual);
        if (!actual.SequenceEqual(header.Slice(8, 16)))
        {
            return null;
        }

        try
        {
            var packet = Parse(type, body, pos + Par3PacketTypes.HeaderSize, bodyLen);
            return packet == null ? null : packet with
            {
                SetId = InputSetId.FromBytes(header[32..]),
                Checksum = Fingerprint.FromBytes(header[8..]),
                SourceFile = path,
                SourceOffset = pos,
            };
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Parse a body. <paramref name="bodyOffset"/>/<paramref name="bodyLength"/> locate bulk data in the file.</summary>
    internal static Par3Packet? Parse(ulong type, byte[] body, long bodyOffset, long bodyLength)
    {
        ReadOnlySpan<byte> s = body;
        switch (type)
        {
            case Par3PacketTypes.Creator:
                return new Par3CreatorPacket(Encoding.UTF8.GetString(s));
            case Par3PacketTypes.Comment:
                return new Par3CommentPacket(Encoding.UTF8.GetString(s));
            case Par3PacketTypes.Start:
            {
                // Current layout (par3cmdline): parent (8) + parent root (16) + block size (8) + gf size (1) + generator.
                if (s.Length >= 33 && s.Length == 33 + s[32])
                {
                    return new Par3StartPacket(0, new InputSetId(U64(s, 0)), Fingerprint.FromBytes(s[8..]), U64(s, 24), s.Slice(33, s[32]).ToArray());
                }

                // Older draft layout with the unique number first.
                Need(s, 41);
                int gf = s[40];
                if (s.Length != 41 + gf)
                {
                    return null;
                }

                return new Par3StartPacket(U64(s, 0), new InputSetId(U64(s, 8)), Fingerprint.FromBytes(s[16..]), U64(s, 32), s.Slice(41, gf).ToArray());
            }

            case Par3PacketTypes.Data:
                return new Par3DataPacket(U64(s, 0), bodyOffset + 8, bodyLength - 8);
            case Par3PacketTypes.ExternalData:
            {
                Need(s, 8);
                if ((s.Length - 8) % 24 != 0)
                {
                    return null;
                }

                return new Par3ExternalDataPacket(U64(s, 0), ReadChecksums(s[8..]));
            }

            case Par3PacketTypes.CauchyMatrix:
                Need(s, 24);
                return new Par3CauchyMatrixPacket(U64(s, 0), U64(s, 8), U64(s, 16));
            case Par3PacketTypes.SparseMatrix:
                Need(s, 40);
                return new Par3SparseMatrixPacket(U64(s, 0), U64(s, 8), U64(s, 16), U64(s, 24), U64(s, 32));
            case Par3PacketTypes.ExplicitMatrix:
                return new Par3ExplicitMatrixPacket(body);
            case Par3PacketTypes.FftMatrix:
            {
                Need(s, 17);
                uint inter = 0;
                for (int i = 17; i < s.Length && i < 21; i++)
                {
                    inter |= (uint)s[i] << (8 * (i - 17));
                }

                return new Par3FftMatrixPacket(U64(s, 0), U64(s, 8), s[16], inter);
            }

            case Par3PacketTypes.RecoveryData:
                return new Par3RecoveryDataPacket(Fingerprint.FromBytes(s), Fingerprint.FromBytes(s[16..]), U64(s, 32), bodyOffset + 40, bodyLength - 40);
            case Par3PacketTypes.RecoveryExternalData:
            {
                Need(s, 40);
                if ((s.Length - 40) % 24 != 0)
                {
                    return null;
                }

                return new Par3RecoveryExternalDataPacket(Fingerprint.FromBytes(s), Fingerprint.FromBytes(s[16..]), U64(s, 32), ReadChecksums(s[40..]));
            }

            case Par3PacketTypes.File:
            {
                Need(s, 2);
                int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s);
                Need(s, 2 + nameLen + 25);
                string name = Encoding.UTF8.GetString(s.Slice(2, nameLen));
                int p = 2 + nameLen;
                ulong crc = U64(s, p);
                var hash = Fingerprint.FromBytes(s[(p + 8)..]);
                int optCount = s[p + 24];
                p += 25;
                Need(s, p + 16 * optCount);
                var options = ReadFingerprints(s.Slice(p, 16 * optCount));
                p += 16 * optCount;
                return new Par3FilePacket(name, crc, hash, options, s[p..].ToArray());
            }

            case Par3PacketTypes.Directory:
            {
                Need(s, 2);
                int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s);
                Need(s, 2 + nameLen + 4);
                string name = Encoding.UTF8.GetString(s.Slice(2, nameLen));
                int p = 2 + nameLen;
                int optCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);
                p += 4;
                Need(s, p + 16 * optCount);
                if ((s.Length - p - 16 * optCount) % 16 != 0)
                {
                    return null;
                }

                return new Par3DirectoryPacket(name, ReadFingerprints(s.Slice(p, 16 * optCount)), ReadFingerprints(s[(p + 16 * optCount)..]));
            }

            case Par3PacketTypes.Root:
            {
                Need(s, 13);
                int optCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[9..]);
                Need(s, 13 + 16 * optCount);
                if ((s.Length - 13 - 16 * optCount) % 16 != 0)
                {
                    return null;
                }

                return new Par3RootPacket(U64(s, 0), s[8], ReadFingerprints(s.Slice(13, 16 * optCount)), ReadFingerprints(s[(13 + 16 * optCount)..]));
            }

            case Par3PacketTypes.Link:
            {
                Need(s, 3);
                bool symbolic = (s[0] & 1) != 0;
                int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s[1..]);
                Need(s, 3 + nameLen + 4);
                string name = Encoding.UTF8.GetString(s.Slice(3, nameLen));
                int p = 3 + nameLen;
                int pathLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[p..]);
                p += 4;
                Need(s, p + pathLen + 1);
                var pathStr = Encoding.UTF8.GetString(s.Slice(p, pathLen));
                p += pathLen;
                bool absolute = pathStr.StartsWith('\0');
                var parts = (absolute ? pathStr[1..] : pathStr).Split('\0');
                int optCount = s[p];
                p++;
                Need(s, p + 16 * optCount);
                return new Par3LinkPacket(symbolic, name, parts, absolute, ReadFingerprints(s.Slice(p, 16 * optCount)));
            }

            case Par3PacketTypes.UnixPermissions:
            {
                Need(s, 35);
                long at = BinaryPrimitives.ReadInt64LittleEndian(s), ct = BinaryPrimitives.ReadInt64LittleEndian(s[8..]), mt = BinaryPrimitives.ReadInt64LittleEndian(s[16..]);
                uint uid = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]), gid = BinaryPrimitives.ReadUInt32LittleEndian(s[28..]);
                ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(s[32..]);
                int p = 34;
                int ol = s[p++];
                Need(s, p + ol + 1);
                string owner = Encoding.UTF8.GetString(s.Slice(p, ol));
                p += ol;
                int gl = s[p++];
                Need(s, p + gl);
                string group = Encoding.UTF8.GetString(s.Slice(p, gl));
                p += gl;
                var xattrs = new List<(string, string)>();
                while (p < s.Length)
                {
                    Need(s, p + 2);
                    int nl = BinaryPrimitives.ReadUInt16LittleEndian(s[p..]);
                    p += 2;
                    Need(s, p + nl + 2);
                    string n = Encoding.UTF8.GetString(s.Slice(p, nl));
                    p += nl;
                    int vl = BinaryPrimitives.ReadUInt16LittleEndian(s[p..]);
                    p += 2;
                    Need(s, p + vl);
                    xattrs.Add((n, Encoding.UTF8.GetString(s.Slice(p, vl))));
                    p += vl;
                }

                return new Par3UnixPermissionsPacket(at, ct, mt, uid, gid, mode, owner, group, xattrs);
            }

            case Par3PacketTypes.FatPermissions:
                Need(s, 26);
                return new Par3FatPermissionsPacket(U64(s, 0), U64(s, 8), U64(s, 16), BinaryPrimitives.ReadUInt16LittleEndian(s[24..]));
            default:
                return null;
        }
    }

    private static void Need(ReadOnlySpan<byte> s, int n)
    {
        if (s.Length < n)
        {
            throw new InvalidDataException("Packet body too short.");
        }
    }

    private static ulong U64(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(s[offset..]);

    private static List<Fingerprint> ReadFingerprints(ReadOnlySpan<byte> s)
    {
        var list = new List<Fingerprint>(s.Length / 16);
        for (int i = 0; i + 16 <= s.Length; i += 16)
        {
            list.Add(Fingerprint.FromBytes(s[i..]));
        }

        return list;
    }

    private static List<BlockChecksum> ReadChecksums(ReadOnlySpan<byte> s)
    {
        var list = new List<BlockChecksum>(s.Length / 24);
        for (int i = 0; i + 24 <= s.Length; i += 24)
        {
            list.Add(new BlockChecksum(U64(s, i), Fingerprint.FromBytes(s[(i + 8)..])));
        }

        return list;
    }

    private static long FindMagic(SafeFileHandle handle, long start, long fileLen)
    {
        var buf = new byte[(1 << 20) + 7];
        long pos = start;
        while (pos < fileLen)
        {
            int want = (int)Math.Min(buf.Length, fileLen - pos);
            int got = RandomAccess.Read(handle, buf.AsSpan(0, want), pos);
            if (got <= 0)
            {
                return -1;
            }

            int idx = buf.AsSpan(0, got).IndexOf(Par3PacketTypes.Magic);
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
