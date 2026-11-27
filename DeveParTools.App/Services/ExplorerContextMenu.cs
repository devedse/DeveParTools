using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DeveParTools.App.Services;

/// <summary>
/// The optional Windows Explorer context menu entries. They are registered for the current user only, under
/// HKEY_CURRENT_USER\Software\Classes, so no administrator rights are needed:
/// <list type="bullet">
/// <item>files and folders: "Create recovery set with DeveParTools", which runs
/// <c>"…\DeveParTools.App.exe" --create "%1"</c>;</item>
/// <item>.par, .par2 and .par3 files: "Verify with DeveParTools", which runs <c>"…\DeveParTools.App.exe" "%1"</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>These are classic "static verbs": one registry key per menu entry, holding the menu text, an icon and a
/// command line. The registry after <see cref="Install"/> (with the app at C:\Tools):</para>
/// <code>
/// HKCU\Software\Classes\*\shell\DeveParTools.Create            MUIVerb = Create recovery set with DeveParTools
///                                                               Icon = C:\Tools\DeveParTools.App.exe
///                                                               MultiSelectModel = Player
/// HKCU\Software\Classes\*\shell\DeveParTools.Create\command    (Default) = "C:\Tools\DeveParTools.App.exe" --create "%1"
/// HKCU\Software\Classes\Directory\shell\DeveParTools.Create    (the same, for folders)
/// HKCU\Software\Classes\SystemFileAssociations\.par2\shell\DeveParTools.Verify
///                                                               MUIVerb = Verify with DeveParTools
/// HKCU\Software\Classes\SystemFileAssociations\.par2\shell\DeveParTools.Verify\command
///                                                               (Default) = "C:\Tools\DeveParTools.App.exe" "%1"
/// (and the same for .par and .par3)
/// </code>
/// <para>Explorer runs a static verb's command once per selected item: selecting 20 files starts 20 processes.
/// <see cref="CreateRequestCollector"/> gathers those into one window. "MultiSelectModel = Player" keeps the entry
/// visible for selections of more than 15 items, where Explorer hides it by default.</para>
/// <para>The Verify entries go under SystemFileAssociations\.ext, which adds them whichever program owns the extension
/// (MultiPar, for example) without changing the default program.</para>
/// <para>Windows 11 lists static verbs under "Show more options"; its compact menu only shows entries of packaged
/// apps.</para>
/// <para>Every key created here is named "DeveParTools.*", so <see cref="Uninstall"/> removes exactly those keys
/// (plus parent keys left empty, such as SystemFileAssociations\.par3 when nothing else uses it).</para>
/// </remarks>
public sealed partial class ExplorerContextMenu
{
    public const string CreateVerb = "DeveParTools.Create";
    public const string VerifyVerb = "DeveParTools.Verify";
    public const string CreateText = "Create recovery set with DeveParTools";
    public const string VerifyText = "Verify with DeveParTools";

    /// <summary>The per-user classes key: HKCU\Software\Classes. Tests pass a scratch key instead.</summary>
    public const string UserClasses = @"Software\Classes";

    /// <summary>One menu entry: its key below the classes root, the menu text and the command line.</summary>
    public sealed record Entry(string KeyPath, string Text, string Command, bool MultiSelect);

    public ExplorerContextMenu(string classesRoot = UserClasses)
    {
        ClassesRoot = classesRoot;
    }

    /// <summary>The key (below HKEY_CURRENT_USER) that stands for HKEY_CLASSES_ROOT.</summary>
    public string ClassesRoot { get; }

    /// <summary>Whether this platform has an Explorer context menu.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The executable that the menu entries should start: this process, unless it runs as "dotnet app.dll".</summary>
    public static string? CurrentExecutable
        => Environment.ProcessPath is { } path && !Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? path : null;

    /// <summary>The menu entries for an app at <paramref name="exePath"/>.</summary>
    public static IReadOnlyList<Entry> Entries(string exePath)
    {
        string exe = $"\"{exePath}\"";
        string create = $"{exe} {AppArguments.CreateSwitch} \"%1\"";
        string verify = $"{exe} \"%1\"";
        return
        [
            new($@"*\shell\{CreateVerb}", CreateText, create, true),
            new($@"Directory\shell\{CreateVerb}", CreateText, create, true),
            new($@"SystemFileAssociations\.par\shell\{VerifyVerb}", VerifyText, verify, false),
            new($@"SystemFileAssociations\.par2\shell\{VerifyVerb}", VerifyText, verify, false),
            new($@"SystemFileAssociations\.par3\shell\{VerifyVerb}", VerifyText, verify, false),
        ];
    }

    /// <summary>
    /// The program the installed entries start, or null when they are not installed. Read from the files-and-folders
    /// "Create" entry: <c>"C:\Tools\DeveParTools.App.exe" --create "%1"</c> gives <c>C:\Tools\DeveParTools.App.exe</c>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public string? RegisteredExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{Entries("x")[0].KeyPath}\command");
        return key?.GetValue(null) is string command ? ExecutableOf(command) : null;
    }

    /// <summary>The program of a command line: the quoted first part, or everything before the first space.</summary>
    public static string? ExecutableOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        int space = command.IndexOf(' ');
        return command.Length == 0 ? null : space < 0 ? command : command[..space];
    }

    /// <summary>Adds (or updates) every entry so that it starts <paramref name="exePath"/>.</summary>
    [SupportedOSPlatform("windows")]
    public void Install(string exePath)
    {
        foreach (var entry in Entries(exePath))
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{entry.KeyPath}");
            key.SetValue("MUIVerb", entry.Text);
            key.SetValue("Icon", exePath);
            if (entry.MultiSelect)
            {
                key.SetValue("MultiSelectModel", "Player");
            }

            using var command = key.CreateSubKey("command");
            command.SetValue(null, entry.Command);
        }

        NotifyExplorer();
    }

    /// <summary>Removes every entry, and the parent keys that are left empty.</summary>
    [SupportedOSPlatform("windows")]
    public void Uninstall()
    {
        foreach (var entry in Entries("x"))
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\{entry.KeyPath}", throwOnMissingSubKey: false);

            // Walk up: "SystemFileAssociations\.par3\shell" then "SystemFileAssociations\.par3", deleting each only
            // when nothing else is in it. The top level ("*", "Directory", "SystemFileAssociations") is never touched.
            var parts = entry.KeyPath.Split('\\');
            for (int depth = parts.Length - 1; depth >= 2; depth--)
            {
                var path = $@"{ClassesRoot}\{string.Join('\\', parts.Take(depth))}";
                using (var key = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (key == null || key.SubKeyCount > 0 || key.ValueCount > 0)
                    {
                        break;
                    }
                }

                Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false);
            }
        }

        NotifyExplorer();
    }

    /// <summary>Tells Explorer that file associations changed, so it re-reads the menus.</summary>
    [SupportedOSPlatform("windows")]
    private static void NotifyExplorer() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, 0, 0);

    [LibraryImport("shell32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
