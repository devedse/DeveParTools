using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace DeveParTools.App.Services;

/// <summary>What the file associations look like now.</summary>
/// <param name="Installed">Whether DeveParTools is registered for the PAR file types.</param>
/// <param name="Executable">The program the registration starts (null when not installed).</param>
/// <param name="TakenBy">Extensions that still open with another program the user chose (extension, program).</param>
public sealed record FileAssociationState(bool Installed, string? Executable, IReadOnlyList<(string Extension, string Program)> TakenBy);

/// <summary>
/// Opening .par, .par2, .par3 and PAR1 volume files (.p01 … .p99) with the desktop app, for the current user only.
/// </summary>
public interface IFileAssociations
{
    /// <summary>Registers the file types for <paramref name="executable"/> and makes it their default where the system allows.</summary>
    void Install(string executable);

    /// <summary>Removes the registration and restores the previous defaults.</summary>
    void Uninstall();

    FileAssociationState Read();

    /// <summary>Where the user can pick the default program when the system does not let an app change it (Windows), or null.</summary>
    Uri? DefaultAppsPage { get; }
}

/// <summary>The file associations of this platform: Windows registry, Linux XDG files, or none (macOS needs an app bundle).</summary>
public static class FileAssociations
{
    /// <summary>The file extensions: the index files, plus PAR1's recovery volumes .p01 … .p99.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".par", ".par2", ".par3", .. Enumerable.Range(1, 99).Select(i => $".p{i:00}")];

    public static IFileAssociations? ForThisPlatform()
        => OperatingSystem.IsWindows() ? new WindowsFileAssociations() : OperatingSystem.IsLinux() ? new LinuxFileAssociations() : null;
}

/// <summary>
/// Windows: per-user file types under HKEY_CURRENT_USER (no administrator rights needed).
/// </summary>
/// <remarks>
/// <para>The registry after <see cref="Install"/> with the app at C:\Tools\deveparui.exe (root = "Software"):</para>
/// <code>
/// HKCU\Software\Classes\DeveParTools.par2                       (Default) = PAR2 recovery file
///   DefaultIcon                                                 (Default) = "C:\Tools\deveparui.exe",0
///   shell\open\command                                          (Default) = "C:\Tools\deveparui.exe" "%1"
/// HKCU\Software\Classes\.par2                                   (Default) = DeveParTools.par2   (the previous value is kept in DeveParTools.Previous)
/// HKCU\Software\Classes\.par2\OpenWithProgids                   DeveParTools.par2 (listed under "Open with")
/// HKCU\Software\Classes\Applications\deveparui.exe              FriendlyAppName = DeveParTools, SupportedTypes = the extensions
/// HKCU\Software\DeveParTools\Capabilities\FileAssociations      .par2 = DeveParTools.par2 …   (for Settings > Default apps)
/// HKCU\Software\RegisteredApplications                          DeveParTools = Software\DeveParTools\Capabilities
/// </code>
/// <para>(.par and .p01 … .p99 use DeveParTools.par, .par3 DeveParTools.par3.)</para>
/// <para>The default program of a type is the class key's default value, unless the user chose a program with
/// "Open with > Always" or in Settings. That choice (Explorer\FileExts\.ext\UserChoice) is protected by a hash, so no
/// app can change it; <see cref="Read"/> reports those types and <see cref="DefaultAppsPage"/> opens the Settings page
/// where DeveParTools can be picked.</para>
/// </remarks>
public sealed partial class WindowsFileAssociations : IFileAssociations
{
    public const string AppName = "DeveParTools";
    private const string PreviousValue = "DeveParTools.Previous";

    private readonly string _root;

    /// <param name="root">The key below HKEY_CURRENT_USER that stands for "Software" (tests use a scratch key).</param>
    public WindowsFileAssociations(string root = "Software")
    {
        _root = root;
    }

    public Uri DefaultAppsPage { get; } = new("ms-settings:defaultapps?registeredAppUser=" + AppName);

    private string Classes => $@"{_root}\Classes";

    private string Capabilities => $@"{_root}\{AppName}\Capabilities";

    private static string ProgIdOf(string extension) => extension switch
    {
        ".par2" => "DeveParTools.par2",
        ".par3" => "DeveParTools.par3",
        _ => "DeveParTools.par",
    };

    private static string DescriptionOf(string progId) => progId switch
    {
        "DeveParTools.par2" => "PAR2 recovery file",
        "DeveParTools.par3" => "PAR3 recovery file",
        _ => "PAR1 recovery file",
    };

