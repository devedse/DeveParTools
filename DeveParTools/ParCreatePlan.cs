using DeveParTools.Core.Operations;
using DeveParTools.Par1;
using DeveParTools.Par2;
using DeveParTools.Par3;

namespace DeveParTools;

/// <summary>One input file of a planned recovery set.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="Input">The input (file or folder) it belongs to, as given in <see cref="ParCreateRequest.Inputs"/>.</param>
/// <param name="Blocks">
/// Input blocks it needs: PAR1 one per file; PAR2 its length divided by the block size, rounded up (0 for an empty
/// file); PAR3 its full blocks plus one for a tail of 40+ bytes (shorter tails are stored in the PAR3 packets).
/// </param>
public sealed record ParPlanFile(string Path, long Length, string Input, long Blocks = 0);

/// <summary>
/// What <see cref="ParClient.Create"/> will make for a request, computed without reading the files: the real block
/// size and block count (not just the target), the recovery blocks and files, and what they can rebuild.
/// </summary>
/// <remarks>
/// <para>The numbers come from the same functions the creators use (PAR2 <see cref="Par2Creator.ChooseSliceSize"/>,
/// PAR3 <see cref="Par3Creator.ChooseBlockSize"/> and <see cref="Par3Creator.CountBlocks"/>, and the recovery file
/// plans), so creating with <see cref="BlockSize"/> and <see cref="RecoveryBlocks"/> gives exactly this set. The one
/// exception is PAR3 deduplication, which needs the file contents: with it, the set can have fewer input blocks.</para>
/// <para>Example: PAR2, files of 1,000,000 and 10,000 bytes, target 100 blocks, 10%. Total ÷ target = 10,100 bytes
/// would give 100 + 1 = 101 blocks (each file is rounded up separately), so the block size is 10,104, the smallest
/// multiple of 4 with at most 100 blocks: 99 + 1 = 100 blocks. 10 recovery blocks go into files of 1, 2, 4 and 3
/// blocks. Efficiency: 1,010,000 / (100 × 10,104) = 99.96%.</para>
/// </remarks>
public sealed class ParCreatePlan
{
    public required ParFormat Format { get; init; }

    /// <summary>The input files with their block counts.</summary>
    public required IReadOnlyList<ParPlanFile> Files { get; init; }

    public long TotalBytes => Files.Sum(f => f.Length);

    public long LargestFile => Files.Select(f => f.Length).DefaultIfEmpty(0).Max();

    /// <summary>The block size that will be used (PAR1: the largest file, as every file is one block).</summary>
    public required long BlockSize { get; init; }

    /// <summary>The number of input blocks that will be made.</summary>
    public required long InputBlocks { get; init; }

    /// <summary>The number of recovery blocks that will be made.</summary>
    public required int RecoveryBlocks { get; init; }

    /// <summary>The recovery files, with the blocks each holds.</summary>
    public required IReadOnlyList<RecoveryFilePlan> RecoveryFiles { get; init; }

    /// <summary>The code, e.g. "Reed-Solomon (GF(2^16))" or "FFT Reed-Solomon (GF(2^16), 1 cohort(s))".</summary>
    public required string Code { get; init; }

    /// <summary>The most input blocks the format allows for these files (the smallest block size): for a block count slider.</summary>
    public required long MaxInputBlocks { get; init; }

    /// <summary>Problems that make Create fail or behave differently, in plain words.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Recovery blocks as a percentage of the input blocks.</summary>
    public double RedundancyPercent => InputBlocks == 0 ? 0 : 100.0 * RecoveryBlocks / InputBlocks;

    /// <summary>Recovery data in bytes (blocks × block size; the files add a little metadata).</summary>
    public long RecoveryBytes => RecoveryBlocks * BlockSize;

    /// <summary>
    /// How much of the input blocks is real data: total bytes ÷ (blocks × block size). Below 100% the last block of
    /// each file is partly empty, so recovery blocks protect less data than their size.
    /// </summary>
    public double Efficiency => InputBlocks == 0 ? 0 : (double)TotalBytes / ((double)InputBlocks * BlockSize);

    /// <summary>
    /// How many whole files can be rebuilt when they are missing completely: at least (the largest files go first)
    /// and at most (the smallest go first). Files without blocks (empty, or stored in the PAR3 packets) always count.
    /// </summary>
    public (int Min, int Max) RebuildableFiles
    {
        get
        {
            int free = Files.Count(f => f.Blocks == 0);
            var sizes = Files.Where(f => f.Blocks > 0).Select(f => f.Blocks).Order().ToList();
            return (free + Fit(sizes.AsEnumerable().Reverse()), free + Fit(sizes));
        }
    }

