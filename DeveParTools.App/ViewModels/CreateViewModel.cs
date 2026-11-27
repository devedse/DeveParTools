using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.Core.Operations;
using DeveParTools.Par3;

namespace DeveParTools.App.ViewModels;

/// <summary>One file or folder in the Create page's list, with what the current plan gives it.</summary>
public sealed partial class CreateInputRow : ObservableObject
{
    public CreateInputRow(string path)
    {
        Path = path;
        IsFolder = Directory.Exists(path);
        Name = IsFolder ? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)) + System.IO.Path.DirectorySeparatorChar : System.IO.Path.GetFileName(path);
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsFolder { get; }

    [ObservableProperty]
    private string _files = string.Empty;

    [ObservableProperty]
    private string _size = string.Empty;

    [ObservableProperty]
    private string _blocks = string.Empty;
}

/// <summary>One file that will be written: its name, what it holds and its exact size.</summary>
public sealed record RecoveryFileRow(string Name, string Blocks, string Size);

/// <summary>
/// Create page: inputs, format and the block and recovery choices, with the real numbers of the set that will be made
/// (<see cref="ParClient.Plan"/>) before anything is written.
/// </summary>
/// <remarks>
/// <para>The two sliders each have a number box next to it, and both show real values:</para>
/// <list type="bullet">
/// <item>Blocks: the slider (logarithmic, 1 to the format's maximum) and the count box set a target block count;
/// the block size box sets an exact size instead. The plan then shows the real count and size (e.g. target 2,000 can
/// give 1,998 blocks, because every file is rounded up to whole blocks).</item>
/// <item>Recovery: the redundancy slider sets a percentage; the count box sets an exact number of recovery blocks
/// instead. The percentage shown is the real one (count ÷ input blocks).</item>
/// </list>
/// <para>Create passes the planned block size and recovery count explicitly, so the set is exactly the one shown.
/// The file list is scanned in the background when the inputs, the format or "include subfolders" change; every other
/// change only re-plans from that list, which is fast.</para>
/// </remarks>
public sealed partial class CreateViewModel : OperationViewModel
{
    private IReadOnlyList<ParPlanFile> _files = [];
    private int _scanVersion;
    private int _blockTarget = 2000;
    private long? _explicitBlockSize;
    private double _redundancy = 10;
    private int? _exactRecovery;

