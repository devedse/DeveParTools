using System.Buffers.Binary;
using System.Security.Cryptography;
using DeveParTools.Core.Hashing;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

/// <summary>Archive formats that can carry their own PAR3 recovery data.</summary>
public enum Par3InsideFormat
{
    Zip,
    SevenZip,
    Rar,
}

/// <summary>Options for <see cref="Par3Inside.Insert"/>.</summary>
public sealed class Par3InsideOptions
{
    /// <summary>The .zip, .7z or .rar file to protect (changed in place).</summary>
    public required string ArchivePath { get; init; }

    /// <summary>Recovery data as a percentage of the input blocks (par3cmdline's default is 1%).</summary>
    public double RedundancyPercent { get; init; } = 1;

    /// <summary>Exact number of recovery blocks instead of <see cref="RedundancyPercent"/>.</summary>
    public int? RecoveryBlockCount { get; init; }

    /// <summary>Block size; by default chosen like par3cmdline (smallest total size).</summary>
    public ulong? BlockSize { get; init; }

    public string Creator { get; init; } = Par3Creator.DefaultCreator;
}

/// <summary>Result of <see cref="Par3Inside.Insert"/>.</summary>
public sealed record Par3InsideResult(Par3InsideFormat Format, long OriginalLength, long NewLength, ulong BlockSize, ulong InputBlocks, int RecoveryBlocks);

/// <summary>
/// "PAR inside": an archive that carries the PAR3 data to repair itself, compatible with par3cmdline's
/// <c>insert</c>, <c>delete</c>, <c>vs</c> (verify itself) and <c>rs</c> (repair itself).
/// </summary>
/// <remarks>
/// <para>The original bytes are never changed; the PAR3 packets are added at the end. A 7z or RAR reader stops at the end
/// of its own data, so for those formats the packets are simply appended:</para>
/// <code>
///   [ original .7z / .rar           ][ PAR3 packets ]
///     protected chunk                  unprotected chunk
/// </code>
/// <para>A ZIP reader starts at the <i>end</i>: it looks for the "end of central directory" record (EOCD) in the last
/// bytes and follows its offset to the central directory. So the ZIP footer (the EOCD, plus the ZIP64 records when
/// present) is copied after the packets:</para>
/// <code>
///   [ ZIP data + central directory ][ footer ][ PAR3 packets ][ footer copy ]
///     protected chunk 1               chunk 2   unprotected     chunk 3 = same blocks as chunk 2
/// </code>
/// <para>The PAR3 set has one File packet named like the archive itself. Its chunk descriptions cover the whole new
/// file: the protected parts point at input blocks, and the packets themselves are an unprotected chunk, which the
/// file hash skips (par3cmdline's rule; see <see cref="Par3FileHasher"/>). Verify and repair then work on the archive like on any other protected
/// file. Repair rebuilds the protected parts and keeps whatever is in the unprotected part. Removing the packets again
/// truncates the file to the protected chunks before the unprotected one, which gives back the original bytes.</para>
/// <para>Not every ZIP reader accepts this layout. Info-ZIP, 7-Zip and par3cmdline do, but readers that check that the
/// central directory ends right where the EOCD starts (Python's zipfile, for example) report the file as damaged. This
/// is inherent to par3cmdline's format.</para>
/// <para>The vital packets (Start, Matrix, External Data, File, Root) are written several times between the recovery
/// packets, as par3cmdline does: <c>[common][recovery…][common][recovery…]…[common][creator]</c>. The number of copies grows with
/// log2(recovery blocks) and is capped by the redundancy.</para>
/// </remarks>
public static class Par3Inside
{
    private const int ZipEocdSignature = 0x06054b50;
    private const int Zip64EocdSignature = 0x06064b50;
    private const int Zip64LocatorSignature = 0x07064b50;