    private int Fit(IEnumerable<long> blocks)
    {
        long left = RecoveryBlocks;
        int n = 0;
        foreach (var b in blocks)
        {
            if (b > left)
            {
                break;
            }

            left -= b;
            n++;
        }

        return n;
    }
}

public static partial class ParClient
{
    /// <summary>The smallest PAR3 block size the plan considers (so the block count stays sensible).</summary>
    public const long Par3MinBlockSize = 64;

    /// <summary>The most input blocks a plan offers for PAR3 (the FFT code with interleaving can go further).</summary>
    public const long Par3MaxPlannedBlocks = 1 << 20;

    /// <summary>
    /// The files a create request would protect, with their lengths (blocks are filled in by <see cref="Plan"/>). Folders
    /// are expanded like <see cref="Create"/> does for the format. Scanning once and passing the result to
    /// <see cref="Plan"/> keeps re-planning cheap while options change.
    /// </summary>
    public static IReadOnlyList<ParPlanFile> ScanInputs(ParCreateRequest request)
    {
        var format = request.Format ?? FromExtension(request.OutputPath) ?? ParFormat.Par2;
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var output = Path.GetFullPath(request.OutputPath);
        var files = new List<ParPlanFile>();
        foreach (var input in request.Inputs)
        {
            IEnumerable<string> paths = Directory.Exists(input)
                ? format == ParFormat.Par3
                    ? Directory.EnumerateFiles(input, "*", request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)
                    : ExpandFiles([input], request.Recursive)
                : File.Exists(input) ? [input] : [];
            foreach (var p in paths.Select(Path.GetFullPath))
            {
                // PAR3 leaves out .par3 files and the output; PAR1/PAR2 leave PAR files out of folders (ExpandFiles).
                if ((format == ParFormat.Par3 && (p.EndsWith(".par3", StringComparison.OrdinalIgnoreCase) || p == output)) || !seen.Add(p))
                {
                    continue;
                }

                files.Add(new ParPlanFile(p, new FileInfo(p).Length, input));
            }
        }

        return files;
    }

