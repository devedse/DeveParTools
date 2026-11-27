using DeveParTools.Core.Operations;
using DeveParTools.Par1;
using DeveParTools.Par2;
using DeveParTools.Par3;

namespace DeveParTools;

public enum ParFormat
{
    Par1,
    Par2,
    Par3,
}

/// <summary>Status of one protected file, format independent.</summary>
public enum ParFileStatus
{
    Complete,
    Damaged,
    Missing,
    Unknown,
}

public sealed record ParFileReport(string Name, string Path, long Size, ParFileStatus Status, string? FoundAs = null);

/// <summary>Format-independent result of a verification.</summary>
public sealed class ParVerifyReport
{
    public required ParFormat Format { get; init; }

    public required string ParFile { get; init; }

    public required IReadOnlyList<ParFileReport> Files { get; init; }

    public required long BlockSize { get; init; }

    public required int BlockCount { get; init; }

    public required int MissingBlocks { get; init; }

    public required int RecoveryBlocksAvailable { get; init; }

    public required bool AllFilesComplete { get; init; }

    public required bool RepairPossible { get; init; }

    public bool CriticalDataMissing { get; init; }

    public IReadOnlyList<string> Creators { get; init; } = [];

    public IReadOnlyList<string> Comments { get; init; } = [];

    /// <summary>The format-specific result (Par1VerifyResult, Par2VerifyResult or Par3VerifyResult), needed for repair.</summary>
    public required object Native { get; init; }
}

public sealed class ParRepairReport
{
    public required bool Success { get; init; }

    public required string Message { get; init; }

    public IReadOnlyList<string> RepairedFiles { get; init; } = [];

    public IReadOnlyList<string> FailedFiles { get; init; } = [];
}

/// <summary>Options for <see cref="ParClient.Create"/>. Format-specific options are ignored by other formats.</summary>
public sealed class ParCreateRequest
{
    /// <summary>Files (and, for PAR3, directories) to protect.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>Output index file; the extension (.par, .par2, .par3) selects the format unless <see cref="Format"/> is set.</summary>
    public required string OutputPath { get; init; }

    public ParFormat? Format { get; init; }

    public string? BaseDirectory { get; init; }

    public double RedundancyPercent { get; init; } = 10;

    public int? RecoveryBlockCount { get; init; }

    public int? BlockCount { get; init; }

    public long? BlockSize { get; init; }

    public int FirstRecoveryBlock { get; init; }

    public int? VolumeCount { get; init; }

    public bool UniformVolumes { get; init; }

    public bool LimitVolumeSize { get; init; }

    public string? Comment { get; init; }

    public bool Recursive { get; init; } = true;

    public Par3Code Par3Code { get; init; } = Par3Code.Auto;

    public Par3Dedup Par3Dedup { get; init; } = Par3Dedup.None;

    public bool Par3PackTails { get; init; } = true;

    public bool Par3StoreData { get; init; }

    public bool Par3UnixPermissions { get; init; }

    public bool Par3FatPermissions { get; init; }

    public bool Par3Reproducible { get; init; }

    public int Par3Interleave { get; init; }

    public int? Par3MaxRecoveryBlocks { get; init; }

    public Par3CauchyConvention Par3CauchyConvention { get; init; } = Par3CauchyConvention.Par3cmdline;
}

public sealed class ParVerifyRequest
{
    public IReadOnlyList<string> ExtraFiles { get; init; } = [];

    public string? BaseDirectory { get; init; }
}

public sealed class ParRepairRequest
{
    public bool PurgeBackups { get; init; }

    public bool RestorePermissions { get; init; }

    public bool RestoreLinks { get; init; }
}

public sealed record ParCreateReport(ParFormat Format, IReadOnlyList<string> WrittenFiles, long BlockSize, long InputBlocks, int RecoveryBlocks, string Description);

/// <summary>One entry point for every PAR version: detects the format and dispatches to the format libraries.</summary>
public static class ParClient
{
    /// <summary>Detects the format of a PAR file by its magic bytes (falls back to the extension).</summary>
    public static ParFormat Detect(string path)
    {
        Span<byte> head = stackalloc byte[8];
        int n = 0;
        if (File.Exists(path))
        {
            using var fs = File.OpenRead(path);
            n = fs.ReadAtLeast(head, 8, throwOnEndOfStream: false);
        }

        if (n == 8)
        {
            if (head.SequenceEqual("PAR2\0PKT"u8))
            {
                return ParFormat.Par2;
            }

            if (head.SequenceEqual("PAR3\0PKT"u8))
            {
                return ParFormat.Par3;
            }

            if (head.SequenceEqual("PAR\0\0\0\0\0"u8))
            {
                return ParFormat.Par1;
            }
        }

        return FromExtension(path) ?? throw new InvalidDataException($"'{path}' is not a recognised PAR file.");
    }

