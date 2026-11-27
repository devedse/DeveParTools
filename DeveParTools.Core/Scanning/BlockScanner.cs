using DeveParTools.Core.IO;

namespace DeveParTools.Core.Scanning;

/// <summary>
/// Format-specific knowledge the <see cref="BlockScanner"/> needs: a cheap rolling ("weak") checksum to find
/// candidate positions, and a strong hash to confirm them.
/// </summary>
/// <remarks>
/// PAR2 uses CRC-32 + MD5 per slice, PAR3 uses CRC-64-ISO + BLAKE3 per block. Both CRCs can "roll": the CRC of
/// bytes [i+1, i+W+1) follows from the CRC of [i, i+W) in O(1), so every byte offset of a file can be tested.
/// </remarks>
public interface IBlockMatcher
{
    /// <summary>Block size (rolling window length).</summary>
    int BlockSize { get; }

    /// <summary>Weak checksum of a full window.</summary>
    ulong Weak(ReadOnlySpan<byte> window);

    /// <summary>Weak checksum after removing <paramref name="outgoing"/> and appending <paramref name="incoming"/>.</summary>
    ulong Slide(ulong weak, byte outgoing, byte incoming);

    /// <summary>Quick membership test; false positives allowed, false negatives not.</summary>
    bool MayMatch(ulong weak);

    /// <summary>Block indices whose full-length weak checksum equals <paramref name="weak"/> and whose strong hash matches.</summary>
    int MatchFull(ulong weak, ReadOnlySpan<byte> window);

    /// <summary>
    /// Shorter blocks (the end of a file, zero-padded to the block size in the checksums). Returns the distinct
    /// lengths that exist; <see cref="MatchTail"/> is tried for each at the end of every scanned file.
    /// </summary>
    IReadOnlyCollection<int> TailLengths { get; }

    /// <summary>Block index matching <paramref name="data"/> as a short (padded) block, or -1.</summary>
    int MatchTail(ReadOnlySpan<byte> data);
}

/// <summary>A block found in a scanned file.</summary>
public readonly record struct FoundBlock(int BlockIndex, long Offset, int Length);

/// <summary>
/// Finds known blocks anywhere inside arbitrary files by sliding a rolling checksum over every byte offset,
/// multithreaded over file segments.
/// </summary>
/// <remarks>
/// <para><b>Algorithm.</b> For a window of W bytes starting at offset i, compute the weak checksum. If the
/// matcher says it may match a known block, compute the strong hash of the window to confirm. On a confirmed
/// match the scan jumps forward W bytes (blocks don't overlap); otherwise it slides one byte:</para>
/// <code>
/// file:   ....[ block 7 ][ garbage ][ block 9 ]...
///              ^ match → jump W ^ slide 1 byte at a time ... ^ match
/// </code>
/// <para>This is how inserted or deleted bytes are handled: after an insertion every later block sits at an
/// unexpected offset, but sliding finds it again.</para>
/// <para><b>Parallelism.</b> The file is split into segments, one per thread. Each thread reads its segment
/// plus W−1 bytes of overlap and reports matches that <i>start</i> inside its segment.</para>
/// </remarks>
public static class BlockScanner
{
    private const int ReadChunk = 4 << 20;

    public static List<FoundBlock> Scan(string path, IBlockMatcher matcher, FileHandleCache handles, int threads, CancellationToken cancellationToken = default)
    {
        long length = new FileInfo(path).Length;
        var results = new List<FoundBlock>();
        int w = matcher.BlockSize;
        if (length >= w)
        {
            long positions = length - w + 1;
            int segments = (int)Math.Clamp(positions / (16L << 20), 1, Math.Max(1, threads));
            long per = (positions + segments - 1) / segments;
            var parts = new List<FoundBlock>[segments];
            Parallel.For(0, segments, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = cancellationToken }, s =>
            {
                long start = s * per;
                long end = Math.Min(positions, start + per);
                parts[s] = ScanSegment(path, matcher, handles, start, end, cancellationToken);
            });
            foreach (var p in parts)
            {
                results.AddRange(p);
            }
        }

        // Short blocks at the very end of the file.
        foreach (int tail in matcher.TailLengths)
        {
            if (tail <= 0 || tail > length)
            {
                continue;
            }

            var buf = new byte[tail];
            handles.ReadPadded(path, length - tail, buf);
            int idx = matcher.MatchTail(buf);
            if (idx >= 0)
            {
                results.Add(new FoundBlock(idx, length - tail, tail));
            }
        }

        return results;
    }

    /// <summary>Scan window start positions in [start, end).</summary>
    private static List<FoundBlock> ScanSegment(string path, IBlockMatcher matcher, FileHandleCache handles, long start, long end, CancellationToken token)
    {
        int w = matcher.BlockSize;
        var found = new List<FoundBlock>();
        // Buffer holds [bufStart, bufStart + bufLen); we keep at least W bytes of lookahead.
        int capacity = Math.Max(ReadChunk, 2 * w);
        var buf = new byte[capacity + w];
        long bufStart = start;
        int bufLen = handles.ReadPadded(path, bufStart, buf.AsSpan(0, capacity + w));
        long pos = start;
        bool haveWeak = false;
        ulong weak = 0;
        while (pos < end)
        {
            token.ThrowIfCancellationRequested();
            int rel = (int)(pos - bufStart);
            if (rel + w + 1 > bufLen)
            {
                // Refill so that the window and one following byte are available.
                bufStart = pos;
                bufLen = handles.ReadPadded(path, bufStart, buf.AsSpan(0, capacity + w));
                rel = 0;
                if (bufLen < w)
                {
                    break;
                }
            }

            if (!haveWeak)
            {
                weak = matcher.Weak(buf.AsSpan(rel, w));
                haveWeak = true;
            }

            if (matcher.MayMatch(weak))
            {
                int idx = matcher.MatchFull(weak, buf.AsSpan(rel, w));
                if (idx >= 0)
                {
                    found.Add(new FoundBlock(idx, pos, w));
                    pos += w;
                    haveWeak = false;
                    continue;
                }
            }

            if (pos + 1 >= end || rel + w >= bufLen)
            {
                pos++;
                haveWeak = false;
                continue;
            }

            weak = matcher.Slide(weak, buf[rel], buf[rel + w]);
            pos++;
        }

        return found;
    }
}