    /// <summary>
    /// What <see cref="Create"/> will make for <paramref name="request"/>: real block size and count, recovery blocks
    /// and files. See <see cref="ParCreatePlan"/>.
    /// </summary>
    /// <param name="files">The result of <see cref="ScanInputs"/> for this request (scanned now when null).</param>
    public static ParCreatePlan Plan(ParCreateRequest request, IReadOnlyList<ParPlanFile>? files = null)
    {
        files ??= ScanInputs(request);
        var format = request.Format ?? FromExtension(request.OutputPath) ?? ParFormat.Par2;
        var lengths = files.Select(f => f.Length).ToList();
        long largest = lengths.DefaultIfEmpty(0).Max();
        var warnings = new List<string>();
        int Recovery(long blocks, int minimum) => blocks == 0 ? 0 : request.RecoveryBlockCount ?? Math.Max(minimum, (int)Math.Ceiling(blocks * request.RedundancyPercent / 100.0));

        switch (format)
        {
            case ParFormat.Par1:
            {
                // Every file is one block; recovery files .p01, .p02, ... hold one block (the size of the largest file) each.
                int recovery = files.Count == 0 ? 0 : request.RecoveryBlockCount ?? Math.Max(1, (int)Math.Ceiling(files.Count * request.RedundancyPercent / 100));
                var baseName = request.OutputPath.EndsWith(".par", StringComparison.OrdinalIgnoreCase) ? request.OutputPath[..^4] : request.OutputPath;
                if (files.Count + recovery > 255)
                {
                    warnings.Add($"PAR1 allows at most 255 files and recovery files together ({files.Count:N0} + {recovery:N0}).");
                }

                var outputDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
                if (files.Any(f => !string.Equals(Path.GetDirectoryName(f.Path), outputDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                {
                    warnings.Add("PAR1 stores bare file names: every file must be in the folder of the .par file.");
                }

                return new ParCreatePlan
                {
                    Format = format,
                    Files = files.Select(f => f with { Blocks = 1 }).ToList(),
                    BlockSize = largest,
                    InputBlocks = files.Count,
                    RecoveryBlocks = recovery,
                    RecoveryFiles = Enumerable.Range(1, recovery).Select(v => new RecoveryFilePlan(baseName + Par1File.VolumeExtension(v), v, 1)).ToList(),
                    Code = "Reed-Solomon (GF(2^8), one block per file)",
                    MaxInputBlocks = files.Count,
                    Warnings = warnings,
                };
            }

            case ParFormat.Par2:
            {
                var nonEmpty = lengths.Where(l => l > 0).ToList();
                long size = request.BlockSize ?? Par2Creator.ChooseSliceSize(nonEmpty, request.BlockCount ?? 2000);
                if (size <= 0 || size % 4 != 0)
                {
                    warnings.Add("The PAR2 block size must be a positive multiple of 4.");
                    size = Math.Max(4, (size + 3) / 4 * 4);
                }

                var planned = files.Select(f => f with { Blocks = (f.Length + size - 1) / size }).ToList();
                long blocks = planned.Sum(f => f.Blocks);
                int recovery = Recovery(blocks, 0);
                if (blocks > Par2Math.MaxInputSlices)
                {
                    warnings.Add($"PAR2 allows at most {Par2Math.MaxInputSlices:N0} input blocks; this is {blocks:N0}. Use a larger block size.");
                }

                if ((long)request.FirstRecoveryBlock + recovery > 65535)
                {
                    warnings.Add("PAR2 recovery block numbers must stay below 65,535: use fewer recovery blocks or a lower first block.");
                }

                return new ParCreatePlan
                {
                    Format = format,
                    Files = planned,
                    BlockSize = size,
                    InputBlocks = blocks,
                    RecoveryBlocks = recovery,
                    RecoveryFiles = Par2Creator.PlanRecoveryFiles(request.OutputPath, SchemeOf(request), request.VolumeCount, request.FirstRecoveryBlock, recovery, size, largest),
                    Code = "Reed-Solomon (GF(2^16))",
                    MaxInputBlocks = Math.Min(Par2Math.MaxInputSlices, nonEmpty.Sum(l => (l + 3) / 4)),
                    Warnings = warnings,
                };
            }

            default:
            {
                ulong size = request.BlockSize is long bs ? (ulong)bs : Par3Creator.ChooseBlockSize(lengths.Sum(), request.BlockCount ?? 2000);
                if (size == 0 || size % 8 != 0)
                {
                    warnings.Add("The PAR3 block size must be a positive multiple of 8.");
                    size = Math.Max(8, (size + 7) / 8 * 8);
                }

                var planned = files.Select(f => f with { Blocks = (long)Par3Creator.FileBlocks(f.Length, size) }).ToList();
                long blocks = (long)Par3Creator.CountBlocks(lengths, size, request.Par3PackTails);
                int recovery = Recovery(blocks, 0);
                var code = Par3Creator.ResolveCode(request.Par3Code, (ulong)blocks, recovery);
                if (code == Par3Code.Fft && Math.Max(recovery, request.Par3MaxRecoveryBlocks ?? 0) > blocks)
                {
                    warnings.Add("The FFT code needs at least as many input blocks as recovery blocks: use the Cauchy code or fewer recovery blocks.");
                }
                else if (code == Par3Code.Cauchy && (ulong)blocks + (ulong)request.FirstRecoveryBlock + (ulong)recovery > 65535)
                {
                    warnings.Add("With more than 65,535 blocks the Cauchy code needs GF(2^32), which is very slow: use the FFT code.");
                }

                if (request.Par3Dedup != Par3Dedup.None)
                {
                    warnings.Add("Deduplication can lower the number of input blocks; these numbers are without it.");
                }

                return new ParCreatePlan
                {
                    Format = format,
                    Files = planned,
                    BlockSize = (long)size,
                    InputBlocks = blocks,
                    RecoveryBlocks = recovery,
                    RecoveryFiles = Par3Creator.PlanRecoveryFiles(request.OutputPath, SchemeOf(request), request.VolumeCount, (ulong)request.FirstRecoveryBlock, recovery, size, largest),
                    Code = Par3Creator.DescribeCode(code, (ulong)blocks, recovery, (ulong)request.FirstRecoveryBlock, request.Par3MaxRecoveryBlocks, request.Par3Interleave),
                    MaxInputBlocks = Math.Min(Par3MaxPlannedBlocks, (long)Par3Creator.CountBlocks(lengths, Par3MinBlockSize, request.Par3PackTails)),
                    Warnings = warnings,
                };
            }
        }
    }

    /// <summary>The recovery file scheme a request asks for.</summary>
    internal static RecoveryFileScheme SchemeOf(ParCreateRequest request)
        => request.UniformVolumes ? RecoveryFileScheme.Uniform : request.LimitVolumeSize ? RecoveryFileScheme.LimitSize : RecoveryFileScheme.Exponential;
}
