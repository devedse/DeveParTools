using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.App.Controls;
using DeveParTools.Core.Operations;

namespace DeveParTools.App.ViewModels;

/// <summary>What view models need from the view: file and folder pickers, confirmations and opening links (the window implements it, tests fake it).</summary>
public interface IViewServices
{
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool multiple, IReadOnlyList<string>? patterns = null);

    Task<string?> OpenFolderAsync(string title);

    Task<string?> SaveFileAsync(string title, string suggestedName, string extension);

    /// <summary>Asks before something that cannot be undone; true when the user chose <paramref name="confirm"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirm);

    /// <summary>Opens a link with the system (a web page, or a Settings page such as ms-settings:defaultapps).</summary>
    Task LaunchAsync(Uri uri);

    /// <summary>Shows the update window (download and installation progress) until the user closes it.</summary>
    Task ShowUpdateAsync(UpdateViewModel updates);
}

public sealed record LogLine(DateTime Time, ParLogLevel Level, string Message)
{
    public string Text => $"{Time:HH:mm:ss}  {Message}";

    public bool IsWarning => Level >= ParLogLevel.Warning;
}

/// <summary>One legend entry: a block state, its colour, its count, and whether it is shown.</summary>
public sealed partial class LegendItem : ObservableObject
{
    public LegendItem(BlockState state, Action changed)
    {
        State = state;
        Name = BlockGrid.Display(state);
        BrushKey = BlockGrid.ResourceKey(state);
        Description = state switch
        {
            BlockState.Found => "The block is where it belongs, and its checksum is right.",
            BlockState.FoundMoved => "The block is in the right file, but at another position (bytes were inserted or removed before it). Repair puts it back in place.",
            BlockState.FoundElsewhere => "The block was found in another file: a renamed copy, an extra file you added, or another file of the set. Repair copies it into place.",
            BlockState.FoundStored => "Not in the file, but stored in the PAR files themselves (the set was made with Store data). Repair copies it back; no recovery blocks are needed.",
            BlockState.Damaged => "The block's position exists, but its contents are wrong.",
            BlockState.Missing => "The block was not found anywhere.",
            BlockState.Recoverable => "Missing or damaged, but there are enough recovery blocks to rebuild it.",
            BlockState.Repairing => "Being rebuilt right now.",
            BlockState.Repaired => "Rebuilt from the recovery blocks and checked.",
            BlockState.Unrecoverable => "Missing or damaged, and there are not enough recovery blocks to rebuild it.",
            _ => "Read and processed.",
        } + " Click to hide or show these blocks.";
        _changed = changed;
    }

    private readonly Action _changed;

    public BlockState State { get; }

    public string Name { get; }

    public string BrushKey { get; }

    /// <summary>What the state means (the chip's tooltip).</summary>
    public string Description { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    private int _count;

    [ObservableProperty]
    private bool _isVisible = true;

    public bool HasCount => Count > 0;

    partial void OnIsVisibleChanged(bool value) => _changed();
}

/// <summary>One step in the progress panel: what it does, how much of the work it is, and how far it is.</summary>
public sealed partial class StageRowViewModel : ObservableObject
{
    public StageRowViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }

    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>"≈ 47% of the work" (the step's estimated share).</summary>
    [ObservableProperty]
    private string _shareText = string.Empty;

    /// <summary>0–100.</summary>
    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsDone), nameof(IsPending), nameof(IsSkipped), nameof(PercentText))]
    private StageStatus _status;

    public bool IsActive => Status == StageStatus.Active;

    public bool IsDone => Status == StageStatus.Done;

    public bool IsPending => Status == StageStatus.Pending;

    public bool IsSkipped => Status == StageStatus.Skipped;

    public string PercentText => Status switch
    {
        StageStatus.Skipped => "not needed",
        StageStatus.Pending => "waiting",
        _ => $"{Percent:0.0}%",
    };

    partial void OnPercentChanged(double value) => OnPropertyChanged(nameof(PercentText));
}

/// <summary>
/// Shared plumbing for long operations: runs work on a background thread, receives progress through
/// <see cref="IParOperationObserver"/> (marshalled to the UI thread), and refreshes the live block grid ~30 times a
/// second from the shared <see cref="BlockStateMap"/>.
/// </summary>
public abstract partial class OperationViewModel : ObservableObject, IParOperationObserver
{
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _timer;
    private long _seenVersion = -1;
    private string? _pendingDetail;
    private readonly ProgressTracker _tracker = new();
    private readonly System.Diagnostics.Stopwatch _elapsed = new();