    public CreateViewModel(SettingsViewModel settings)
        : base(settings)
    {
        Inputs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanInsertIntoArchive));
            Rescan();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Map))
            {
                OnPropertyChanged(nameof(ShowBlocks));
            }
        };
        Replan();
    }

    public ObservableCollection<CreateInputRow> Inputs { get; } = new();

    public ObservableCollection<RecoveryFileRow> RecoveryFiles { get; } = new();

    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>Problems that make Create refuse the choices (shown in red).</summary>
    public ObservableCollection<string> Errors { get; } = new();

    /// <summary>PAR3 first: it is the default.</summary>
    public string[] Formats { get; } = ["PAR3 (modern: folders, fast FFT code)", "PAR2 (most compatible)", "PAR1 (legacy)"];

    public string[] Codes { get; } = ["Automatic", "Cauchy Reed-Solomon", "FFT Reed-Solomon (fast)"];

    public string[] VolumeSizings { get; } = ["Growing (1, 2, 4, 8… blocks per file)", "Equal (same number of blocks per file)", "No file larger than the largest input file"];

    public string[] CauchyConventions { get; } = ["par3cmdline (x = I, compatible)", "PAR3 specification draft (x = I + 1)"];

    private Task _scanTask = Task.CompletedTask;
    private Task _sizeTask = Task.CompletedTask;
    private int _sizeVersion;

    /// <summary>Completes when the latest scan, plan and file sizes are done (the UI checks wait for it).</summary>
    public Task Planned => WhenPlannedAsync();

    private async Task WhenPlannedAsync()
    {
        while (true)
        {
            var scan = _scanTask;
            var size = _sizeTask;
            await scan;
            await size;
            if (scan == _scanTask && size == _sizeTask)
            {
                return;
            }
        }
    }

    /// <summary>The set that Create will make with the current choices (blocks and recovery; computed at once).</summary>
    [ObservableProperty]
    private ParCreatePlan? _plan;

    /// <summary>The same plan with the exact size of every file (computed in the background, null meanwhile).</summary>
    [ObservableProperty]
    private ParCreatePlan? _sizedPlan;

    // ---------------------------------------------------------------- choices

    [ObservableProperty]
    private int _formatIndex;

    [ObservableProperty]
    private int _codeIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string? _outputPath;

    [ObservableProperty]
    private string? _comment;

    [ObservableProperty]
    private decimal? _volumeCount;

    /// <summary>0 = growing (default), 1 = equal (<c>-u</c>), 2 = limited to the largest input (<c>-l</c>).</summary>
    [ObservableProperty]
    private int _volumeSizingIndex;

    [ObservableProperty]
    private decimal? _firstRecoveryBlock;

    /// <summary>Folder that stored names are relative to (<c>-B</c>); default: the folder of the output.</summary>
    [ObservableProperty]
    private string? _baseDirectory;

    [ObservableProperty]
    private bool _recursive = true;

    [ObservableProperty]
    private bool _dedup;

    [ObservableProperty]
    private bool _packTails = true;

    [ObservableProperty]
    private bool _storeData;

    [ObservableProperty]
    private bool _unixPermissions;

    [ObservableProperty]
    private bool _fatPermissions;

    [ObservableProperty]
    private bool _reproducible;

    [ObservableProperty]
    private decimal? _maxRecoveryBlocks;

    [ObservableProperty]
    private decimal? _interleave;

    [ObservableProperty]
    private int _cauchyConventionIndex;

    [ObservableProperty]
    private CreateInputRow? _selectedInput;

    [ObservableProperty]
    private bool _insideArchive;

    public bool IsPar3 => FormatIndex == 0;

    public bool IsPar1 => FormatIndex == 2;

    /// <summary>PAR1 has one block per file, so there is no block size to choose and one recovery block per file.</summary>
    public bool HasBlockChoice => !IsPar1;

    /// <summary>"PAR inside" is offered for PAR3 when the only input is a .zip, .7z or .rar archive.</summary>
    public bool CanInsertIntoArchive => IsPar3 && Inputs.Count == 1 && File.Exists(Inputs[0].Path)
        && Path.GetExtension(Inputs[0].Path).ToLowerInvariant() is ".zip" or ".7z" or ".rar";

    public string OutputPlaceholder => "recovery" + Extension;

    private string Extension => FormatIndex switch { 0 => ".par3", 2 => ".par", _ => ".par2" };

    private ParFormat Format => FormatIndex switch { 0 => ParFormat.Par3, 2 => ParFormat.Par1, _ => ParFormat.Par2 };

    // ---------------------------------------------------------------- the linked sliders and boxes

    /// <summary>The block slider: log2 of the target block count (so 1 … 32,768 fits one slider).</summary>
    public double BlockSlider
    {
        get => Math.Log2(Math.Max(1, _explicitBlockSize == null ? _blockTarget : Plan?.InputBlocks ?? 1));
        set => SetBlockTarget((int)Math.Round(Math.Pow(2, value)));
    }

    /// <summary>The slider's end: log2 of the most blocks the format allows for these files.</summary>
    public double BlockSliderMaximum => Math.Log2(Math.Max(2, Plan?.MaxInputBlocks ?? 2));

    /// <summary>The real number of input blocks; typing a number makes it the target.</summary>
    public decimal? BlockCount
    {
        get => Plan?.InputBlocks;
        set
        {
            if (value is decimal v && v >= 1)
            {
                SetBlockTarget((int)Math.Min(int.MaxValue, v));
            }
        }
    }

    /// <summary>The real block size; typing one fixes it (rounded up to a multiple of 4 for PAR2, 8 for PAR3).</summary>
    public decimal? BlockSize
    {
        get => Plan?.BlockSize;
        set
        {
            if (value is decimal v && v >= 1)
            {
                long step = IsPar3 ? 8 : 4;
                _explicitBlockSize = ((long)v + step - 1) / step * step;
                Replan();
            }
        }
    }

    public long BlockSizeStep => IsPar3 ? 8 : 4;

    /// <summary>The redundancy slider (0–100%): the set percentage, or the real one when an exact count is used.</summary>
    public double Redundancy
    {
        get => _exactRecovery == null ? _redundancy : Math.Min(100, Plan?.RedundancyPercent ?? 0);
        set
        {
            _redundancy = Math.Round(value, 2);
            _exactRecovery = null;
            Replan();
        }
    }

    /// <summary>The real number of recovery blocks; typing one makes it exact.</summary>
    public decimal? RecoveryCount
    {
        get => Plan?.RecoveryBlocks;
        set
        {
            if (value is decimal v && v >= 0)
            {
                _exactRecovery = (int)Math.Min(int.MaxValue, v);
                Replan();
            }
        }
    }

    private void SetBlockTarget(int target)
    {
        _blockTarget = Math.Max(1, target);
        _explicitBlockSize = null;
        Replan();
    }

    // ---------------------------------------------------------------- what the plan says

    public bool HasInputs => Plan is { Files.Count: > 0 };

    public string InputSummary => Plan is not { Files.Count: > 0 } plan
        ? "Add files or folders to protect: use the buttons or drop them here."
        : $"{plan.Files.Count:N0} file(s) · {Formatting.Bytes(plan.TotalBytes)} · largest {Formatting.Bytes(plan.LargestFile)}";

    public string BlockSizeText => Plan == null ? string.Empty : Formatting.Bytes(Plan.BlockSize);

    public string RedundancyText => Plan is { InputBlocks: > 0 } plan ? $"{plan.RedundancyPercent:0.00}%" : $"{_redundancy:0.00}%";

    /// <summary>How full the input blocks are (the last block of each file is partly empty).</summary>
    public string BlockFillText => Plan is { InputBlocks: > 0 } plan ? $"blocks {plan.BlockFill * 100:0.0}% full" : string.Empty;

    /// <summary>Everything that will be written: index, recovery files (and PAR3 data parts).</summary>
    public string ParFilesText => Plan is not { Files.Count: > 0 }
        ? "–"
        : SizedPlan?.ParFileBytes is long total ? $"{Formatting.Bytes(total)} in {SizedPlan.OutputFiles!.Count:N0} file(s)" : Plan.Errors.Count > 0 ? "–" : "calculating…";

    /// <summary>The recovery blocks alone.</summary>
    public string RecoveryDataText => Plan is not { RecoveryBlocks: > 0 } plan
        ? "No recovery data"
        : $"{Formatting.Bytes(plan.RecoveryBytes)} ({plan.RecoveryBlocks:N0} block(s) of {Formatting.Bytes(plan.BlockSize)})";

    /// <summary>The share of what is written that is recovery data for real file data (see ParCreatePlan.Efficiency).</summary>
    public string EfficiencyText => Plan is not { RecoveryBlocks: > 0 } ? "–" : SizedPlan?.Efficiency is double e ? $"{e * 100:0.0}%" : Plan.Errors.Count > 0 ? "–" : "calculating…";

    /// <summary>For "Automatic": which code it picks for this set.</summary>
    public string? CodeChoiceText => CodeIndex == 0 && Plan is { RecoveryBlocks: > 0 } plan
        ? $"Automatic picks {(plan.Code.StartsWith("FFT", StringComparison.Ordinal) ? "the FFT code" : "the Cauchy code")} for this set."
        : null;

    /// <summary>Whole files that can be rebuilt when missing: "3 to 5 of 5" (largest files first … smallest first).</summary>
    public string RebuildText
    {
        get
        {
            if (Plan is not { Files.Count: > 0 } plan)
            {
                return "–";
            }

            var (min, max) = plan.RebuildableFiles;
            return min == max ? $"{min:N0} of {plan.Files.Count:N0}" : $"{min:N0} to {max:N0} of {plan.Files.Count:N0}";
        }
    }

    public string CodeText => Plan?.Code ?? string.Empty;

    public string RecoveryFilesHeader => $"Files that will be written ({RecoveryFiles.Count:N0})";

    public bool HasWarnings => Warnings.Count > 0 || Errors.Count > 0;

    public decimal RecoveryCountMaximum => IsPar1 ? Math.Max(0, 255 - (Plan?.Files.Count ?? 0)) : IsPar3 ? 1 << 24 : 65535;

    /// <summary>The block grid is shown once something is being (or was) created.</summary>
    public bool ShowBlocks => Map != null;

    // ---------------------------------------------------------------- re-planning

    partial void OnFormatIndexChanged(int value)
    {
        _explicitBlockSize = null; // PAR2 sizes are multiples of 4, PAR3 of 8
        if (OutputPath != null)
        {
            OutputPath = Path.ChangeExtension(OutputPath, Extension);
        }

        foreach (var name in new[] { nameof(IsPar3), nameof(IsPar1), nameof(HasBlockChoice), nameof(CanInsertIntoArchive), nameof(OutputPlaceholder), nameof(BlockSizeStep) })
        {
            OnPropertyChanged(name);
        }

        Rescan();
    }

    partial void OnRecursiveChanged(bool value) => Rescan();

    partial void OnOutputPathChanged(string? value) => Replan();

    partial void OnCodeIndexChanged(int value) => Replan();

    partial void OnVolumeCountChanged(decimal? value) => Replan();

    partial void OnVolumeSizingIndexChanged(int value) => Replan();

    partial void OnFirstRecoveryBlockChanged(decimal? value) => Replan();

    partial void OnPackTailsChanged(bool value) => Replan();

    partial void OnDedupChanged(bool value) => Replan();

    partial void OnMaxRecoveryBlocksChanged(decimal? value) => Replan();

    partial void OnInterleaveChanged(decimal? value) => Replan();

    partial void OnCommentChanged(string? value) => Replan();

    partial void OnStoreDataChanged(bool value) => Replan();

    partial void OnUnixPermissionsChanged(bool value) => Replan();

    partial void OnFatPermissionsChanged(bool value) => Replan();

    partial void OnBaseDirectoryChanged(string? value) => Replan();

    /// <summary>Scans the inputs in the background, then plans.</summary>
    private void Rescan()
    {
        // The grid and status of the previous run belong to other inputs.
        if (!IsBusy)
        {
            Map = null;
            StatusText = null;
        }

        int version = ++_scanVersion;
        var request = BuildRequest(exact: false);
        _scanTask = ScanAsync(version, request);
    }

    private async Task ScanAsync(int version, ParCreateRequest request)
    {
        IReadOnlyList<ParPlanFile> files;
        try
        {
            files = await Task.Run(() => ParClient.ScanInputs(request));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            files = [];
            Log.Add(new LogLine(DateTime.Now, ParLogLevel.Warning, "Could not read the inputs: " + e.Message));
        }

        if (version == _scanVersion)
        {
            _files = files;
            Replan();
        }
    }

    /// <summary>
    /// Plans from the scanned file list and updates everything shown; the exact file sizes follow in the background
    /// (they need the set's metadata built, which takes longer for very large sets).
    /// </summary>
    private void Replan()
    {
        var request = BuildRequest(exact: false);
        Plan = ParClient.Plan(request, _files, withFileSizes: false);
        var plan = Plan;
        SizedPlan = null;
        int sizeVersion = ++_sizeVersion;
        _sizeTask = SizeAsync(sizeVersion, request, _files);
        foreach (var row in Inputs)
        {
            var mine = plan.Files.Where(f => f.Input == row.Path).ToList();
            row.Files = row.IsFolder ? $"{mine.Count:N0}" : string.Empty;
            row.Size = Formatting.Bytes(mine.Sum(f => f.Length));
            row.Blocks = $"{mine.Sum(f => f.Blocks):N0}";
        }

        ShowFiles(plan);
        foreach (var name in new[]
        {
            nameof(BlockSlider), nameof(BlockSliderMaximum), nameof(BlockCount), nameof(BlockSize), nameof(Redundancy), nameof(RecoveryCount),
            nameof(HasInputs), nameof(InputSummary), nameof(BlockSizeText), nameof(RedundancyText), nameof(BlockFillText), nameof(RecoveryDataText),
            nameof(RebuildText), nameof(CodeText), nameof(CodeChoiceText), nameof(RecoveryCountMaximum),
        })
        {
            OnPropertyChanged(name);
        }

        CreateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Computes the exact file sizes after a short pause (so dragging a slider does not compute every step).</summary>
    private async Task SizeAsync(int version, ParCreateRequest request, IReadOnlyList<ParPlanFile> files)
    {
        await Task.Delay(120);
        if (version != _sizeVersion)
        {
            return;
        }

        var sized = await Task.Run(() => ParClient.Plan(request, files, withFileSizes: true));
        if (version == _sizeVersion)
        {
            SizedPlan = sized;
            ShowFiles(sized);
        }
    }

    /// <summary>The files to be written (with sizes once known), the warnings and the derived texts.</summary>
    private void ShowFiles(ParCreatePlan plan)
    {
        RecoveryFiles.Clear();
        if (plan.OutputFiles is { } sizes)
        {
            foreach (var f in sizes)
            {
                string what = f.RecoveryBlocks > 0 ? $"{f.RecoveryBlocks:N0} block(s)" : f == sizes[0] ? "index" : "data";
                RecoveryFiles.Add(new RecoveryFileRow(Path.GetFileName(f.Path), what, Formatting.Bytes(f.Bytes)));
            }
        }
        else
        {
            foreach (var f in plan.RecoveryFiles)
            {
                RecoveryFiles.Add(new RecoveryFileRow(Path.GetFileName(f.Path), $"{f.Count:N0} block(s)", "…"));
            }
        }

        Warnings.Clear();
        foreach (var w in plan.Warnings)
        {
            Warnings.Add(w);
        }

        Errors.Clear();
        foreach (var e in plan.Errors)
        {
            Errors.Add(e);
        }

        foreach (var name in new[] { nameof(ParFilesText), nameof(EfficiencyText), nameof(RecoveryFilesHeader), nameof(HasWarnings) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>
    /// The request for the current choices. <paramref name="exact"/>: with the plan's block size and recovery count,
    /// so Create makes exactly the planned set.
    /// </summary>
    private ParCreateRequest BuildRequest(bool exact) => new()
    {
        Inputs = Inputs.Select(i => i.Path).ToList(),
        OutputPath = string.IsNullOrWhiteSpace(OutputPath) ? OutputPlaceholder : OutputPath,
        Format = Format,
        RedundancyPercent = _redundancy,
        RecoveryBlockCount = exact && Plan != null ? Plan.RecoveryBlocks : _exactRecovery,
        BlockCount = _blockTarget,
        BlockSize = exact && Plan != null && !IsPar1 ? Plan.BlockSize : _explicitBlockSize,
        VolumeCount = VolumeCount is decimal vc and > 0 ? (int)vc : null,
        UniformVolumes = VolumeSizingIndex == 1,
        LimitVolumeSize = VolumeSizingIndex == 2,
        FirstRecoveryBlock = FirstRecoveryBlock is decimal first ? (int)first : 0,
        BaseDirectory = string.IsNullOrWhiteSpace(BaseDirectory) ? null : BaseDirectory,
        Recursive = Recursive,
        Comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment,
        Par3Code = CodeIndex switch { 1 => Par3Code.Cauchy, 2 => Par3Code.Fft, _ => Par3Code.Auto },
        Par3Dedup = Dedup ? Par3Dedup.Blocks : Par3Dedup.None,
        Par3PackTails = PackTails,
        Par3StoreData = StoreData,
        Par3UnixPermissions = UnixPermissions,
        Par3FatPermissions = FatPermissions,
        Par3Reproducible = Reproducible,
        Par3MaxRecoveryBlocks = MaxRecoveryBlocks is decimal max and > 0 ? (int)max : null,
        Par3Interleave = Interleave is decimal il ? (int)il : 0,
        Par3CauchyConvention = CauchyConventionIndex == 1 ? Par3CauchyConvention.Specification : Par3CauchyConvention.Par3cmdline,
    };

    // ---------------------------------------------------------------- inputs

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (Dialogs == null)
        {
            return;
        }

        AddPaths(await Dialogs.OpenFilesAsync("Files to protect", true));
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (Dialogs != null && await Dialogs.OpenFolderAsync("Folder to protect") is { } dir)
        {
            Add(dir);
        }
    }

    /// <summary>Adds dropped (or picked) files and folders.</summary>
    [RelayCommand]
    private void AddPaths(IReadOnlyList<string> paths)
    {
        foreach (var p in paths)
        {
            Add(p);
        }
    }

    /// <summary>Adds a file or folder; the first one also names the output.</summary>
    public void Add(string path)
    {
        if (Inputs.Any(i => i.Path == path))
        {
            return;
        }

        Inputs.Add(new CreateInputRow(path));
        if (OutputPath == null)
        {
            var dir = Directory.Exists(path) ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))! : Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(path));
            OutputPath = Path.Combine(dir, name + Extension);
        }
    }

    [RelayCommand]
    private void RemoveInput(CreateInputRow row) => Inputs.Remove(row);

    [RelayCommand]
    private void ClearInputs() => Inputs.Clear();

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (Dialogs != null && await Dialogs.SaveFileAsync("Save recovery set as", Path.GetFileName(OutputPath ?? OutputPlaceholder), Extension) is { } path)
        {
            OutputPath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseBaseAsync()
    {
        if (Dialogs != null && await Dialogs.OpenFolderAsync("Base folder (stored names are relative to it)") is { } dir)
        {
            BaseDirectory = dir;
        }
    }

    // ---------------------------------------------------------------- create

    private bool CanCreate() => !string.IsNullOrWhiteSpace(OutputPath) && Inputs.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (InsideArchive && CanInsertIntoArchive)
        {
            var archive = Inputs[0].Path;
            var insert = new ParInsertRequest
            {
                ArchivePath = archive,
                RedundancyPercent = _redundancy,
                RecoveryBlockCount = _exactRecovery,
                BlockSize = _explicitBlockSize,
            };
            var inserted = await RunAsync("Adding recovery data to the archive", ctx => ParClient.Insert(insert, ctx));
            if (inserted != null)
            {
                StatusText = $"{Path.GetFileName(archive)} now carries {inserted.RecoveryBlocks:N0} recovery block(s) of {Formatting.Bytes(inserted.BlockSize)} ({Formatting.Bytes(inserted.OriginalLength)} → {Formatting.Bytes(inserted.NewLength)}). Open it on the Verify page to check or repair it.";
                Log.Add(new LogLine(DateTime.Now, ParLogLevel.Info, StatusText));
            }

            return;
        }

        await _scanTask;
        var request = BuildRequest(exact: true);
        var report = await RunAsync("Creating", ctx => ParClient.Create(request, ctx));
        if (report != null)
        {
            StatusText = $"Created {report.Format} set: {report.InputBlocks:N0} blocks of {Formatting.Bytes(report.BlockSize)}, {report.RecoveryBlocks:N0} recovery blocks ({report.Description}).";
            Log.Add(new LogLine(DateTime.Now, ParLogLevel.Info, StatusText));
            foreach (var f in report.WrittenFiles)
            {
                Log.Add(new LogLine(DateTime.Now, ParLogLevel.Info, "  " + f));
            }
        }
    }
}

public static class Formatting
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{bytes:N0} B" : $"{v:0.##} {units[u]}";
    }
}
