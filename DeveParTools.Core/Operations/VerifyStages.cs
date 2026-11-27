namespace DeveParTools.Core.Operations;

/// <summary>The steps of verifying a set (all formats), with what they do. Repair and extend start with them too.</summary>
public static class VerifyStages
{
    public const string Loading = "Loading";
    public const string Verifying = "Verifying";
    public const string Scanning = "Scanning";

    /// <summary>
    /// The verify steps, weighted by the bytes they read: the PAR files, the protected files, and the files that are
    /// searched for moved blocks (0 until it is known whether any are damaged: then the step may not be needed).
    /// </summary>
    public static IReadOnlyList<OperationStage> Plan(long parBytes, long dataBytes, long scanBytes) =>
    [
        new(Loading, Math.Max(1, parBytes), "Reads all PAR files of the set: the list of protected files, the checksum of every block, and which recovery blocks are there."),
        new(Verifying, Math.Max(1, dataBytes), "Reads every protected file and compares each block with its checksum: found in place, damaged or missing."),
        new(Scanning, scanBytes, "Only when files are damaged or you added extra files: searches them byte by byte for blocks that moved, for example after bytes were inserted or removed, or for renamed copies."),
    ];
}

/// <summary>The steps of repairing a set (all formats), with what they do.</summary>
public static class RepairStages
{
    public const string Choosing = "Choosing recovery blocks";
    public const string Copying = "Copying good blocks";
    public const string Reconstructing = "Reconstructing";
    public const string Checking = "Checking rebuilt blocks";
    public const string Writing = "Writing repaired files";
    public const string VerifyingRepaired = "Verifying repaired files";
    public const string Metadata = "Restoring permissions and links";

    public const string ChoosingDescription = "Picks recovery blocks that together can rebuild exactly the missing blocks, and inverts their matrix (Gauss-Jordan elimination in the Galois field): it says how much of every recovery block makes up each missing block.";
    public const string CopyingDescription = "Creates the repaired files next to the originals and copies every good block into place, from wherever it was found.";

    public static string ReconstructingDescription(int missing) => $"Reads the good blocks and the recovery blocks and computes the {missing:N0} missing block(s) from them.";

    public const string CheckingDescription = "Compares every rebuilt block with its checksum.";
    public const string WritingDescription = "Puts each repaired file together from its good and rebuilt blocks, checks the whole file's hash, and only then replaces the damaged file (which is kept as a backup unless you chose otherwise).";
    public const string VerifyingRepairedDescription = "Checks the hash of every repaired file, and only then replaces the damaged file (which is kept as a backup unless you chose otherwise).";
    public const string MetadataDescription = "Applies stored permissions and times and recreates links, when you asked for it.";
}
