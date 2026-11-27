using DeveParTools.Core.Operations;

namespace DeveParTools.Par2;

/// <summary>Options for <see cref="Par2Extender.Extend"/>.</summary>
public sealed class Par2ExtendOptions
{
    /// <summary>Any .par2 file of the existing set.</summary>
    public required string Par2Path { get; init; }

    public string? BaseDirectory { get; init; }

    /// <summary>Exact number of new recovery slices (else <see cref="RedundancyPercent"/> of the input slices).</summary>
    public int? RecoverySliceCount { get; init; }

    public double RedundancyPercent { get; init; } = 10;

    /// <summary>Exponent of the first new recovery slice. Default: one past the highest exponent in the set.</summary>
    public int? FirstExponent { get; init; }

    public Par2VolumeScheme Scheme { get; init; } = Par2VolumeScheme.Exponential;

    public int? VolumeCount { get; init; }

    /// <summary>Base name of the new volume files. Default: the set's index file name.</summary>
    public string? OutputPath { get; init; }

    public string Creator { get; init; } = Par2Creator.DefaultCreator;
}

/// <summary>
/// Adds recovery volumes to an existing PAR2 set. par2cmdline can do the same with <c>par2 create -f&lt;first&gt;</c>
/// over the same files.
/// </summary>
/// <remarks>
/// <para>A PAR2 set is fully determined by its slice size and its files. Each file's ID is MD5(MD5 of the first
/// 16 KiB ‖ length ‖ name), and the recovery set ID is the MD5 of the main packet, which lists the slice size and the
/// file IDs. So with every file complete at its stored name, creating "again" with the same slice size gives the
/// same set ID and the same file description and checksum packets. Only the exponents of the new recovery slices
/// differ: recovery slice e is Σ c_i^e · slice_i, and every exponent that is not used yet gives a new, independent
/// slice.</para>
/// <para>The index file is left as it is; only <c>name.volFIRST+COUNT.par2</c> files are written. If the recomputed set
/// ID differs from the set's (for example because another program put an empty file into the recovery set, which this
/// library would not), the new volumes are deleted again and an error is raised.</para>
/// </remarks>
public static class Par2Extender
{
    public static Par2CreateResult Extend(Par2ExtendOptions options, OperationContext? context = null)
    {
        context ??= OperationContext.Default;
        var verify = Par2Verifier.Verify(new Par2VerifyOptions { Par2Path = options.Par2Path, BaseDirectory = options.BaseDirectory }, context);
        if (!verify.AllFilesComplete)
        {
            throw new InvalidOperationException("Every file of the set must be complete and at its stored name. Repair the files before extending the set.");
        }

        var set = verify.Set;
        var used = set.RecoverySlices.Keys.ToHashSet();
        int first = options.FirstExponent ?? (used.Count == 0 ? 0 : (int)used.Max() + 1);
        int count = options.RecoverySliceCount ?? (int)Math.Ceiling(set.TotalSlices * options.RedundancyPercent / 100.0);
        if (count <= 0)
        {
            throw new ArgumentException("At least one new recovery slice is needed.");
        }

        if (Enumerable.Range(first, count).FirstOrDefault(e => e >= 0 && used.Contains((uint)e), -1) is int clash and >= 0)
        {
            throw new ArgumentException($"Recovery slice {clash} already exists; choose another first exponent.");
        }

        var indexPath = options.OutputPath ?? IndexPathOf(options.Par2Path);
        var result = Par2Creator.Create(new Par2CreateOptions
        {
            InputFiles = verify.Files.Select(f => f.TargetPath).ToList(),
            OutputPath = indexPath,
            BaseDirectory = verify.BaseDirectory,
            SliceSize = set.SliceSize,
            RecoverySliceCount = count,
            FirstExponent = first,
            Scheme = options.Scheme,
            VolumeCount = options.VolumeCount,
            Comment = set.Comments.FirstOrDefault(),
            Creator = options.Creator,
            WriteIndexFile = false,
        }, context);

        if (result.SetId != set.SetId)
        {
            foreach (var f in result.WrittenFiles)
            {
                File.Delete(f);
            }

            throw new InvalidDataException("The set could not be reproduced from its files (different recovery set ID), so compatible recovery volumes cannot be made.");
        }

        return result;
    }

    /// <summary>The index file name for a set file: <c>name.vol10+5.par2</c> becomes <c>name.par2</c>.</summary>
    public static string IndexPathOf(string par2Path)
        => Path.Combine(Path.GetDirectoryName(par2Path) ?? string.Empty, Par2RecoverySet.BaseNameOf(Path.GetFileName(par2Path)) + ".par2");
}