    /// <summary>The archive format, from the extension and the signature, or null for anything else.</summary>
    public static Par3InsideFormat? FormatOf(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var fs = File.OpenRead(path);
        if (fs.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8)
        {
            return null;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".zip" when head[..4].SequenceEqual("PK\x03\x04"u8) => Par3InsideFormat.Zip,
            ".7z" when head[..6].SequenceEqual((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]) => Par3InsideFormat.SevenZip,
            ".rar" when head[..7].SequenceEqual("Rar!\x1A\x07\x00"u8) || head.SequenceEqual("Rar!\x1A\x07\x01\x00"u8) => Par3InsideFormat.Rar,
            _ => null,
        };
    }

    /// <summary>True when a PAR3 packet starts in the last 64 KiB of the file (where the creator packet of inside data is).</summary>
    public static bool ContainsPar3Packets(string path)
    {
        using var fs = File.OpenRead(path);
        long length = fs.Length;
        int window = (int)Math.Min(length, 64 * 1024);
        var tail = new byte[window];
        RandomAccess.Read(fs.SafeFileHandle, tail, length - window);
        return tail.AsSpan().IndexOf("PAR3\0PKT"u8) >= 0;
    }

    /// <summary>Adds PAR3 recovery data to the archive, in place.</summary>
    /// <exception cref="InvalidDataException">Not a supported archive, or it already has data after its end.</exception>
    public static Par3InsideResult Insert(Par3InsideOptions options, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        var path = Path.GetFullPath(options.ArchivePath);
        var format = FormatOf(path) ?? throw new InvalidDataException($"'{path}' is not a .zip, .7z or .rar archive.");
        if (ContainsPar3Packets(path))
        {
            throw new InvalidDataException($"'{path}' already contains PAR3 data.");
        }

        long length = new FileInfo(path).Length;
        int footer = format switch
        {
            Par3InsideFormat.Zip => ZipFooterLength(path, length),
            Par3InsideFormat.SevenZip => CheckSevenZip(path, length),
            _ => 0,
        };

        context.Observer.Plan(
        [
            new("Planning", 4096, "Chooses the block size for the archive: the smallest one for which a larger block would not make the result noticeably smaller (as par3cmdline does)."),
            new("Hashing", length, "Reads the archive once and computes the checksum of every block."),
            new("Writing recovery data", length + length / 20.0, "Reads the archive again, computes the recovery blocks and writes them, with the set's description, into the archive (before its end record, so archive tools still open it)."),
        ]);
        context.Observer.Stage("Planning");
        var name = Path.GetFileName(path);
        Plan plan;
        if (options.BlockSize is ulong fixedSize)
        {
            plan = Layout(length, footer, fixedSize, name, options);
        }
        else
        {
            plan = Layout(length, footer, InitialBlockSize((ulong)length), name, options);
            for (ulong bs = plan.BlockSize == 40 ? 64 : plan.BlockSize * 2; bs * 2 <= (ulong)length; bs *= 2)
            {
                var candidate = Layout(length, footer, bs, name, options);

                // Like par3cmdline: take a larger block only when the file gets at least 1/64 smaller.
                if ((length + candidate.TotalPacketBytes) * 64 >= (length + plan.TotalPacketBytes) * 63)
                {
                    break;
                }

                plan = candidate;
            }
        }

        context.Observer.Stage("Hashing");
        var packets = BuildPackets(path, length, footer, plan, options.Creator, context.Observer);

        context.Observer.Stage("Writing recovery data");
        Write(path, length, footer, plan, packets, context);
        context.Observer.Progress(1);
        return new Par3InsideResult(format, length, length + plan.TotalPacketBytes + footer, plan.BlockSize, plan.InputBlocks, plan.RecoveryBlocks);
    }

