using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.Core.Operations;

namespace DeveParTools.App.ViewModels;

/// <summary>
/// One protected file in the Verify page's list: live while verifying (from the states of its blocks), then with the
/// verification's result.
/// </summary>
public sealed partial class FileRowViewModel : ObservableObject
{
    public FileRowViewModel(string name, string path, string size)
    {
        Name = name;
        Path = path;
        Size = size;
    }

    /// <summary>A row for a verified file (<paramref name="blocks"/>: its block states, for the damage counts).</summary>
    public static FileRowViewModel From(ParFileReport report, BlockCounts? blocks = null)
    {
        var row = new FileRowViewModel(report.Name, report.Path, Formatting.Bytes(report.Size)) { Report = report, IsOk = report.Status == ParFileStatus.Complete };
        string status = report.FoundAs != null ? $"{report.Status} (found as {System.IO.Path.GetFileName(report.FoundAs)})" : report.Status.ToString();
        row.StatusText = blocks is { Total: > 0, Bad: > 0 } b && report.Status != ParFileStatus.Complete ? $"{status}: {b.Bad:N0} of {b.Total:N0} blocks bad ({100.0 * b.Bad / b.Total:0.##}%)" : status;
        row.StatusBrushKey = report.Status switch
        {
            ParFileStatus.Complete => "BlockFoundBrush",
            ParFileStatus.Damaged => "BlockDamagedBrush",
            _ => "BlockMissingBrush",
        };
        row.CheckedPercent = 100;
        return row;
    }

    /// <summary>The verification's result for the file (null while it is still being checked).</summary>
    public ParFileReport? Report { get; private init; }

    public string Name { get; }

    public string Path { get; }

    public string Size { get; }

    [ObservableProperty]
    private string _statusText = "Waiting to be checked";

    [ObservableProperty]
    private string _statusBrushKey = "BlockUnknownBrush";

    [ObservableProperty]
    private bool _isOk;

    /// <summary>How much of the file has been checked so far (0–100).</summary>
    [ObservableProperty]
    private double _checkedPercent;

    /// <summary>While verifying: updates the row from its blocks' current states.</summary>
    public void Update(BlockCounts counts)
    {
        int checkedBlocks = counts.Total - counts.Unchecked;
        CheckedPercent = counts.Total == 0 ? 100 : 100.0 * checkedBlocks / counts.Total;
        StatusText = checkedBlocks < counts.Total
            ? $"Checking… {CheckedPercent:0}%" + (counts.Bad > 0 ? $", {counts.Bad:N0} bad block(s) so far" : string.Empty)
            : counts.Bad == 0 ? "All blocks found" : $"{counts.Bad:N0} of {counts.Total:N0} blocks bad ({100.0 * counts.Bad / counts.Total:0.##}%)";
        StatusBrushKey = counts.Bad > 0 ? "BlockDamagedBrush" : checkedBlocks < counts.Total ? "BlockScanningBrush" : "BlockFoundBrush";
        IsOk = counts.Bad == 0 && checkedBlocks == counts.Total;
    }
}

/// <summary>A file's blocks by state: all of them, those not checked yet, and those damaged or missing.</summary>
public readonly record struct BlockCounts(int Total, int Unchecked, int Bad);

