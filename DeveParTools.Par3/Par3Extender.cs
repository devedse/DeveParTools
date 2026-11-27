using System.Buffers.Binary;
using DeveParTools.Core.IO;
using DeveParTools.Core.Operations;
using DeveParTools.Par3.Packets;

namespace DeveParTools.Par3;

/// <summary>Options for <see cref="Par3Extender.Extend"/>.</summary>
public sealed class Par3ExtendOptions
{
    /// <summary>Any PAR3 file of the existing set (index or volume).</summary>
    public required string Par3Path { get; init; }

    public string? BaseDirectory { get; init; }

    /// <summary>Extra files to search for input data (renamed copies).</summary>
    public IReadOnlyList<string> ExtraDataFiles { get; init; } = [];

    /// <summary>Exact number of new recovery blocks (else <see cref="RedundancyPercent"/> of the input blocks).</summary>
    public int? RecoveryBlockCount { get; init; }

    public double RedundancyPercent { get; init; } = 10;

    /// <summary>Index of the first new recovery block. Default: one past the highest index already in the set.</summary>
    public ulong? FirstRecoveryBlock { get; init; }

    /// <summary>Number of new recovery files (default: exponentially growing sizes).</summary>
    public int? VolumeCount { get; init; }

    /// <summary>Base name of the new recovery files. Default: the set's index file name.</summary>
    public string? OutputPath { get; init; }

    public string Creator { get; init; } = Par3Creator.DefaultCreator;
}

