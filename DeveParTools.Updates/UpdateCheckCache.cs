using System.Globalization;

namespace DeveParTools.Updates;

/// <summary>
/// The answer of the last automatic update check, so that GitHub is asked at most once a day (it allows 60 requests
/// an hour per IP address without an account, and a command line tool can be started many times a minute).
/// </summary>
/// <remarks>
/// <para>A small text file, shared by devepar and deveparui (they are released together):</para>
/// <code>
/// checked=2026-11-27T14:05:00.0000000+00:00
/// latest=1.0.41
/// </code>
/// <para>Reading or writing it never fails: without the file the program simply asks GitHub again.</para>
/// </remarks>
public sealed class UpdateCheckCache(string path)
{
    /// <summary>%LOCALAPPDATA%\DeveParTools\update-check.txt (Linux: ~/.local/share/DeveParTools/update-check.txt).</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeveParTools", "update-check.txt");

    public string FilePath { get; } = path;

    /// <summary>When GitHub was last asked and the latest version it reported, or null when unknown.</summary>
    public (DateTimeOffset Checked, Version Latest)? Read()
    {
        try
        {
            DateTimeOffset? at = null;
            Version? latest = null;
            foreach (var line in File.ReadLines(FilePath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                if (parts[0] == "checked" && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
                {
                    at = t;
                }
                else if (parts[0] == "latest" && Version.TryParse(parts[1], out var v))
                {
                    latest = v;
                }
            }

            return at is { } a && latest != null ? (a, latest) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(DateTimeOffset at, Version latest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            File.WriteAllLines(FilePath, [$"checked={at.ToString("O", CultureInfo.InvariantCulture)}", $"latest={latest}"]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