    protected OperationViewModel(SettingsViewModel settings)
    {
        Settings = settings;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick());
        foreach (var s in new[] { BlockState.Found, BlockState.FoundMoved, BlockState.FoundElsewhere, BlockState.FoundStored, BlockState.Damaged, BlockState.Missing, BlockState.Recoverable, BlockState.Repairing, BlockState.Repaired, BlockState.Unrecoverable, BlockState.Processed })
        {
            Legend.Add(new LegendItem(s, UpdateHidden));
        }
    }

    public SettingsViewModel Settings { get; }

    public IViewServices? Dialogs { get; set; }

    public ObservableCollection<LogLine> Log { get; } = new();

    public ObservableCollection<LegendItem> Legend { get; } = new();

    /// <summary>Every step of the running (or last) operation, with its progress.</summary>
    public ObservableCollection<StageRowViewModel> Stages { get; } = new();

    public bool HasStages => Stages.Count > 0;

    /// <summary>"42.3% · about 3 min left · 1:02 elapsed".</summary>
    [ObservableProperty]
    private string? _totalText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _stage = "Ready";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _progressDetail;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private BlockStateMap? _map;

    [ObservableProperty]
    private long _mapVersion;

    [ObservableProperty]
    private IReadOnlySet<BlockState>? _hiddenStates;

    [ObservableProperty]
    private int _selectedBlock = -1;

    /// <summary>The selected block's details, written by the grid (BlockGrid.SelectedBlockDescription, one way to here).</summary>
    [ObservableProperty]
    private string? _selectedBlockInfo;

    /// <summary>Square size of the block grid in pixels; below 2 means "fit" (the default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _cellSize;

    public string ZoomText => CellSize < 2 ? "Zoom: fit" : $"Zoom: {CellSize:0} px";

    [ObservableProperty]
    private int _recoveryTotal;

    [ObservableProperty]
    private int _recoveryUsed;

    private void UpdateHidden() => HiddenStates = Legend.Where(l => !l.IsVisible).Select(l => l.State).ToHashSet();

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Runs <paramref name="work"/> on a background thread with a fresh operation context.</summary>
    protected async Task<T?> RunAsync<T>(string title, Func<OperationContext, T> work)
    {
        if (IsBusy)
        {
            return default;
        }

        _cts = new CancellationTokenSource();
        var context = new OperationContext
        {
            Observer = this,
            CancellationToken = _cts.Token,
            Threads = Settings.Threads,
            MemoryBudget = Settings.MemoryMiB * (1L << 20),
        };
        context.BlocksCreated += map => Dispatcher.UIThread.Post(() =>
        {
            Map = map;
            _seenVersion = -1;
        });
        IsBusy = true;
        Stage = title;
        Progress = 0;
        StatusText = null;
        _tracker.Reset();
        Stages.Clear();
        OnPropertyChanged(nameof(HasStages));
        _elapsed.Restart();
        _timer.Start();
        bool success = false;
        try
        {
            var result = await Task.Run(() => work(context), _cts.Token);
            success = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            Log.Add(new LogLine(DateTime.Now, ParLogLevel.Warning, "Cancelled."));
            StatusText = "Cancelled.";
            return default;
        }
        catch (Exception e)
        {
            Log.Add(new LogLine(DateTime.Now, ParLogLevel.Error, e.Message));
            StatusText = e.Message;
            return default;
        }
        finally
        {
            _timer.Stop();
            _elapsed.Stop();
            _tracker.Finish(success);
            Tick();
            IsBusy = false;
            Stage = success ? "Done" : StatusText == "Cancelled." ? "Cancelled" : "Failed";
            if (success)
            {
                TotalText = $"Done in {Formatting.Duration(_elapsed.Elapsed)}";
            }

            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Copies the tracker's state into the stage rows, the total and the time estimate.</summary>
    private void UpdateStages()
    {
        var (stages, total) = _tracker.Read();
        double weight = stages.Where(st => st.Status != StageStatus.Skipped).Sum(st => st.Weight);
        bool rebuilt = Stages.Count != stages.Count || Stages.Zip(stages).Any(p => p.First.Name != p.Second.Name);
        if (rebuilt)
        {
            Stages.Clear();
            foreach (var st in stages)
            {
                Stages.Add(new StageRowViewModel(st.Name));
            }

            OnPropertyChanged(nameof(HasStages));
        }

        for (int i = 0; i < stages.Count; i++)
        {
            var row = Stages[i];
            var st = stages[i];
            row.Description = st.Description;
            row.ShareText = st.Status == StageStatus.Skipped || weight <= 0 ? string.Empty : $"≈ {Math.Max(0.1, 100 * st.Weight / weight):0.#}% of the work";
            row.Percent = 100 * (st.Status == StageStatus.Done ? 1 : st.Fraction);
            row.Status = st.Status;
        }

        Progress = 100 * total;
        if (IsBusy && stages.Count > 0)
        {
            var elapsed = _elapsed.Elapsed;
            string left = total >= 0.01 && elapsed.TotalSeconds >= 2
                ? $" · about {Formatting.Duration(TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - total) / total))} left"
                : string.Empty;
            TotalText = $"{100 * total:0.0}%{left} · {Formatting.Duration(elapsed)} elapsed";
        }
    }

    /// <summary>Called on the UI timer while an operation runs (and once at its end), after the stages are updated.</summary>
    protected virtual void OnTick()
    {
    }

    private void Tick()
    {
        UpdateStages();
        OnTick();
        ProgressDetail = _pendingDetail;
        if (Map is { } map && map.Version != _seenVersion)
        {
            _seenVersion = map.Version;
            MapVersion++;
            var counts = map.CountStates();
            foreach (var l in Legend)
            {
                l.Count = counts[(int)l.State];
            }

            var rec = map.CountRecoveryStates();
            RecoveryTotal = map.RecoveryBlockCount;
            RecoveryUsed = rec[(int)RecoveryBlockState.Used];
        }
    }

    void IParOperationObserver.Plan(IReadOnlyList<OperationStage> stages) => _tracker.Plan(stages);

    void IParOperationObserver.Stage(string name)
    {
        _tracker.Stage(name);
        _pendingDetail = null;
        Dispatcher.UIThread.Post(() =>
        {
            Stage = name;
            Log.Add(new LogLine(DateTime.Now, ParLogLevel.Info, name + "..."));
        });
    }

    void IParOperationObserver.Progress(double fraction, string? detail)
    {
        _tracker.Progress(fraction);
        _pendingDetail = detail;
    }

    void IParOperationObserver.Log(ParLogLevel level, string message) => Dispatcher.UIThread.Post(() => Log.Add(new LogLine(DateTime.Now, level, message)));
}