/// <summary>Verify &amp; repair page: pick a .par/.par2/.par3 file, see every file and block, repair.</summary>
public sealed partial class VerifyViewModel : OperationViewModel
{
    public VerifyViewModel(SettingsViewModel settings)
        : base(settings)
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy))
            {
                OnPropertyChanged(nameof(Headline));
                ExtendCommand.NotifyCanExecuteChanged();
                RemoveInsideCommand.NotifyCanExecuteChanged();
            }
        };
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExtendCommand))]
    private decimal? _extendPercent = 10;

    /// <summary>Extend: exact number of new recovery blocks (<c>-c</c>); when set, it replaces <see cref="ExtendPercent"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExtendCommand))]
    [NotifyPropertyChangedFor(nameof(ExtendUsesPercent))]
    private decimal? _extendCount;

    /// <summary>The percentage applies unless an exact number of new recovery blocks is given.</summary>
    public bool ExtendUsesPercent => ExtendCount == null;

    /// <summary>Extend: number of the first new recovery block (<c>--first</c>; default: after the existing ones).</summary>
    [ObservableProperty]
    private decimal? _extendFirst;

    /// <summary>Extend: number of new recovery files (<c>-n</c>).</summary>
    [ObservableProperty]
    private decimal? _extendVolumes;

    /// <summary>Extend: base name of the new recovery files (<c>-o</c>; default: the set's own name).</summary>
    [ObservableProperty]
    private string? _extendOutput;

    /// <summary>Folder the protected files are in (<c>-B</c>; default: next to the PAR file, or as stored).</summary>
    [ObservableProperty]
    private string? _baseDirectory;

    /// <summary>
    /// "Remove recovery data" is offered for a .zip/.7z/.rar that carries PAR3 data for itself (PAR inside) and is
    /// intact, which <see cref="ParClient.RemoveInside"/> requires.
    /// </summary>
    public bool CanOfferRemoveInside => Report is { Format: ParFormat.Par3, AllFilesComplete: true }
        && Path.GetExtension(ParFile ?? string.Empty).ToLowerInvariant() is ".zip" or ".7z" or ".rar";

    private string? Base => string.IsNullOrWhiteSpace(BaseDirectory) ? null : BaseDirectory;

    [RelayCommand]
    private async Task BrowseBaseAsync()
    {
        if (Dialogs != null && await Dialogs.OpenFolderAsync("Folder with the protected files") is { } dir)
        {
            BaseDirectory = dir;
        }
    }

    private bool CanRemoveInside() => CanOfferRemoveInside && !IsBusy;

    /// <summary>Takes the recovery data out of a protected archive again, leaving its original bytes.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveInside))]
    private async Task RemoveInsideAsync()
    {
        var archive = ParFile!;
        if (Dialogs == null || !await Dialogs.ConfirmAsync(
                "Remove the recovery data?",
                $"{Path.GetFileName(archive)} goes back to its original bytes and can no longer repair itself. You can add recovery data again on the Create page.",
                "Remove"))
        {
            return;
        }

        var length = await RunAsync("Removing recovery data", ctx => (long?)ParClient.RemoveInside(archive, ctx));
        if (length is long original)
        {
            Report = null;
            var message = $"Removed the recovery data: {Path.GetFileName(archive)} is back to its original {Formatting.Bytes(original)}.";
            Log.Add(new LogLine(DateTime.Now, ParLogLevel.Info, message));
            StatusText = message;
        }
    }

    /// <summary>Extending is offered for complete PAR2 sets and for PAR3 sets whose input data is all there.</summary>
    public bool CanOfferExtend => Report is { Format: ParFormat.Par3, MissingBlocks: 0 } or { Format: ParFormat.Par2, AllFilesComplete: true };

    public ObservableCollection<string> ExtraFiles { get; } = new();

    public ObservableCollection<FileRowViewModel> Files { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private string? _parFile;

    [ObservableProperty]
    private bool _purgeBackups;

    [ObservableProperty]
    private bool _restorePermissions;

    [ObservableProperty]
    private bool _restoreLinks;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    private ParVerifyReport? _report;

    [ObservableProperty]
    private FileRowViewModel? _selectedFile;

    [ObservableProperty]
    private IReadOnlySet<int>? _highlight;

    public string Headline => Report switch
    {
        null when IsBusy => "Checking your files…",
        null => "Open a PAR file to check your files.",
        { AllFilesComplete: true, MissingBlocks: 0 } when Map?.CountStates()[(int)BlockState.Repaired] > 0 => "Repaired: all files are correct again.",
        { AllFilesComplete: true } => "All files are correct.",
        { RepairPossible: true } => "Damage found: repair is possible.",
        _ => "Damage found: not enough recovery data.",
    };

    public string HealthIconKey => Report switch
    {
        null => "IconInfo",
        { AllFilesComplete: true } => "IconHealthOk",
        { RepairPossible: true } => "IconHealthAttention",
        _ => "IconHealthError",
    };

    public string Details => Report == null
        ? string.Empty
        : $"{Report.Format} · {Report.Files.Count} files · {Report.BlockCount:N0} blocks{(Report.BlockSize > 0 ? $" of {Formatting.Bytes(Report.BlockSize)}" : string.Empty)}";

    public int FilesOk => Report?.Files.Count(f => f.Status == ParFileStatus.Complete) ?? 0;

    public int FilesBad => Report?.Files.Count(f => f.Status != ParFileStatus.Complete) ?? 0;

    public int MissingBlocks => Report?.MissingBlocks ?? 0;

    public int RecoveryAvailable => Report?.RecoveryBlocksAvailable ?? 0;

    /// <summary>Needed vs available recovery blocks for the gauge (0..1, capped).</summary>
    public double NeedRatio => RecoveryAvailable == 0 ? (MissingBlocks > 0 ? 1 : 0) : Math.Min(1, (double)MissingBlocks / RecoveryAvailable);

    public string NeedText => Report == null ? string.Empty : $"{MissingBlocks:N0} missing / {RecoveryAvailable:N0} recovery blocks available";

    partial void OnReportChanged(ParVerifyReport? value)
    {
        _blockToRow = null;
        var byPath = Map != null && value != null ? CountsByFile(Map) : null;
        Files.Clear();
        foreach (var f in value?.Files ?? [])
        {
            Files.Add(FileRowViewModel.From(f, byPath?.GetValueOrDefault(f.Path)));
        }

        ExtendCommand.NotifyCanExecuteChanged();
        RemoveInsideCommand.NotifyCanExecuteChanged();
        foreach (var name in new[] { nameof(CanOfferExtend), nameof(CanOfferRemoveInside), nameof(Headline), nameof(HealthIconKey), nameof(Details), nameof(FilesOk), nameof(FilesBad), nameof(MissingBlocks), nameof(RecoveryAvailable), nameof(NeedRatio), nameof(NeedText) })
        {
            OnPropertyChanged(name);
        }
    }

    // ---------------------------------------------------------------- live file rows while verifying

    private int[]? _blockToRow;
    private byte[] _states = [];
    private int _ticks;

    /// <summary>
    /// While verifying: one row per protected file, built from the block map's owners as soon as the files are being
    /// checked, and updated a few times a second from the block states.
    /// </summary>
    protected override void OnTick()
    {
        if (!IsBusy || Report != null || Map is not { } map)
        {
            return;
        }

        if (_blockToRow == null)
        {
            if (Stage != VerifyStages.Verifying || map.Ownership.IsEmpty)
            {
                return;
            }

            // Every block belongs to (the first of) its owners' file; rows in name order.
            var paths = map.Ownership.Values.Select(o => o[0].File).Distinct().Order(StringComparer.Ordinal).ToList();
            var rowOf = paths.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
            _blockToRow = new int[map.InputBlockCount];
            Array.Fill(_blockToRow, -1);
            foreach (var (block, owners) in map.Ownership)
            {
                _blockToRow[block] = rowOf[owners[0].File];
            }

            Files.Clear();
            foreach (var p in paths)
            {
                Files.Add(new FileRowViewModel(System.IO.Path.GetFileName(p), p, File.Exists(p) ? Formatting.Bytes(new FileInfo(p).Length) : "missing"));
            }
        }

        if (_ticks++ % 8 == 0)
        {
            var counts = Count(map, _blockToRow, Files.Count);
            for (int r = 0; r < Files.Count; r++)
            {
                Files[r].Update(counts[r]);
            }
        }
    }

    private BlockCounts[] Count(BlockStateMap map, int[] blockToRow, int rows)
    {
        if (_states.Length != map.InputBlockCount)
        {
            _states = new byte[map.InputBlockCount];
        }

        map.CopyStates(_states);
        var total = new int[rows];
        var unchecked_ = new int[rows];
        var bad = new int[rows];
        for (int b = 0; b < _states.Length; b++)
        {
            int r = blockToRow[b];
            if (r < 0)
            {
                continue;
            }

            total[r]++;
            switch ((BlockState)_states[b])
            {
                case BlockState.Unknown or BlockState.Scanning:
                    unchecked_[r]++;
                    break;
                case BlockState.Damaged or BlockState.Missing or BlockState.Recoverable or BlockState.Unrecoverable:
                    bad[r]++;
                    break;
            }
        }

        return Enumerable.Range(0, rows).Select(r => new BlockCounts(total[r], unchecked_[r], bad[r])).ToArray();
    }

    /// <summary>Block counts per file path, from the map's owners (for the rows of the finished verification).</summary>
    private Dictionary<string, BlockCounts> CountsByFile(BlockStateMap map)
    {
        var paths = map.Ownership.Values.Select(o => o[0].File).Distinct().ToList();
        var rowOf = paths.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var blockToRow = new int[map.InputBlockCount];
        Array.Fill(blockToRow, -1);
        foreach (var (block, owners) in map.Ownership)
        {
            blockToRow[block] = rowOf[owners[0].File];
        }

        var counts = Count(map, blockToRow, paths.Count);
        return paths.Select((p, i) => (p, counts[i])).ToDictionary(x => x.p, x => x.Item2, StringComparer.Ordinal);
    }

    partial void OnSelectedFileChanged(FileRowViewModel? value)
    {
        if (value == null || Map == null)
        {
            Highlight = null;
            return;
        }

        var path = value.Path;
        Highlight = Map.Ownership.Where(kv => kv.Value.Any(o => string.Equals(o.File, path, StringComparison.Ordinal))).Select(kv => kv.Key).ToHashSet();
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (Dialogs == null)
        {
            return;
        }

        var files = await Dialogs.OpenFilesAsync("Open a PAR file or a protected archive", false, ["*.par2", "*.par3", "*.par", "*.p??", "*.zip", "*.7z", "*.rar"]);
        if (files.Count > 0)
        {
            ParFile = files[0];
            await VerifyAsync();
        }
    }

    [RelayCommand]
    private async Task AddExtraFilesAsync()
    {
        if (Dialogs == null)
        {
            return;
        }

        foreach (var f in await Dialogs.OpenFilesAsync("Extra files to search", true))
        {
            if (!ExtraFiles.Contains(f))
            {
                ExtraFiles.Add(f);
            }
        }
    }

    [RelayCommand]
    private void RemoveExtraFile(string file) => ExtraFiles.Remove(file);

    private bool CanVerify() => !string.IsNullOrWhiteSpace(ParFile) && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        var file = ParFile!;
        var extra = ExtraFiles.ToList();
        var baseDirectory = Base;
        Report = null;
        var report = await RunAsync("Verifying", ctx => ParClient.Verify(file, new ParVerifyRequest { ExtraFiles = extra, BaseDirectory = baseDirectory }, ctx));
        if (report != null)
        {
            Report = report;
            StatusText = Headline;
        }

        RepairCommand.NotifyCanExecuteChanged();
    }

    private bool CanRepair() => Report is { AllFilesComplete: false, RepairPossible: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairAsync()
    {
        var report = Report!;
        var request = new ParRepairRequest { PurgeBackups = PurgeBackups, RestorePermissions = RestorePermissions, RestoreLinks = RestoreLinks };
        var result = await RunAsync("Repairing", ctx =>
        {
            // Repair reuses the verification's block map so the grid animates from "recoverable" to "repaired".
            ctx.Blocks = Map;
            return ParClient.Repair(report, request, ctx);
        });
        if (result != null)
        {
            StatusText = result.Message;
            Log.Add(new LogLine(DateTime.Now, result.Success ? ParLogLevel.Info : ParLogLevel.Error, result.Message));
            if (result.Success)
            {
                // Show the repaired state (the block map keeps its "repaired" colours).
                Report = new ParVerifyReport
                {
                    Format = report.Format,
                    ParFile = report.ParFile,
                    Files = report.Files.Select(f => f with { Status = ParFileStatus.Complete, FoundAs = null }).ToList(),
                    BlockSize = report.BlockSize,
                    BlockCount = report.BlockCount,
                    MissingBlocks = 0,
                    RecoveryBlocksAvailable = report.RecoveryBlocksAvailable,
                    AllFilesComplete = true,
                    RepairPossible = true,
                    Creators = report.Creators,
                    Comments = report.Comments,
                    Native = report.Native,
                };
                StatusText = result.Message;
            }
        }
    }

    private bool CanExtend() => CanOfferExtend && !IsBusy && (ExtendCount > 0 || (ExtendCount == null && ExtendPercent > 0));

    /// <summary>Adds recovery blocks to the open PAR3 set, then verifies again so the counts update.</summary>
    [RelayCommand(CanExecute = nameof(CanExtend))]
    private async Task ExtendAsync()
    {
        var request = new ParExtendRequest
        {
            ParFile = ParFile!,
            ExtraFiles = ExtraFiles.ToList(),
            BaseDirectory = Base,
            RedundancyPercent = (double)(ExtendPercent ?? 10),
            RecoveryBlockCount = ExtendCount is decimal count ? (int)count : null,
            FirstRecoveryBlock = ExtendFirst is decimal first ? (long)first : null,
            VolumeCount = ExtendVolumes is decimal volumes and > 0 ? (int)volumes : null,
            OutputPath = string.IsNullOrWhiteSpace(ExtendOutput) ? null : ExtendOutput,
        };
        var report = await RunAsync("Adding recovery data", ctx => ParClient.Extend(request, ctx));
        if (report == null)
        {
            return;
        }

        var message = $"Added {report.RecoveryBlocks:N0} recovery block(s) in {report.WrittenFiles.Count} new file(s).";
        Log.Add(new LogLine(DateTime.Now, ParLogLevel.Info, message));
        foreach (var f in report.WrittenFiles)
        {
            Log.Add(new LogLine(DateTime.Now, ParLogLevel.Info, "  " + f));
        }

        await VerifyAsync();
        StatusText = message;
    }

    /// <summary>Opens the first dropped file.</summary>
    [RelayCommand]
    private async Task OpenDroppedAsync(IReadOnlyList<string> paths)
    {
        if (paths.FirstOrDefault(File.Exists) is { } file)
        {
            await OpenAsync(file);
        }
    }

    /// <summary>Opens a file passed on the command line or by a file association.</summary>
    public async Task OpenAsync(string path)
    {
        ParFile = path;
        await VerifyAsync();
    }
}
