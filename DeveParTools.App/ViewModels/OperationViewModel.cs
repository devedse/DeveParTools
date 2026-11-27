using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.App.Controls;
using DeveParTools.Core.Operations;

namespace DeveParTools.App.ViewModels;

/// <summary>File and folder pickers, provided by the window.</summary>
public interface IFileDialogs
{
    Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool multiple, IReadOnlyList<string>? patterns = null);

    Task<string?> OpenFolderAsync(string title);

    Task<string?> SaveFileAsync(string title, string suggestedName, string extension);
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
        _changed = changed;
    }

    private readonly Action _changed;

    public BlockState State { get; }

    public string Name { get; }

    public string BrushKey { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    private int _count;

    [ObservableProperty]
    private bool _isVisible = true;

    public bool HasCount => Count > 0;

    partial void OnIsVisibleChanged(bool value) => _changed();
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
    private double _pendingProgress;
    private string? _pendingDetail;

    protected OperationViewModel(SettingsViewModel settings)
    {
        Settings = settings;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => Tick());
        foreach (var s in new[] { BlockState.Found, BlockState.FoundMoved, BlockState.FoundElsewhere, BlockState.Damaged, BlockState.Missing, BlockState.Recoverable, BlockState.Repairing, BlockState.Repaired, BlockState.Unrecoverable, BlockState.Processed })
        {
            Legend.Add(new LegendItem(s, UpdateHidden));
        }
    }

    public SettingsViewModel Settings { get; }

    public IFileDialogs? Dialogs { get; set; }

    public ObservableCollection<LogLine> Log { get; } = new();

    public ObservableCollection<LegendItem> Legend { get; } = new();

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

    [ObservableProperty]
    private string? _selectedBlockInfo;

    [ObservableProperty]
    private double _cellSize = 10;

    [ObservableProperty]
    private int _recoveryTotal;

    [ObservableProperty]
    private int _recoveryUsed;

    /// <summary>Formats a block description for the details pane (set by the view, which owns the grid).</summary>
    public Func<int, string>? DescribeBlock { get; set; }

    partial void OnSelectedBlockChanged(int value) => SelectedBlockInfo = value >= 0 ? DescribeBlock?.Invoke(value) : null;

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
        _timer.Start();
        try
        {
            return await Task.Run(() => work(context), _cts.Token);
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
            Tick();
            IsBusy = false;
            Stage = "Done";
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Tick()
    {
        Progress = _pendingProgress * 100;
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
            if (SelectedBlock >= 0)
            {
                SelectedBlockInfo = DescribeBlock?.Invoke(SelectedBlock);
            }
        }
    }

    void IParOperationObserver.Stage(string name) => Dispatcher.UIThread.Post(() =>
    {
        Stage = name;
        Log.Add(new LogLine(DateTime.Now, ParLogLevel.Info, name + "..."));
    });

    void IParOperationObserver.Progress(double fraction, string? detail)
    {
        _pendingProgress = Math.Clamp(fraction, 0, 1);
        _pendingDetail = detail;
    }

    void IParOperationObserver.Log(ParLogLevel level, string message) => Dispatcher.UIThread.Post(() => Log.Add(new LogLine(DateTime.Now, level, message)));
}
