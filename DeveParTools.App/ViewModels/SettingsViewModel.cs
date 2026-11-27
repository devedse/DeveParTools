using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.App.Services;
using DeveParTools.Core.Galois;

namespace DeveParTools.App.ViewModels;

/// <summary>
/// Settings: threads, memory budget, SIMD kernel override and theme, stored in the user's app-data folder; and on
/// Windows the Explorer context menu entries, whose state is the registry itself (see <see cref="ExplorerContextMenu"/>).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly string _path;
    private readonly ExplorerContextMenu _contextMenu;
    private readonly IFileAssociations? _associations;
    private readonly string? _executable;
    private bool _readingExplorerMenu;
    private bool _readingAssociations;

    /// <param name="path">The settings file (default: %AppData%\DeveParTools\settings.txt).</param>
    /// <param name="explorerMenu">Where the context menu entries go (default: the user's real classes key).</param>
    /// <param name="executable">The program the entries start (default: this one).</param>
    /// <param name="associations">The file associations (default: this platform's, see <see cref="FileAssociations.ForThisPlatform"/>).</param>
    public SettingsViewModel(string? path = null, ExplorerContextMenu? explorerMenu = null, string? executable = null, IFileAssociations? associations = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeveParTools", "settings.txt");
        _contextMenu = explorerMenu ?? new ExplorerContextMenu();
        _associations = associations ?? FileAssociations.ForThisPlatform();
        _executable = executable ?? ExplorerContextMenu.CurrentExecutable;
        Kernels = SimdSupport.SupportedLevels.Select(l => l.ToString()).ToArray();
        _kernel = SimdSupport.Best.ToString();
        Load();
        ReadExplorerMenu();
        ReadAssociations();
    }

    /// <summary>Opens links (the Windows Default apps page); set by the window.</summary>
    public IViewServices? Dialogs { get; set; }

    // ---------------------------------------------------------------- file associations

    /// <summary>True where PAR files can be associated with the app (Windows and Linux).</summary>
    public bool AssociationsSupported => _associations != null;

    /// <summary>Whether .par/.par2/.par3 (and PAR1 volumes) open with this app. Changing it registers or removes that.</summary>
    [ObservableProperty]
    private bool _associateFiles;

    [ObservableProperty]
    private bool _associationsEditable;

    /// <summary>Which program opens PAR files, or which types another program still opens (the user's own choice).</summary>
    [ObservableProperty]
    private string? _associationStatus;

    /// <summary>Windows: some types still open with another program, which only the user can change in Settings.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenDefaultAppsCommand))]
    private bool _canOpenDefaultApps;

    partial void OnAssociateFilesChanged(bool value)
    {
        if (_readingAssociations || _associations == null)
        {
            return;
        }

        string? error = null;
        try
        {
            if (!value)
            {
                _associations.Uninstall();
            }
            else if (_executable != null)
            {
                _associations.Install(_executable);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = "The file associations could not be changed: " + e.Message;
        }

        ReadAssociations();
        AssociationStatus = error ?? AssociationStatus;
    }

    /// <summary>Reads the current associations back from the system.</summary>
    public void ReadAssociations()
    {
        if (_associations == null)
        {
            return;
        }

        FileAssociationState state;
        try
        {
            state = _associations.Read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            state = new FileAssociationState(false, null, []);
        }

        _readingAssociations = true;
        AssociateFiles = state.Installed;
        _readingAssociations = false;
        AssociationsEditable = state.Installed || _executable != null;
        CanOpenDefaultApps = state.Installed && state.TakenBy.Count > 0 && _associations.DefaultAppsPage != null;
        AssociationStatus = !state.Installed
            ? _executable == null ? "Start deveparui itself (not through dotnet) to associate the files." : null
            : state.TakenBy.Count > 0
                ? $"{string.Join(", ", state.TakenBy.Select(t => t.Extension))} files still open with {string.Join(", ", state.TakenBy.Select(t => t.Program).Distinct())}, "
                  + (_associations.DefaultAppsPage != null
                      ? "because you chose that program. Windows only lets you change that yourself: click \"Choose in Windows\" and pick DeveParTools."
                      : "set in your default applications.")
                : $"PAR files open with {state.Executable}.";
    }

    [RelayCommand(CanExecute = nameof(CanOpenDefaultApps))]
    private async Task OpenDefaultAppsAsync()
    {
        if (_associations?.DefaultAppsPage is { } page && Dialogs != null)
        {
            await Dialogs.LaunchAsync(page);
        }
    }

    /// <summary>True on Windows, where the Explorer context menu option is shown.</summary>
    public bool ExplorerMenuSupported => ExplorerContextMenu.IsSupported;

    /// <summary>Whether the Explorer entries are installed. Changing it installs or removes them.</summary>
    [ObservableProperty]
    private bool _explorerMenu;

    /// <summary>False when the entries cannot be added because this app runs through "dotnet" instead of its .exe.</summary>
    [ObservableProperty]
    private bool _explorerMenuEditable;

    /// <summary>Which program the entries start, or why they could not be changed.</summary>
    [ObservableProperty]
    private string? _explorerMenuStatus;

    partial void OnExplorerMenuChanged(bool value)
    {
        if (_readingExplorerMenu || !OperatingSystem.IsWindows())
        {
            return;
        }

        string? error = null;
        try
        {
            if (!value)
            {
                _contextMenu.Uninstall();
            }
            else if (_executable != null)
            {
                _contextMenu.Install(_executable);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = "The menu entries could not be changed: " + e.Message;
        }

        ReadExplorerMenu();
        ExplorerMenuStatus = error ?? ExplorerMenuStatus;
    }

    /// <summary>Reads the installed state back from the registry.</summary>
    public void ReadExplorerMenu()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? registered;
        try
        {
            registered = _contextMenu.RegisteredExecutable();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            registered = null;
        }

        _readingExplorerMenu = true;
        ExplorerMenu = registered != null;
        _readingExplorerMenu = false;
        ExplorerMenuEditable = registered != null || _executable != null;
        ExplorerMenuStatus = registered == null
            ? _executable == null ? "Start deveparui.exe itself (not through dotnet) to add the entries." : null
            : string.Equals(registered, _executable, StringComparison.OrdinalIgnoreCase)
                ? $"The entries start {registered}."
                : $"The entries start another copy of the app: {registered}. Turn this off and on again to use this copy.";
    }

    public string[] Kernels { get; }

    public string[] Themes { get; } = ["System", "Light", "Dark"];

    public string CpuFeatures => SimdSupport.Describe();

    public int MaxThreads => Environment.ProcessorCount;

    public string Version => AppInfo.Version;

    public string Runtime => $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";

    [ObservableProperty]
    private int _threads = Environment.ProcessorCount;

    [ObservableProperty]
    private int _memoryMiB = (int)Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4 / (1 << 20), 256, 8192);

    [ObservableProperty]
    private string _kernel;

    [ObservableProperty]
    private string _theme = "System";

    partial void OnThreadsChanged(int value) => Save();

    partial void OnMemoryMiBChanged(int value) => Save();

    partial void OnKernelChanged(string value)
    {
        if (Enum.TryParse<SimdLevel>(value, out var level) && SimdSupport.IsSupported(level))
        {
            Gf16Kernels.Level = level;
            Gf8Kernels.Level = level;
        }

        Save();
    }

    partial void OnThemeChanged(string value)
    {
        ApplyTheme();
        Save();
    }

    public void ApplyTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = Theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        }
    }

    private bool _loading;

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        _loading = true;
        try
        {
            foreach (var line in File.ReadAllLines(_path))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "threads" when int.TryParse(parts[1], out var t) && t > 0:
                        Threads = Math.Min(t, Environment.ProcessorCount);
                        break;
                    case "memory" when int.TryParse(parts[1], out var m) && m >= 64:
                        MemoryMiB = m;
                        break;
                    case "kernel" when Kernels.Contains(parts[1]):
                        Kernel = parts[1];
                        break;
                    case "theme" when Themes.Contains(parts[1]):
                        Theme = parts[1];
                        break;
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, [$"threads={Threads}", $"memory={MemoryMiB}", $"kernel={Kernel}", $"theme={Theme}"]);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
