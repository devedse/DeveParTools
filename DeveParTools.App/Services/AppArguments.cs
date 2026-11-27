namespace DeveParTools.App.Services;

/// <summary>
/// The desktop app's command line:
/// <list type="bullet">
/// <item><c>deveparui "set.par2"</c>: open the Verify page with that set (also used by "Verify with
/// DeveParTools" in the Explorer context menu and by file associations);</item>
/// <item><c>deveparui --create "file" "folder" ...</c>: open the Create page with these inputs (used by
/// "Create recovery set with DeveParTools").</item>
/// </list>
/// </summary>
public sealed record AppArguments(IReadOnlyList<string> CreatePaths, string? OpenFile)
{
    public const string CreateSwitch = "--create";

    /// <summary>
    /// "--finish-update &lt;program&gt; &lt;process id&gt;": started by an updating app from the new version's extracted
    /// copy; puts itself at &lt;program&gt; once that process has exited, and starts it (no window of its own).
    /// </summary>
    public const string FinishUpdateSwitch = "--finish-update";

    public static AppArguments Parse(IReadOnlyList<string> args)
    {
        int create = args.ToList().FindIndex(a => string.Equals(a, CreateSwitch, StringComparison.OrdinalIgnoreCase));
        if (create >= 0)
        {
            var paths = args.Skip(create + 1)
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .Select(Path.GetFullPath)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToList();
            return new AppArguments(paths, null);
        }

        return new AppArguments([], args.FirstOrDefault(File.Exists));
    }
}
