using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.App.Services;
using DeveParTools.Updates;

namespace DeveParTools.App.ViewModels;

/// <summary>Where the app's self-update is.</summary>
public enum UpdateState
{
    /// <summary>Not checked yet.</summary>
    Idle,
    Checking,
    UpToDate,
    Available,

    /// <summary>Downloading, extracting or putting the new program in place.</summary>
    Installing,

    /// <summary>Installed; the new version is being started.</summary>
    Restarting,

    /// <summary>The last check or installation failed (see <see cref="UpdateViewModel.Error"/>).</summary>
    Failed,
}

/// <summary>
/// The update banner, the Updates card in Settings, and the update window: checks GitHub for a newer release and
/// installs it with <see cref="Updater"/> (download → extract → swap the program file), then restarts the app.
/// </summary>
/// <remarks>
/// The automatic check runs when the window opens (if enabled in Settings) and asks GitHub at most once a day; the
/// answer is shared with the command line tool (see <see cref="UpdateCheckCache"/>). It is skipped for development
/// builds, which cannot update themselves. "Check now" always asks GitHub.
/// </remarks>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly Func<Updater> _newUpdater;
    private readonly UpdateCheckCache _cache;
    private readonly Action<string, string> _restart;
    private readonly UpdateTarget _target;
    private UpdateCheck? _check;
    private CancellationTokenSource? _cancel;
    private Stopwatch? _downloadClock;

    /// <param name="newUpdater">Makes the updater (tests pass one with a fake GitHub).</param>
    /// <param name="cache">Where the last automatic check is remembered (default: shared with devepar).</param>
    /// <param name="processPath">The program to replace (default: this one).</param>
    /// <param name="restart">
    /// Hands over to the new program (its extracted copy, then this program's path) and closes this one; tests record it
    /// instead. See <see cref="Updater"/> for why the new copy puts itself in place.
    /// </param>
    public UpdateViewModel(Func<Updater>? newUpdater = null, UpdateCheckCache? cache = null, string? processPath = null, Action<string, string>? restart = null)
    {
        _newUpdater = newUpdater ?? (() => new Updater("deveparui", Version.Parse(AppInfo.Version)));
        _cache = cache ?? new UpdateCheckCache(UpdateCheckCache.DefaultPath);
        _restart = restart ?? Restart;
        using (var updater = _newUpdater())
        {
            CurrentVersion = updater.Current.ToString();
            _target = Updater.FindTarget(processPath ?? Environment.ProcessPath, updater.Current, "deveparui");
        }
    }

    /// <summary>Opens the update window and links; set by the main window.</summary>
    public IViewServices? Dialogs { get; set; }

    public string CurrentVersion { get; }

    /// <summary>Whether this build can replace itself (released single-file builds only).</summary>
    public bool CanSelfUpdate => _target.Executable != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsInstalling), nameof(IsUpdateAvailable), nameof(ShowInstallButton), nameof(InstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(CheckNowCommand), nameof(CancelCommand))]
    private UpdateState _state;

    /// <summary>The newest version GitHub reported, e.g. "1.0.41".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerText), nameof(InstallButtonText), nameof(WindowTitle), nameof(ShowInstallButton))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private string? _latestVersion;

    /// <summary>One line for the Settings card: up to date, available, or why it failed.</summary>
    [ObservableProperty]
    private string? _statusText;

    /// <summary>The "update available" banner above the pages (closing it hides it until the next start).</summary>
    [ObservableProperty]
    private bool _bannerVisible;

    /// <summary>"Step 1 of 3: Downloading deveparui-win-x64.zip".</summary>
    [ObservableProperty]
    private string? _stageText;

    /// <summary>Progress of the current step, 0–100.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _progress;

    /// <summary>True until the first progress arrives (asking GitHub for the download): the bar moves without a percentage.</summary>
    [ObservableProperty]
    private bool _isIndeterminate;

    /// <summary>"12.3 MB of 28.5 MB · 8.1 MB/s · about 2 s left".</summary>
    [ObservableProperty]
    private string? _progressText;

    /// <summary>Why the last check or installation failed, in words for the user.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>The release's web page (what is new).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReleasePageCommand))]
    private Uri? _releasePage;

    /// <summary>What the update window says before installing: where it comes from, how big it is, what happens.</summary>
    [ObservableProperty]
    private string? _windowText;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Installing or UpdateState.Restarting;

    /// <summary>Downloading, extracting or replacing: the time the update can still be cancelled.</summary>
    public bool IsInstalling => State == UpdateState.Installing;

    public bool IsUpdateAvailable => State == UpdateState.Available || (State is UpdateState.Failed or UpdateState.Installing or UpdateState.Restarting && _check?.UpdateAvailable == true);

    /// <summary>The window's main button: shown when an update can be (re)tried.</summary>
    public bool ShowInstallButton => State is UpdateState.Available or UpdateState.Failed && LatestVersion != null;

    public string PercentText => $"{Progress:0}%";

    public string BannerText => $"DeveParTools {LatestVersion} is available. You have {CurrentVersion}.";

    public string InstallButtonText => State == UpdateState.Failed ? "Try again" : "Update and restart";

    public string WindowTitle => LatestVersion == null ? "Update DeveParTools" : $"DeveParTools {LatestVersion}";

    /// <summary>When the window opens: asks GitHub at most once a day (shared with devepar), and shows the banner for a newer version.</summary>
    public async Task CheckAutomaticallyAsync()
    {
        if (!CanSelfUpdate || IsBusy)
        {
            return;
        }

        using var updater = _newUpdater();
        if (await updater.LatestForAutomaticCheckAsync(_cache, DateTimeOffset.UtcNow) is { } latest && latest > updater.Current && State is UpdateState.Idle or UpdateState.UpToDate)
        {
            LatestVersion = latest.ToString();
            ReleasePage = Updater.ReleasePage(latest);
            State = UpdateState.Available;
            StatusText = BannerText;
            WindowText = DescribeInstall(null);
            BannerVisible = true;
        }
    }

    private bool CanCheckNow => !IsBusy;

    /// <summary>Asks GitHub now (Settings' "Check now").</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        State = UpdateState.Checking;
        StatusText = "Checking for updates…";
        Error = null;
        try
        {
            using var updater = _newUpdater();
            Show(await updater.CheckAsync(_cache));
        }
        catch (UpdateException e)
        {
            Fail("Could not check for updates: " + e.Message);
        }
    }

    private void Show(UpdateCheck check)
    {
        _check = check;
        LatestVersion = check.Latest.Version.ToString();
        ReleasePage = check.Latest.Page;
        if (!check.UpdateAvailable)
        {
            State = UpdateState.UpToDate;
            StatusText = $"DeveParTools {CurrentVersion} is the latest version.";
            BannerVisible = false;
            return;
        }

        State = UpdateState.Available;
        StatusText = BannerText + (CanSelfUpdate ? string.Empty : " " + _target.Reason);
        WindowText = DescribeInstall(check);
        BannerVisible = true;
    }

    private string DescribeInstall(UpdateCheck? check)
        => !CanSelfUpdate ? _target.Reason!
            : check == null ? "The new version is downloaded from GitHub, replaces this program and starts in its place."
            : check.Asset is { } asset ? $"{asset.Name} ({Formatting.Bytes(asset.Size)}) is downloaded from GitHub, checked, and replaces this program. DeveParTools then restarts."
            : $"Release {check.Latest.Version} has no download for this system. See its page for what is available.";

    /// <summary>Opens the update window (the banner's and Settings' "Update…" button).</summary>
    [RelayCommand]
    private async Task OpenUpdateWindowAsync()
    {
        if (Dialogs != null)
        {
            await Dialogs.ShowUpdateAsync(this);
        }
    }

    private bool CanInstall => State is UpdateState.Available or UpdateState.Failed && CanSelfUpdate && LatestVersion != null;

    /// <summary>Downloads, checks and installs the new version, then restarts the app.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        State = UpdateState.Installing;
        Error = null;
        Progress = 0;
        ProgressText = null;
        StageText = "Checking GitHub for the download";
        IsIndeterminate = true;
        using var cancel = _cancel = new CancellationTokenSource();
        try
        {
            using var updater = _newUpdater();
            var check = await updater.CheckAsync(_cache, cancel.Token);
            _check = check;
            LatestVersion = check.Latest.Version.ToString();
            ReleasePage = check.Latest.Page;
            var asset = check.Asset ?? throw new UpdateException(DescribeInstall(check));
            WindowText = DescribeInstall(check);
            string staged = await updater.PrepareAsync(asset, _target.Executable!, new Progress<UpdateProgress>(p => OnProgress(asset, p)), cancel.Token);
            State = UpdateState.Restarting;
            StageText = $"Step 3 of 3: Installing DeveParTools {LatestVersion} and restarting…";
            ProgressText = null;
            Progress = 100;
            _restart(staged, _target.Executable!);
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Available;
            StageText = null;
            StatusText = BannerText;
        }
        catch (Exception e) when (e is UpdateException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Fail("The update failed: " + e.Message);
        }
        finally
        {
            _cancel = null;
            IsIndeterminate = false;
        }
    }

    private void OnProgress(UpdateAsset asset, UpdateProgress p)
    {
        if (State != UpdateState.Installing)
        {
            return; // a late report after cancelling
        }

        IsIndeterminate = false;
        Progress = p.Fraction * 100;
        switch (p.Stage)
        {
            case UpdateStage.Downloading:
                _downloadClock ??= Stopwatch.StartNew();
                StageText = $"Step 1 of 3: Downloading {asset.Name}";
                double seconds = _downloadClock.Elapsed.TotalSeconds;
                double speed = seconds > 0.2 ? p.Done / seconds : 0;
                ProgressText = $"{Formatting.Bytes(p.Done)} of {Formatting.Bytes(p.Total)}"
                    + (speed > 0 ? $" · {Formatting.Bytes((long)speed)}/s" : string.Empty)
                    + (speed > 0 && p.Total > p.Done ? $" · about {Formatting.Duration(TimeSpan.FromSeconds((p.Total - p.Done) / speed))} left" : string.Empty);
                break;
            case UpdateStage.Extracting:
                _downloadClock = null;
                StageText = "Step 2 of 3: Extracting";
                ProgressText = $"{Formatting.Bytes(p.Done)} of {Formatting.Bytes(p.Total)} read";
                break;
            default:
                StageText = "Step 3 of 3: Installing";
                ProgressText = null;
                break;
        }
    }

    private bool CanCancel => State == UpdateState.Installing;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancel?.Cancel();

    /// <summary>The window closes: stop a download that is still running.</summary>
    public void CancelIfRunning() => _cancel?.Cancel();

    [RelayCommand]
    private void DismissBanner() => BannerVisible = false;

    private bool HasReleasePage => ReleasePage != null;

    [RelayCommand(CanExecute = nameof(HasReleasePage))]
    private async Task OpenReleasePageAsync()
    {
        if (Dialogs != null && ReleasePage != null)
        {
            await Dialogs.LaunchAsync(ReleasePage);
        }
    }

    private void Fail(string message)
    {
        Error = message;
        StatusText = message;
        StageText = null;
        State = UpdateState.Failed;
    }

    /// <summary>
    /// Starts the new program from where it was extracted, which waits for this process to exit, puts itself at
    /// <paramref name="executable"/> and starts it (see Program.FinishUpdate); then closes this one.
    /// </summary>
    private static void Restart(string staged, string executable)
    {
        var start = new ProcessStartInfo(staged) { UseShellExecute = false };
        foreach (var argument in new[] { AppArguments.FinishUpdateSwitch, executable, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        Process.Start(start);
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}