    public static ParFormat? FromExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".par2" => ParFormat.Par2,
            ".par3" => ParFormat.Par3,
            ".par" => ParFormat.Par1,
            _ when ext.Length == 4 && ext[1] >= 'p' && ext[1] <= 'z' && char.IsDigit(ext[2]) && char.IsDigit(ext[3]) => ParFormat.Par1,
            _ => null,
        };
    }

    public static ParCreateReport Create(ParCreateRequest request, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        var format = request.Format ?? FromExtension(request.OutputPath) ?? ParFormat.Par2;
        switch (format)
        {
            case ParFormat.Par1:
            {
                int volumes = request.RecoveryBlockCount ?? Math.Max(1, (int)Math.Ceiling(request.Inputs.Count * request.RedundancyPercent / 100));
                var written = Par1Set.Create(new Par1CreateOptions { InputFiles = request.Inputs, OutputPath = request.OutputPath, RecoveryVolumes = volumes, Comment = request.Comment }, context);
                return new ParCreateReport(format, written, 0, request.Inputs.Count, volumes, "PAR1 Reed-Solomon (GF(2^8), one block per file)");
            }

            case ParFormat.Par2:
            {
                var r = Par2Creator.Create(new Par2CreateOptions
                {
                    InputFiles = ExpandFiles(request.Inputs, request.Recursive),
                    OutputPath = request.OutputPath,
                    BaseDirectory = request.BaseDirectory,
                    SliceSize = request.BlockSize,
                    SliceCount = request.BlockCount ?? 2000,
                    RecoverySliceCount = request.RecoveryBlockCount,
                    RedundancyPercent = request.RedundancyPercent,
                    FirstExponent = request.FirstRecoveryBlock,
                    Scheme = request.UniformVolumes ? Par2VolumeScheme.Uniform : request.LimitVolumeSize ? Par2VolumeScheme.LimitSize : Par2VolumeScheme.Exponential,
                    VolumeCount = request.VolumeCount,
                    Comment = request.Comment,
                }, context);
                return new ParCreateReport(format, r.WrittenFiles, r.SliceSize, r.InputSlices, r.RecoverySlices, "PAR2 Reed-Solomon (GF(2^16))");
            }

            default:
            {
                var r = Par3Creator.Create(new Par3CreateOptions
                {
                    Inputs = request.Inputs,
                    OutputPath = request.OutputPath,
                    BaseDirectory = request.BaseDirectory,
                    Recursive = request.Recursive,
                    BlockSize = request.BlockSize is long bs ? (ulong)bs : null,
                    BlockCount = request.BlockCount ?? 2000,
                    RecoveryBlockCount = request.RecoveryBlockCount,
                    RedundancyPercent = request.RedundancyPercent,
                    FirstRecoveryBlock = (ulong)request.FirstRecoveryBlock,
                    MaxRecoveryBlocks = request.Par3MaxRecoveryBlocks,
                    Code = request.Par3Code,
                    Dedup = request.Par3Dedup,
                    PackTails = request.Par3PackTails,
                    StoreData = request.Par3StoreData,
                    VolumeCount = request.UniformVolumes ? request.VolumeCount : request.VolumeCount,
                    Comment = request.Comment,
                    UnixPermissions = request.Par3UnixPermissions,
                    FatPermissions = request.Par3FatPermissions,
                    Reproducible = request.Par3Reproducible,
                    Interleave = request.Par3Interleave,
                    CauchyConvention = request.Par3CauchyConvention,
                }, context);
                return new ParCreateReport(format, r.WrittenFiles, (long)r.BlockSize, (long)r.InputBlocks, r.RecoveryBlocks, r.CodeDescription);
            }
        }
    }

    public static ParVerifyReport Verify(string parFile, ParVerifyRequest? request = null, OperationContext? context = null)
    {
        request ??= new ParVerifyRequest();
        context ??= OperationContext.Default;
        var format = Detect(parFile);
        switch (format)
        {
            case ParFormat.Par1:
            {
                var r = Par1Set.Verify(parFile, request.ExtraFiles, context);
                return new ParVerifyReport
                {
                    Format = format,
                    ParFile = parFile,
                    Files = r.Files.Select(f => new ParFileReport(f.Entry.Name, f.TargetPath, f.Entry.Size, Map(f.Status), f.FoundAs)).ToList(),
                    BlockSize = 0,
                    BlockCount = r.Files.Count,
                    MissingBlocks = r.MissingSavedFiles,
                    RecoveryBlocksAvailable = r.Volumes.Count,
                    AllFilesComplete = r.AllFilesComplete,
                    RepairPossible = r.RepairPossible,
                    Comments = r.Index.Comment is { Length: > 0 } c ? [c] : [],
                    Native = r,
                };
            }

            case ParFormat.Par2:
            {
                var r = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = parFile, ExtraDataFiles = request.ExtraFiles, BaseDirectory = request.BaseDirectory }, context);
                return new ParVerifyReport
                {
                    Format = format,
                    ParFile = parFile,
                    Files = r.Files.Select(f => new ParFileReport(f.File.Name, f.TargetPath, f.File.Length, Map(f.Status), f.FoundAs)).ToList(),
                    BlockSize = r.Set.SliceSize,
                    BlockCount = r.Set.TotalSlices,
                    MissingBlocks = r.MissingSlices,
                    RecoveryBlocksAvailable = r.RecoverySlicesAvailable,
                    AllFilesComplete = r.AllFilesComplete,
                    RepairPossible = r.RepairPossible,
                    CriticalDataMissing = !r.CriticalDataComplete,
                    Creators = r.Set.Creators,
                    Comments = r.Set.Comments,
                    Native = r,
                };
            }

            default:
            {
                var r = Par3Verifier.Verify(new Par3VerifyOptions { Par3Path = parFile, ExtraDataFiles = request.ExtraFiles, BaseDirectory = request.BaseDirectory }, context);
                return new ParVerifyReport
                {
                    Format = format,
                    ParFile = parFile,
                    Files = r.Files.Select(f => new ParFileReport(f.File.RelativePath, f.TargetPath, (long)f.File.Length, Map(f.Status))).ToList(),
                    BlockSize = (long)r.Set.BlockSize,
                    BlockCount = (int)r.Set.BlockCount,
                    MissingBlocks = r.MissingBlocks,
                    RecoveryBlocksAvailable = r.RecoveryBlocksAvailable,
                    AllFilesComplete = r.AllFilesComplete,
                    RepairPossible = r.RepairPossible,
                    Creators = r.Set.Creators,
                    Comments = r.Set.Comments,
                    Native = r,
                };
            }
        }
    }

    public static ParRepairReport Repair(ParVerifyReport report, ParRepairRequest? request = null, OperationContext? context = null)
    {
        request ??= new ParRepairRequest();
        context ??= OperationContext.Default;
        switch (report.Native)
        {
            case Par1VerifyResult p1:
            {
                bool ok = Par1Set.Repair(p1, context);
                return new ParRepairReport { Success = ok, Message = ok ? "Repair complete." : "Repair failed." };
            }

            case Par2VerifyResult p2:
            {
                var r = Par2Repairer.Repair(p2, new Par2RepairOptions { PurgeBackups = request.PurgeBackups }, context);
                return new ParRepairReport { Success = r.Success, Message = r.Message ?? string.Empty, RepairedFiles = r.RepairedFiles, FailedFiles = r.FailedFiles };
            }

            case Par3VerifyResult p3:
            {
                var r = Par3Repairer.Repair(p3, new Par3RepairOptions { PurgeBackups = request.PurgeBackups, RestorePermissions = request.RestorePermissions, RestoreLinks = request.RestoreLinks }, context);
                return new ParRepairReport { Success = r.Success, Message = r.Message ?? string.Empty, RepairedFiles = r.RepairedFiles, FailedFiles = r.FailedFiles };
            }

            default:
                throw new ArgumentException("Unknown verification result.", nameof(report));
        }
    }

    /// <summary>Expands directories into their files (PAR1/PAR2 protect files only).</summary>
    public static List<string> ExpandFiles(IEnumerable<string> inputs, bool recursive)
    {
        var result = new List<string>();
        foreach (var i in inputs)
        {
            if (Directory.Exists(i))
            {
                result.AddRange(Directory.EnumerateFiles(i, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => !f.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal));
            }
            else
            {
                result.Add(i);
            }
        }

        return result;
    }

    private static ParFileStatus Map(Par1FileStatus s) => s switch
    {
        Par1FileStatus.Complete => ParFileStatus.Complete,
        Par1FileStatus.Damaged => ParFileStatus.Damaged,
        _ => ParFileStatus.Missing,
    };

    private static ParFileStatus Map(Par2FileStatus s) => s switch
    {
        Par2FileStatus.Complete => ParFileStatus.Complete,
        Par2FileStatus.Damaged => ParFileStatus.Damaged,
        Par2FileStatus.Missing => ParFileStatus.Missing,
        _ => ParFileStatus.Unknown,
    };

    private static ParFileStatus Map(Par3FileStatus s) => s switch
    {
        Par3FileStatus.Complete => ParFileStatus.Complete,
        Par3FileStatus.Damaged => ParFileStatus.Damaged,
        _ => ParFileStatus.Missing,
    };
}