    public void Install(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var user = Registry.CurrentUser;
        string open = $"\"{executable}\" \"%1\"";
        foreach (var progId in FileAssociations.Extensions.Select(ProgIdOf).Distinct())
        {
            using var key = user.CreateSubKey($@"{Classes}\{progId}");
            key.SetValue(null, DescriptionOf(progId));
            key.SetValue("FriendlyTypeName", DescriptionOf(progId));
            using (var icon = key.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, $"\"{executable}\",0");
            }

            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(null, open);
        }

        foreach (var ext in FileAssociations.Extensions)
        {
            using var key = user.CreateSubKey($@"{Classes}\{ext}");
            var current = key.GetValue(null) as string;
            if (current != null && !current.StartsWith(AppName + ".", StringComparison.Ordinal))
            {
                key.SetValue(PreviousValue, current);
            }

            key.SetValue(null, ProgIdOf(ext));
            using var openWith = key.CreateSubKey("OpenWithProgids");
            openWith.SetValue(ProgIdOf(ext), Array.Empty<byte>(), RegistryValueKind.None);
        }

        string appKey = Path.GetFileName(executable);
        using (var app = user.CreateSubKey($@"{Classes}\Applications\{appKey}"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var types = app.CreateSubKey("SupportedTypes"))
            {
                foreach (var ext in FileAssociations.Extensions)
                {
                    types.SetValue(ext, string.Empty);
                }
            }

            using var command = app.CreateSubKey(@"shell\open\command");
            command.SetValue(null, open);
        }

