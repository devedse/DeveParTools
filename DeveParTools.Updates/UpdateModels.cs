namespace DeveParTools.Updates;

/// <summary>A downloadable file of a release, e.g. deveparui-win-x64.zip.</summary>
/// <param name="Size">Its size in bytes, as GitHub reports it.</param>
/// <param name="Sha256">
/// The SHA-256 that GitHub computed when the file was uploaded (lowercase hex), or null when GitHub has none (files
/// uploaded before it started recording them).
/// </param>
public sealed record UpdateAsset(string Name, Uri Url, long Size, string? Sha256);

/// <summary>A published release: GitHub's "latest release", which is never a draft or a pre-release.</summary>
/// <param name="Version">The version from the tag (tag 1.0.41 or v1.0.41 → 1.0.41).</param>
/// <param name="Page">The release's web page.</param>
public sealed record UpdateRelease(Version Version, string Tag, Uri Page, IReadOnlyList<UpdateAsset> Assets);

/// <summary>The result of a check: the running version, the latest release and its download for this system.</summary>
/// <param name="Asset">The download for this system, or null when the release has none (an unsupported system).</param>
public sealed record UpdateCheck(Version Current, UpdateRelease Latest, UpdateAsset? Asset)
{
    /// <summary>Whether the latest release is newer than the running program.</summary>
    public bool UpdateAvailable => Latest.Version > Current;
}

/// <summary>Where an update is: downloading, extracting the archive, or putting the new program in place.</summary>
public enum UpdateStage
{
    Downloading,
    Extracting,
    Installing,
}

/// <summary>Progress of one stage: bytes done of the total (download: received; extract: archive bytes read).</summary>
public readonly record struct UpdateProgress(UpdateStage Stage, long Done, long Total)
{
    /// <summary>Done ÷ total, between 0 and 1 (0 while the total is unknown).</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

/// <summary>Whether the running program can replace itself, and if not, why.</summary>
/// <param name="Executable">The program file an update replaces, or null when this build cannot update itself.</param>
/// <param name="Reason">Why it cannot (for the user), or null.</param>
public sealed record UpdateTarget(string? Executable, string? Reason);

/// <summary>An update could not be checked, downloaded or installed. The message says why, in words for the user.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