    /// <summary>
    /// Removes the PAR3 data from an archive protected by <see cref="Insert"/> (or by par3cmdline). The archive must be
    /// intact; repair it first otherwise.
    /// </summary>
    /// <returns>The original length the archive was truncated to.</returns>
    public static long Delete(string archivePath, OperationContext? context = null)
    {
        var path = Path.GetFullPath(archivePath);
        context ??= OperationContext.Default;
        long length = new FileInfo(path).Length;
        context.Observer.Plan([.. VerifyStages.Plan(length, length, 0), new("Removing recovery data", 1, "Cuts the archive back to its original length, which removes the PAR3 data appended to it.")]);
        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = path }, context.Nested());
        var self = verify.Files.FirstOrDefault(f => string.Equals(Path.GetFullPath(f.TargetPath), path, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"'{path}' does not contain PAR3 data that protects the file itself.");
        int unprotected = self.File.Chunks.ToList().FindIndex(c => !c.Protected);
        if (unprotected < 0)
        {
            throw new InvalidDataException("The PAR3 data in this file is not \"PAR inside\" data (no unprotected chunk).");
        }

        if (self.Status != Par3FileStatus.Complete)
        {
            throw new InvalidOperationException("The archive is damaged. Repair it before removing the PAR3 data.");
        }

        long original = (long)self.File.Chunks.Take(unprotected).Sum(c => (decimal)c.Length);
        context.Observer.Stage("Removing recovery data");
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(original);
        }

        context.Observer.Progress(1);

        return original;
    }

    // ------------------------------------------------------------------ format checks

    /// <summary>
    /// Length of the ZIP footer that must stay at the end: the EOCD record (with its comment), plus the ZIP64 EOCD
    /// record and locator when the EOCD fields are saturated. The central directory must end exactly where the
    /// footer starts, or there is already extra data in the file.
    /// </summary>
    internal static int ZipFooterLength(string path, long length)
    {
        int window = (int)Math.Min(length, 22 + 65535 + 76);
        var tail = new byte[window];
        using (var fs = File.OpenRead(path))
        {
            RandomAccess.Read(fs.SafeFileHandle, tail, length - window);
        }

        for (int i = window - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(i)) != ZipEocdSignature)
            {
                continue;
            }

            long eocdStart = length - window + i;
            ulong cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12));
            ulong cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
            long footerStart = eocdStart;
            if ((cdSize == uint.MaxValue || cdOffset == uint.MaxValue) && i >= 20 + 56
                && BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(i - 20)) == Zip64LocatorSignature)
            {
                // ZIP64: the locator (20 bytes) before the EOCD points at the ZIP64 EOCD record.
                long recordStart = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(i - 20 + 8));
                int r = (int)(recordStart - (length - window));
                if (r < 0 || BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(r)) != Zip64EocdSignature)
                {
                    continue;
                }

                cdSize = BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(r + 40));
                cdOffset = BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(r + 48));
                footerStart = recordStart;
            }

            if (cdOffset + cdSize == (ulong)footerStart)
            {
                // The EOCD (22 bytes plus its comment) must end the file; anything after it is extra data.
                int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
                if (eocdStart + 22 + commentLength != length)
                {
                    throw new InvalidDataException("There is extra data in the ZIP file already.");
                }

                return (int)(length - footerStart);
            }

            if (cdOffset + cdSize < (ulong)footerStart)
            {
                throw new InvalidDataException("There is extra data in the ZIP file already.");
            }
        }

        throw new InvalidDataException("No valid ZIP end-of-central-directory record was found.");
    }

    /// <summary>A 7z file ends exactly at its "next header" (whose last two bytes are kEnd, size 0); no footer to copy.</summary>
    internal static int CheckSevenZip(string path, long length)
    {
        Span<byte> start = stackalloc byte[32];
        using var fs = File.OpenRead(path);
        if (fs.ReadAtLeast(start, 32, throwOnEndOfStream: false) < 32)
        {
            throw new InvalidDataException("Truncated 7z file.");
        }

        long end = 32 + BinaryPrimitives.ReadInt64LittleEndian(start[12..]) + BinaryPrimitives.ReadInt64LittleEndian(start[20..]);
        if (end < length)
        {
            throw new InvalidDataException("There is extra data in the 7z file already.");
        }

        Span<byte> last = stackalloc byte[2];
        if (end != length || RandomAccess.Read(fs.SafeFileHandle, last, end - 2) != 2 || last[0] != 0 || last[1] != 0)
        {
            throw new InvalidDataException("Invalid 7z file.");
        }

        return 0;
    }

    // ------------------------------------------------------------------ layout

    /// <summary>The block structure and the exact size of the PAR3 data for one block size.</summary>
    private sealed record Plan(ulong BlockSize, ulong InputBlocks, int RecoveryBlocks, int Repeats, long CommonBytes, long CreatorBytes, ulong DataBlocks, ulong FooterBlocks, ulong DataTailBlock, ulong FooterTailBlock, ulong FooterTailOffset)
    {
        public long RecoveryPacketBytes => 88 + (long)BlockSize;

        public long TotalPacketBytes => CommonBytes * Repeats + RecoveryPacketBytes * RecoveryBlocks + CreatorBytes;
    }

    /// <summary>par3cmdline's starting block size: about 10·√size, a power of two, at least 40, at most 2048 blocks.</summary>
    internal static ulong InitialBlockSize(ulong size)
    {
        if (size <= 40)
        {
            return 40;
        }

        ulong bs = (ulong)(Math.Sqrt(size) * 10);
        if (size / bs < 128)
        {
            bs = NextPow2(size / 256);
        }

        if (bs <= 40)
        {
            return 40;
        }

        bs = NextPow2(bs);
        while (size / bs > 2048)
        {
            bs *= 2;
        }

        return bs;
    }

    private static ulong NextPow2(ulong v) => v <= 1 ? 1 : System.Numerics.BitOperations.RoundUpToPowerOf2(v);

    private static Plan Layout(long length, int footer, ulong bs, string name, Par3InsideOptions options)
    {
        ulong dataLength = (ulong)(length - footer);
        ulong dataBlocks = dataLength / bs, dataTail = dataLength % bs;
        ulong footerBlocks = (ulong)footer / bs, footerTail = (ulong)footer % bs;

        // Tail blocks: the data tail and the footer tail share one block when both are ≥ 40 bytes and fit together.
        ulong next = dataBlocks + footerBlocks;
        ulong dataTailBlock = ulong.MaxValue, footerTailBlock = ulong.MaxValue, footerTailOffset = 0;
        if (dataTail >= Par3Chunk.InlineLimit)
        {
            dataTailBlock = next++;
        }

        if (footerTail >= Par3Chunk.InlineLimit)
        {
            if (dataTailBlock != ulong.MaxValue && dataTail + footerTail <= bs)
            {
                footerTailBlock = dataTailBlock;
                footerTailOffset = dataTail;
            }
            else
            {
                footerTailBlock = next++;
            }
        }

        ulong inputBlocks = next;
        int recovery = Math.Max(1, options.RecoveryBlockCount ?? (int)Math.Ceiling(inputBlocks * options.RedundancyPercent / 100.0));

        // Copies of the common packets: 2 + log2(recovery / 2), at most 3 below 9% redundancy, else at most log2(redundancy).
        int repeats = 2;
        for (long i = 4; i <= recovery; i *= 2)
        {
            repeats++;
        }

        double percent = 100.0 * recovery / Math.Max(1, inputBlocks);
        repeats = Math.Min(repeats, percent <= 8 ? 3 : (int)Math.Ceiling(Math.Log2(percent)));
        repeats = Math.Max(2, repeats);

        var plan = new Plan(bs, inputBlocks, recovery, repeats, 0, 0, dataBlocks, footerBlocks, dataTailBlock, footerTailBlock, footerTailOffset);

        // Sizes do not depend on checksums, so dummy packets give the exact byte counts.
        var dummy = Common(default, plan, (ulong)length, footer, 0, name, Fingerprint.Zero, 0, Enumerable.Repeat(default(BlockChecksum), (int)inputBlocks).ToList(), default, default);
        return plan with { CommonBytes = dummy.Sum(p => (long)p.Length), CreatorBytes = Par3PacketWriter.Text(default, Par3PacketTypes.Creator, options.Creator).Length };
    }

    private static byte[] Generator(Plan plan) => plan.InputBlocks + (ulong)plan.RecoveryBlocks <= 255 ? [0x1D] : [0x0B, 0x10];

    /// <summary>
    /// The common (vital) packets: Start, Cauchy matrix, External Data, File and Root. The File packet's chunks describe
    /// the final file, so they include the unprotected chunk of <paramref name="packetBytes"/> bytes.
    /// </summary>
    private static List<byte[]> Common(InputSetId setId, Plan plan, ulong length, int footer, long packetBytes, string name, Fingerprint fileHash, ulong hash16k, List<BlockChecksum> checks, TailInfo dataTail, TailInfo footerTail)
    {
        var start = Par3PacketWriter.Build(setId, Par3PacketTypes.Start, StartBody(plan));
        var matrix = Par3PacketWriter.Cauchy(setId, 0, 0, (ulong)plan.RecoveryBlocks);
        var packets = new List<byte[]> { start, matrix };
        for (int first = 0; first < checks.Count; first += 1024)
        {
            packets.Add(Par3PacketWriter.ExternalData(setId, (ulong)first, checks.Skip(first).Take(1024).ToList()));
        }

        var chunks = new List<Par3Chunk> { Chunk(length - (ulong)footer, 0, plan.BlockSize, plan.DataTailBlock, 0, dataTail) };
        if (footer > 0)
        {
            var footerChunk = Chunk((ulong)footer, plan.DataBlocks, plan.BlockSize, plan.FooterTailBlock, plan.FooterTailOffset, footerTail);
            chunks.Add(footerChunk);
            chunks.Add(new Par3Chunk { Protected = false, Length = (ulong)packetBytes });
            chunks.Add(footerChunk);
        }
        else
        {
            chunks.Add(new Par3Chunk { Protected = false, Length = (ulong)packetBytes });
        }

        var file = Par3PacketWriter.File(setId, name, hash16k, fileHash, [], chunks, plan.BlockSize);
        packets.Add(file);
        packets.Add(Par3PacketWriter.Root(setId, plan.InputBlocks, false, [], [Par3PacketWriter.ChecksumOf(file)]));
        return packets;
    }

    private static byte[] StartBody(Plan plan) => Par3PacketWriter.StartBody(default, default, plan.BlockSize, Generator(plan));

    private readonly record struct TailInfo(byte[]? Inline, ulong Crc, Fingerprint Hash);

    private static Par3Chunk Chunk(ulong length, ulong firstBlock, ulong bs, ulong tailBlock, ulong tailOffset, TailInfo tail)
    {
        ulong tailLength = length % bs;
        return tailLength >= Par3Chunk.InlineLimit
            ? new Par3Chunk { Length = length, FirstBlock = firstBlock, TailCrc = tail.Crc, TailHash = tail.Hash, TailBlock = tailBlock, TailOffset = tailOffset }
            : new Par3Chunk { Length = length, FirstBlock = firstBlock, InlineTail = tailLength > 0 ? tail.Inline ?? new byte[tailLength] : null };
    }

    // ------------------------------------------------------------------ packets

    private sealed record Packets(InputSetId SetId, List<byte[]> Common, byte[] Creator, Fingerprint RootChecksum, Fingerprint MatrixChecksum, Par3BlockSource Blocks, FileHandleCache Handles);

    /// <summary>The input blocks as pieces of the archive (the tail block is assembled from both tails).</summary>
    private static IReadOnlyList<(ulong, ulong, PieceSource)>[] Pieces(string path, long length, int footer, Plan plan)
    {
        ulong bs = plan.BlockSize;
        long dataLength = length - footer;
        var blocks = new List<(ulong, ulong, PieceSource)>[plan.InputBlocks];
        for (ulong b = 0; b < plan.DataBlocks; b++)
        {
            blocks[b] = [(0, bs, new PieceSource(path, (long)(b * bs)))];
        }

        for (ulong b = 0; b < plan.FooterBlocks; b++)
        {
            blocks[plan.DataBlocks + b] = [(0, bs, new PieceSource(path, dataLength + (long)(b * bs)))];
        }

        if (plan.DataTailBlock != ulong.MaxValue)
        {
            blocks[plan.DataTailBlock] = [(0, (ulong)dataLength % bs, new PieceSource(path, (long)(plan.DataBlocks * bs)))];
        }

        if (plan.FooterTailBlock != ulong.MaxValue)
        {
            var piece = (plan.FooterTailOffset, (ulong)footer % bs, new PieceSource(path, dataLength + (long)(plan.FooterBlocks * bs)));
            if (plan.FooterTailBlock == plan.DataTailBlock)
            {
                blocks[plan.FooterTailBlock].Add(piece);
            }
            else
            {
                blocks[plan.FooterTailBlock] = [piece];
            }
        }

        return blocks;
    }

    private static Packets BuildPackets(string path, long length, int footer, Plan plan, string creatorText, IParOperationObserver observer)
    {
        ulong bs = plan.BlockSize;
        var handles = new FileHandleCache();
        var source = new Par3BlockSource(Pieces(path, length, footer, plan), handles);

        // Block checksums.
        var checks = new List<BlockChecksum>((int)plan.InputBlocks);
        var buf = new byte[bs];
        for (int b = 0; b < (int)plan.InputBlocks; b++)
        {
            if (b % 256 == 255)
            {
                observer.Progress((double)b / plan.InputBlocks);
            }

            source.Read(b, 0, buf);
            checks.Add(new BlockChecksum(Crc64Iso.Compute(buf), Fingerprint.Compute(buf)));
        }

        TailInfo TailOf(long offset, ulong tailLength)
        {
            if (tailLength == 0)
            {
                return default;
            }

            var bytes = new byte[tailLength];
            handles.ReadPadded(path, offset, bytes);
            return tailLength < Par3Chunk.InlineLimit
                ? new TailInfo(bytes, 0, default)
                : new TailInfo(null, Crc64Iso.Compute(bytes.AsSpan(0, Par3Chunk.InlineLimit)), Fingerprint.Compute(bytes));
        }

        long dataLength = length - footer;
        var dataTail = TailOf((long)(plan.DataBlocks * bs), (ulong)dataLength % bs);
        var footerTail = TailOf(dataLength + (long)(plan.FooterBlocks * bs), (ulong)footer % bs);

        // File hash and 16 KiB CRC of the final file the way par3cmdline computes them: over the protected bytes only
        // (the unprotected packets are skipped, not hashed as zeros; see Par3FileHasher), and the CRC covers the
        // protected bytes that lie in the file's first 16 KiB.
        long packetBytes = plan.TotalPacketBytes;
        var hasher = new Blake3Hasher();
        ulong crc16k = 0;
        long filePos = 0;
        void Feed(ReadOnlySpan<byte> data)
        {
            hasher.Update(data);
            if (filePos < 16384)
            {
                crc16k = Crc64Iso.Compute(data[..(int)Math.Min(data.Length, 16384 - filePos)], crc16k);
            }

            filePos += data.Length;
        }

        var chunk = new byte[1 << 20];
        void FeedFile(long offset, long count)
        {
            while (count > 0)
            {
                int n = (int)Math.Min(chunk.Length, count);
                handles.ReadPadded(path, offset, chunk.AsSpan(0, n));
                Feed(chunk.AsSpan(0, n));
                offset += n;
                count -= n;
            }
        }

        FeedFile(0, length);
        filePos += packetBytes;
        FeedFile(dataLength, footer);
        var hash = new byte[16];
        hasher.Finalize(hash);

        var unique = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var setId = Par3PacketWriter.ComputeSetId(unique, StartBody(plan));
        var common = Common(setId, plan, (ulong)length, footer, packetBytes, Path.GetFileName(path), Fingerprint.FromBytes(hash), crc16k, checks, dataTail, footerTail);
        var creator = Par3PacketWriter.Text(setId, Par3PacketTypes.Creator, creatorText);
        System.Diagnostics.Debug.Assert(common.Sum(p => (long)p.Length) == plan.CommonBytes && creator.Length == plan.CreatorBytes, "packet sizes differ from the plan");

        return new Packets(setId, common, creator, Par3PacketWriter.ChecksumOf(common[^1]), Par3PacketWriter.ChecksumOf(common[1]), source, handles);
    }

    // ------------------------------------------------------------------ writing

    private static void Write(string path, long length, int footer, Plan plan, Packets packets, OperationContext context)
    {
        using var handles = packets.Handles;
        long bs = (long)plan.BlockSize;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            fs.SetLength(length + plan.TotalPacketBytes + footer);
            var common = packets.Common.SelectMany(p => p).ToArray();
            long pos = length;
            void Put(ReadOnlySpan<byte> bytes)
            {
                RandomAccess.Write(fs.SafeFileHandle, bytes, pos);
                pos += bytes.Length;
            }

            // [common][recovery group][common]...[recovery group][common][creator]: Repeats copies of the common packets,
            // with the recovery packets split as evenly as possible into Repeats − 1 groups (group g holds blocks
            // g·n/(R−1) up to (g+1)·n/(R−1)).
            var recoveryIndices = Enumerable.Range(0, plan.RecoveryBlocks).Select(i => (ulong)i).ToList();
            var outputs = new (FileStream, long, byte[])[plan.RecoveryBlocks];
            int groups = plan.Repeats - 1;
            Put(common);
            for (int g = 0; g < groups; g++)
            {
                for (int r = g * plan.RecoveryBlocks / groups; r < (g + 1) * plan.RecoveryBlocks / groups; r++)
                {
                    var header = Par3PacketWriter.RecoveryDataHeader(packets.SetId, packets.RootChecksum, packets.MatrixChecksum, (ulong)r, bs);
                    outputs[r] = (fs, pos + header.Length, header);
                    pos += header.Length + bs;
                }

                Put(common);
            }

            Put(packets.Creator);
            System.Diagnostics.Debug.Assert(pos == length + plan.TotalPacketBytes, "written size differs from the plan");

            if (footer > 0)
            {
                var copy = new byte[footer];
                RandomAccess.Read(fs.SafeFileHandle, copy, length - footer);
                Put(copy);
            }

            // Recovery data: Cauchy code with par3cmdline's x_I = I, like par3cmdline's insert.
            var done = new bool[plan.RecoveryBlocks];
            var map = context.CreateBlockMap((int)plan.InputBlocks, plan.RecoveryBlocks);
            var sink = new Par3Creator.RecoverySink(outputs, bs, done, map);
            var progress = context.Observer.ToProgress();
            Par3Fields.With(Generator(plan), new Par3Creator.CauchyEncode(plan.InputBlocks, recoveryIndices, packets.Blocks, sink, bs, context.Combine.Create(progress), Par3CauchyConvention.Par3cmdline));
            for (int r = 0; r < plan.RecoveryBlocks; r++)
            {
                var (_, dataOffset, header) = outputs[r];
                if (!done[r])
                {
                    Par3Creator.FinishChecksumFromDisk(fs, dataOffset, header, bs);
                }

                RandomAccess.Write(fs.SafeFileHandle, header, dataOffset - header.Length);
            }
        }
        catch
        {
            fs.SetLength(length);
            throw;
        }
    }
}