        using (var caps = user.CreateSubKey(Capabilities))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "Create, verify and repair PAR1, PAR2 and PAR3 recovery sets.");
            caps.SetValue("ApplicationKey", appKey);
            using var assoc = caps.CreateSubKey("FileAssociations");
            foreach (var ext in FileAssociations.Extensions)
            {
                assoc.SetValue(ext, ProgIdOf(ext));
            }
        }

        using (var registered = user.CreateSubKey($@"{_root}\RegisteredApplications"))
        {
            registered.SetValue(AppName, Capabilities);
        }

        NotifyExplorer();
    }

    public void Uninstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var user = Registry.CurrentUser;
        foreach (var ext in FileAssociations.Extensions)
        {
            using (var key = user.OpenSubKey($@"{Classes}\{ext}", writable: true))
            {
                if (key == null)
                {
                    continue;
                }

                if (key.GetValue(null) is string current && current == ProgIdOf(ext))
                {
                    if (key.GetValue(PreviousValue) is string previous)
                    {
                        key.SetValue(null, previous);
                    }
                    else
                    {
                        key.DeleteValue(string.Empty, throwOnMissingValue: false);
                    }
                }

                key.DeleteValue(PreviousValue, throwOnMissingValue: false);
                using (var openWith = key.OpenSubKey("OpenWithProgids", writable: true))
                {
                    openWith?.DeleteValue(ProgIdOf(ext), throwOnMissingValue: false);
                }

                DeleteIfEmpty(key, "OpenWithProgids");
            }

            DeleteIfEmpty(user, $@"{Classes}\{ext}");
        }

        foreach (var progId in FileAssociations.Extensions.Select(ProgIdOf).Distinct())
        {
            user.DeleteSubKeyTree($@"{Classes}\{progId}", throwOnMissingSubKey: false);
        }

        using (var caps = user.OpenSubKey(Capabilities))
        {
            if (caps?.GetValue("ApplicationKey") is string appKey)
            {
                user.DeleteSubKeyTree($@"{Classes}\Applications\{appKey}", throwOnMissingSubKey: false);
            }
        }

        user.DeleteSubKeyTree(Capabilities, throwOnMissingSubKey: false);
        DeleteIfEmpty(user, $@"{_root}\{AppName}");
        using (var registered = user.OpenSubKey($@"{_root}\RegisteredApplications", writable: true))
        {
            registered?.DeleteValue(AppName, throwOnMissingValue: false);
        }

        NotifyExplorer();
    }

    public FileAssociationState Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileAssociationState(false, null, []);
        }

        var user = Registry.CurrentUser;
        string? executable;
        using (var command = user.OpenSubKey($@"{Classes}\DeveParTools.par2\shell\open\command"))
        {
            executable = command?.GetValue(null) is string c ? ExplorerContextMenu.ExecutableOf(c) : null;
        }

        var taken = new List<(string, string)>();
        foreach (var ext in new[] { ".par", ".par2", ".par3" })
        {
            using var choice = user.OpenSubKey($@"{_root}\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice");
            if (choice?.GetValue("ProgId") is string progId && progId != ProgIdOf(ext) && !progId.EndsWith(@"\deveparui.exe", StringComparison.OrdinalIgnoreCase))
            {
                taken.Add((ext, progId));
            }
        }

        return new FileAssociationState(executable != null, executable, taken);
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteIfEmpty(RegistryKey parent, string name)
    {
        using (var key = parent.OpenSubKey(name))
        {
            if (key == null || key.SubKeyCount > 0 || key.ValueCount > 0)
            {
                return;
            }
        }

        parent.DeleteSubKey(name, throwOnMissingSubKey: false);
    }

    [SupportedOSPlatform("windows")]
    private static void NotifyExplorer() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);

    [LibraryImport("shell32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

/// <summary>
/// Linux (freedesktop.org): a desktop entry, MIME types and the user's default applications, all in the user's own
/// folders.
/// </summary>
/// <remarks>
/// <para>Files written by <see cref="Install"/> (XDG_DATA_HOME defaults to ~/.local/share, XDG_CONFIG_HOME to
/// ~/.config):</para>
/// <code>
/// $XDG_DATA_HOME/applications/deveparui.desktop      Exec="/opt/deveparui/deveparui" %f, MimeType=application/x-par2;…
/// $XDG_DATA_HOME/mime/packages/deveparui.xml         application/x-par3 (*.par3) and application/x-par (*.par, *.p01 …)
/// $XDG_DATA_HOME/icons/hicolor/256x256/apps/deveparui.png
/// $XDG_CONFIG_HOME/mimeapps.list                     [Default Applications] application/x-par2=deveparui.desktop …
/// </code>
/// <para>application/x-par2 already comes with shared-mime-info; the other two are added. update-mime-database and
/// update-desktop-database run afterwards when they are installed (file managers also notice the files on their own).
/// Other lines of mimeapps.list are kept.</para>
/// </remarks>
public sealed class LinuxFileAssociations : IFileAssociations
{
    public const string DesktopId = "deveparui.desktop";
    private static readonly string[] MimeTypes = ["application/x-par2", "application/x-par3", "application/x-par"];

    private readonly string _dataHome;
    private readonly string _configHome;
    private readonly Func<Stream?> _icon;
    private readonly bool _runTools;

    /// <param name="dataHome">XDG_DATA_HOME (default: the environment variable or ~/.local/share).</param>
    /// <param name="configHome">XDG_CONFIG_HOME (default: the environment variable or ~/.config).</param>
    /// <param name="icon">The 256 px icon (default: the app's own).</param>
    /// <param name="runTools">Run update-mime-database and update-desktop-database (tests turn this off).</param>
    public LinuxFileAssociations(string? dataHome = null, string? configHome = null, Func<Stream?>? icon = null, bool runTools = true)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _dataHome = dataHome ?? NonEmpty(Environment.GetEnvironmentVariable("XDG_DATA_HOME")) ?? Path.Combine(home, ".local", "share");
        _configHome = configHome ?? NonEmpty(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
        _icon = icon ?? (() => typeof(LinuxFileAssociations).Assembly.GetManifestResourceStream("deveparui.icon.png"));
        _runTools = runTools;
    }

    public Uri? DefaultAppsPage => null;

    public string DesktopFile => Path.Combine(_dataHome, "applications", DesktopId);

    public string MimePackage => Path.Combine(_dataHome, "mime", "packages", "deveparui.xml");

    public string IconFile => Path.Combine(_dataHome, "icons", "hicolor", "256x256", "apps", "deveparui.png");

    public string MimeAppsList => Path.Combine(_configHome, "mimeapps.list");

    public void Install(string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MimePackage)!);
        File.WriteAllText(MimePackage, """
            <?xml version="1.0" encoding="UTF-8"?>
            <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
              <mime-type type="application/x-par3">
                <comment>PAR3 recovery file</comment>
                <magic priority="60"><match offset="0" type="string" value="PAR3\0PKT"/></magic>
                <glob pattern="*.par3"/>
              </mime-type>
              <mime-type type="application/x-par">
                <comment>PAR1 recovery file</comment>
                <magic priority="60"><match offset="0" type="string" value="PAR\0\0\0\0\0"/></magic>
                <glob pattern="*.par"/>
                <glob pattern="*.p[0-9][0-9]"/>
              </mime-type>
            </mime-info>
            """);

        Directory.CreateDirectory(Path.GetDirectoryName(IconFile)!);
        using (var icon = _icon())
        {
            if (icon != null)
            {
                using var file = File.Create(IconFile);
                icon.CopyTo(file);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
        File.WriteAllText(DesktopFile, $"""
            [Desktop Entry]
            Type=Application
            Name=DeveParTools
            Comment=Create, verify and repair PAR1, PAR2 and PAR3 recovery sets
            Exec={QuoteExec(executable)} %f
            Icon=deveparui
            Terminal=false
            Categories=Utility;Archiving;
            MimeType={string.Join(';', MimeTypes)};

            """);

        EditMimeApps(lines =>
        {
            foreach (var mime in MimeTypes)
            {
                SetEntry(lines, "Default Applications", mime, DesktopId);
            }
        });
        RunTools();
    }

    public void Uninstall()
    {
        foreach (var f in new[] { DesktopFile, MimePackage, IconFile })
        {
            File.Delete(f);
        }

        if (File.Exists(MimeAppsList))
        {
            EditMimeApps(lines => lines.RemoveAll(l => l.Contains('=') && l[(l.IndexOf('=') + 1)..].Trim().TrimEnd(';') == DesktopId));
        }

        RunTools();
    }

    public FileAssociationState Read()
    {
        if (!File.Exists(DesktopFile))
        {
            return new FileAssociationState(false, null, []);
        }

        var exec = File.ReadLines(DesktopFile).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        string? executable = exec == null ? null : UnquoteExec(exec[5..]);
        var lines = File.Exists(MimeAppsList) ? File.ReadAllLines(MimeAppsList).ToList() : [];
        var taken = new List<(string, string)>();
        foreach (var (mime, ext) in new[] { ("application/x-par", ".par"), ("application/x-par2", ".par2"), ("application/x-par3", ".par3") })
        {
            if (GetEntry(lines, "Default Applications", mime) is { } app && app.TrimEnd(';') != DesktopId)
            {
                taken.Add((ext, app.TrimEnd(';')));
            }
        }

        return new FileAssociationState(true, executable, taken);
    }

    /// <summary>
    /// An Exec value for a program path (desktop entry spec): quoted, with ", `, $ and \ escaped by a backslash, and
    /// every backslash then doubled because the value itself is a string with escapes.
    /// </summary>
    internal static string QuoteExec(string path)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in path)
        {
            if (ch is '"' or '`' or '$' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.Append('"').ToString().Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    /// <summary>The program of an Exec value: the reverse of <see cref="QuoteExec"/> (an unquoted value ends at the first space).</summary>
    internal static string? UnquoteExec(string value)
    {
        value = value.Replace("\\\\", "\\", StringComparison.Ordinal).Trim();
        if (!value.StartsWith('"'))
        {
            return ExplorerContextMenu.ExecutableOf(value);
        }

        var sb = new StringBuilder();
        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                sb.Append(value[++i]);
            }
            else if (value[i] == '"')
            {
                return sb.ToString();
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return null;
    }

    private void EditMimeApps(Action<List<string>> edit)
    {
        Directory.CreateDirectory(_configHome);
        var lines = File.Exists(MimeAppsList) ? File.ReadAllLines(MimeAppsList).ToList() : [];
        edit(lines);
        File.WriteAllLines(MimeAppsList, lines);
    }

    /// <summary>Sets key=value in [section], adding the section at the end when it is missing.</summary>
    internal static void SetEntry(List<string> lines, string section, string key, string value)
    {
        int start = lines.FindIndex(l => l.Trim() == $"[{section}]");
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }

        int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        end = end < 0 ? lines.Count : end;
        int existing = lines.FindIndex(start + 1, end - start - 1, l => l.Split('=', 2)[0].Trim() == key);
        if (existing >= 0)
        {
            lines[existing] = $"{key}={value}";
        }
        else
        {
            // Before any blank lines that end the section.
            int at = end;
            while (at > start + 1 && string.IsNullOrWhiteSpace(lines[at - 1]))
            {
                at--;
            }

            lines.Insert(at, $"{key}={value}");
        }
    }

    internal static string? GetEntry(List<string> lines, string section, string key)
    {
        int start = lines.FindIndex(l => l.Trim() == $"[{section}]");
        if (start < 0)
        {
            return null;
        }

        for (int i = start + 1; i < lines.Count && !lines[i].TrimStart().StartsWith('['); i++)
        {
            var parts = lines[i].Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim() == key)
            {
                return parts[1].Trim();
            }
        }

        return null;
    }

    private void RunTools()
    {
        if (!_runTools)
        {
            return;
        }

        Run("update-mime-database", Path.Combine(_dataHome, "mime"));
        Run("update-desktop-database", Path.Combine(_dataHome, "applications"));
    }

    private static void Run(string tool, string argument)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, [argument]) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
            p?.WaitForExit(10_000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The tool is not installed; desktops also pick the files up on their own.
        }
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