/// <summary>
/// Adds recovery blocks to an existing PAR3 set, like <c>par3 extend</c>: the input files are verified, and new
/// recovery blocks with unused indices are written to new <c>.volXX+YY.par3</c> files. Nothing existing is changed,
/// so old and new recovery files work together.
/// </summary>
/// <remarks>
/// <para><b>Cauchy sets.</b> Recovery block R is Σ_I B_I / (x_I + y_R) with y_R = MAX − R, so any unused R gives a new,
/// independent block. The only limit is the field: all x and y values must differ, so (input blocks + 1) plus the
/// highest recovery index must stay below 2^bits − 1.</para>
/// <para>The set does not say which x_I convention made it: par3cmdline uses x_I = I and the specification text uses
/// x_I = I + 1 (see <see cref="Par3CauchyConvention"/>). If the set already has recovery blocks, one of them is computed
/// again during the same pass over the input (one extra output row) and compared with the stored data. The convention
/// that reproduces it is the one to use. If par3cmdline's convention does not match, a second pass with the other one
/// follows.</para>
/// <para><b>FFT sets.</b> The FFT matrix fixes m, the number of recovery blocks per cohort, when the set is created.
/// Recovery block r belongs to cohort r mod C at local index r div C, which must stay below m. So an FFT set can grow
/// up to m × C recovery blocks, which is why <c>--max-recovery</c> exists at creation.</para>
/// </remarks>
public static class Par3Extender
{
    public static Par3CreateResult Extend(Par3ExtendOptions options, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        var verify = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = options.Par3Path, BaseDirectory = options.BaseDirectory, ExtraDataFiles = options.ExtraDataFiles }, context);
        if (verify.MissingBlocks > 0)
        {
            throw new InvalidOperationException($"{verify.MissingBlocks} input block(s) are missing or damaged. Repair the files before extending the set.");
        }

        var set = verify.Set;
        ulong blockCount = set.BlockCount;
        if (blockCount == 0)
        {
            throw new InvalidOperationException("The set has no input blocks to protect.");
        }

        var packets = ReadVitalPackets(set);
        var matrix = (Par3MatrixPacket?)packets.Select(p => p.Packet).OfType<Par3FftMatrixPacket>().FirstOrDefault()
            ?? packets.Select(p => p.Packet).OfType<Par3CauchyMatrixPacket>().FirstOrDefault(m => m.First == 0 && m.LastPlus1 == 0)
            ?? throw new NotSupportedException("Only sets with a Cauchy or FFT matrix over all input blocks can be extended.");

        var existing = set.RecoveryBlocks.Where(r => r.Matrix.Checksum == matrix.Checksum).ToList();
        var used = existing.Select(r => r.Index).ToHashSet();
        ulong first = options.FirstRecoveryBlock ?? (used.Count == 0 ? 0 : used.Max() + 1);
        int count = options.RecoveryBlockCount ?? (int)Math.Ceiling(blockCount * options.RedundancyPercent / 100.0);
        if (count <= 0)
        {
            throw new ArgumentException("At least one new recovery block is needed.");
        }

        var indices = Enumerable.Range(0, count).Select(i => first + (ulong)i).ToList();
        if (indices.Where(used.Contains).Select(i => (ulong?)i).FirstOrDefault() is ulong clash)
        {
            throw new ArgumentException($"Recovery block {clash} already exists; choose another first recovery block.");
        }

        byte[] generator = set.Start.GeneratorBytes;
        Par3Fft.Layout? fft = null;
        if (matrix is Par3FftMatrixPacket fftPacket)
        {
            fft = Par3Fft.FromPacket(fftPacket, generator.Length, blockCount) ?? throw new NotSupportedException("This FFT matrix cannot be encoded.");
            ulong capacity = fft.Field == null ? 1 : (ulong)fft.M * (ulong)fft.Cohorts;
            if (indices[^1] >= capacity)
            {
                throw new ArgumentException($"This FFT set holds at most {capacity} recovery blocks (indices 0 to {capacity - 1}); it was created for that maximum.");
            }
        }
        else if (generator.Length < 8)
        {
            // All x (≤ input blocks) and y = MAX − R must differ: blocks + 1 + R < 2^bits − 1.
            ulong max = (1UL << (generator.Length * 8)) - 1;
            if (blockCount + 1 + indices[^1] >= max)
            {
                throw new ArgumentException($"Recovery block {indices[^1]} does not fit the set's GF(2^{generator.Length * 8}) Cauchy code with {blockCount} input blocks.");
            }
        }

        var indexPath = options.OutputPath ?? IndexPathOf(options.Par3Path);
        var volumes = Par3Creator.PlanVolumes(Path.GetFullPath(indexPath), new Par3CreateOptions { Inputs = [], OutputPath = indexPath, VolumeCount = options.VolumeCount, FirstRecoveryBlock = first }, count, ".vol");
        foreach (var v in volumes.Where(v => File.Exists(v.Path)))
        {
            throw new IOException($"'{v.Path}' already exists.");
        }

        var vital = packets.Select(p => p.Raw).ToList();
        var creator = Par3PacketWriter.Text(set.SetId, Par3PacketTypes.Creator, options.Creator);
        using var handles = new FileHandleCache();
        var source = new Par3BlockSource(verify.Blocks.Select(b => b!.Pieces).ToArray(), handles);
        long blockSize = (long)set.BlockSize;

        // For a Cauchy set with recovery data, recompute one stored block as a probe to learn the x_I convention.
        var probe = fft == null ? existing.FirstOrDefault() : null;
        var probeVolume = probe == null ? null : new Par3Creator.Volume(Path.Combine(Path.GetDirectoryName(volumes[0].Path)!, $".devepar-probe-{Guid.NewGuid():N}.tmp"), count, 1);
        var allIndices = probe == null ? indices : [.. indices, probe.Index];
        var allVolumes = probeVolume == null ? volumes : [.. volumes, probeVolume];
        try
        {
            foreach (var convention in new[] { Par3CauchyConvention.Par3cmdline, Par3CauchyConvention.Specification })
            {
                var map = context.CreateBlockMap((int)blockCount, allIndices.Count);
                var createOptions = new Par3CreateOptions { Inputs = [], OutputPath = indexPath, CauchyConvention = convention };
                context.Observer.Stage(convention == Par3CauchyConvention.Par3cmdline ? "Encoding" : "Encoding (specification convention)");
                Par3Creator.WriteRecovery(set.SetId, set.Root.Checksum, matrix.Checksum, generator, fft, blockCount, allIndices, allVolumes, source, blockSize, vital, creator, createOptions, context, map);
                if (probe == null || SameData(probeVolume!.Path, ProbeDataOffset(set, matrix, probe.Index, blockSize), probe.Data, blockSize))
                {
                    break;
                }

                if (convention == Par3CauchyConvention.Specification)
                {
                    throw new InvalidDataException("The set's existing recovery data does not match either Cauchy convention, so compatible recovery blocks cannot be made.");
                }
            }
        }
        catch
        {
            // Leave no half-written or incompatible recovery files behind.
            foreach (var v in volumes)
            {
                File.Delete(v.Path);
            }

            throw;
        }
        finally
        {
            if (probeVolume != null)
            {
                File.Delete(probeVolume.Path);
            }
        }

        context.Observer.Progress(1);
        return new Par3CreateResult
        {
            SetId = set.SetId,
            BlockSize = set.BlockSize,
            InputBlocks = blockCount,
            RecoveryBlocks = count,
            CodeDescription = fft != null ? "FFT Reed-Solomon" : $"Cauchy Reed-Solomon (GF(2^{generator.Length * 8}))",
            WrittenFiles = volumes.Select(v => v.Path).ToList(),
        };
    }

    /// <summary>The index file name for a set file: <c>name.vol10+5.par3</c> and <c>name.part0+3.par3</c> become <c>name.par3</c>.</summary>
    public static string IndexPathOf(string par3Path)
    {
        var name = Path.GetFileName(par3Path);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^(?<base>.*)\.(vol|part)\d+\+\d+\.par3$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? Path.Combine(Path.GetDirectoryName(par3Path) ?? string.Empty, match.Groups["base"].Value + ".par3") : par3Path;
    }

    /// <summary>
    /// Every packet the new recovery files must repeat (everything except recovery data, data and creator packets),
    /// once each, read back as raw bytes from the files they came from. The Start packet goes first.
    /// </summary>
    private static List<(Par3Packet Packet, byte[] Raw)> ReadVitalPackets(Par3InputSet set)
    {
        var result = new List<(Par3Packet, byte[])>();
        var seen = new HashSet<Fingerprint>();
        foreach (var file in set.SourceFiles)
        {
            var read = Par3PacketReader.ReadFile(file);
            using var stream = File.OpenRead(file);
            foreach (var p in read.Packets)
            {
                if (p.SetId != set.SetId || p is Par3RecoveryDataPacket or Par3DataPacket or Par3CreatorPacket or Par3RecoveryExternalDataPacket || !seen.Add(p.Checksum))
                {
                    continue;
                }

                result.Add((p, ReadRaw(stream, p.SourceOffset)));
            }
        }

        return result.OrderBy(r => r.Item1 is Par3StartPacket ? 0 : 1).ToList();
    }

    private static byte[] ReadRaw(FileStream stream, long offset)
    {
        Span<byte> length = stackalloc byte[8];
        RandomAccess.Read(stream.SafeFileHandle, length, offset + 24);
        var raw = new byte[BinaryPrimitives.ReadInt64LittleEndian(length)];
        RandomAccess.Read(stream.SafeFileHandle, raw, offset);
        return raw;
    }

    private static long ProbeDataOffset(Par3InputSet set, Par3MatrixPacket matrix, ulong index, long blockSize)
        => Par3PacketWriter.RecoveryDataHeader(set.SetId, set.Root.Checksum, matrix.Checksum, index, blockSize).Length;

    private static bool SameData(string probeFile, long probeOffset, Par3RecoveryDataPacket stored, long length)
    {
        using var a = File.OpenRead(probeFile);
        using var b = File.OpenRead(stored.SourceFile!);
        var bufA = new byte[(int)Math.Min(length, 1 << 20)];
        var bufB = new byte[bufA.Length];
        for (long done = 0; done < length; done += bufA.Length)
        {
            int n = (int)Math.Min(bufA.Length, length - done);
            FileHandleCache.ReadPadded(a.SafeFileHandle, probeOffset + done, bufA.AsSpan(0, n));
            FileHandleCache.ReadPadded(b.SafeFileHandle, stored.DataOffset + done, bufB.AsSpan(0, n));
            if (!bufA.AsSpan(0, n).SequenceEqual(bufB.AsSpan(0, n)))
            {
                return false;
            }
        }

        return true;
    }
}
