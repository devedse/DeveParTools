using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.Core.Operations;
using DeveParTools.Par3;

namespace DeveParTools.App.ViewModels;

/// <summary>Create page: choose inputs, format and redundancy, see an estimate, create with a live block grid.</summary>
public sealed partial class CreateViewModel : OperationViewModel
{
    public CreateViewModel(SettingsViewModel settings)
        : base(settings)
    {
        Inputs.CollectionChanged += (_, _) =>
        {
            Recalculate();
            OnPropertyChanged(nameof(CanInsertIntoArchive));
        };
    }

    public ObservableCollection<string> Inputs { get; } = new();

    public string[] Formats { get; } = ["PAR2 (most compatible)", "PAR3 (modern, folders, FFT)", "PAR1 (legacy)"];

    public string[] Codes { get; } = ["Automatic", "Cauchy Reed-Solomon", "FFT Reed-Solomon (fast)"];

    [ObservableProperty]
    private int _formatIndex;

    [ObservableProperty]
    private int _codeIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string? _outputPath;

    [ObservableProperty]
    private double _redundancy = 10;

    [ObservableProperty]
    private decimal? _blockCount = 2000;

    [ObservableProperty]
    private decimal? _blockSize;

    [ObservableProperty]
    private decimal? _volumeCount;

    [ObservableProperty]
    private bool _dedup;

    [ObservableProperty]
    private bool _packTails = true;

    [ObservableProperty]
    private bool _storeData;

    [ObservableProperty]
    private bool _unixPermissions;

    [ObservableProperty]
    private bool _reproducible;

    [ObservableProperty]
    private string? _comment;

    // ---- Advanced options (the rest of "devepar create"'s options) ----

    /// <summary>Exact number of recovery blocks (<c>-c</c>); when set, it replaces <see cref="Redundancy"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesRedundancy))]
    private decimal? _recoveryCount;

    /// <summary>Number of the first recovery block (<c>--first</c>; the PAR2 exponent).</summary>
    [ObservableProperty]
    private decimal? _firstRecoveryBlock;

    public string[] VolumeSizings { get; } = ["Growing (1, 2, 4, 8… blocks per file)", "Equal (same number of blocks per file)", "No file larger than the largest input file (PAR2)"];

    /// <summary>0 = growing (default), 1 = equal (<c>-u</c>), 2 = limited to the largest input (<c>-l</c>).</summary>
    [ObservableProperty]
    private int _volumeSizingIndex;

    /// <summary>Folder that stored names are relative to (<c>-B</c>); default: the folder of the inputs.</summary>
    [ObservableProperty]
    private string? _baseDirectory;

    /// <summary>Include the contents of subfolders (off = <c>--no-recurse</c>).</summary>
    [ObservableProperty]
    private bool _recursive = true;

    /// <summary>PAR3: store FAT/Windows times and attributes (<c>--fat-permissions</c>).</summary>
    [ObservableProperty]
    private bool _fatPermissions;

    /// <summary>PAR3 FFT: the most recovery blocks the set may ever have, which limits later extending (<c>--max-recovery</c>).</summary>
    [ObservableProperty]
    private decimal? _maxRecoveryBlocks;

    /// <summary>PAR3 FFT: number of extra cohorts (<c>-i</c>; empty or 0 = automatic).</summary>
    [ObservableProperty]
    private decimal? _interleave;

    public string[] CauchyConventions { get; } = ["par3cmdline (x = I, compatible)", "PAR3 specification draft (x = I + 1)"];

    /// <summary>PAR3 Cauchy: 0 = par3cmdline's coefficients (default), 1 = the specification's (<c>--spec-cauchy</c>).</summary>
    [ObservableProperty]
    private int _cauchyConventionIndex;

    /// <summary>The redundancy slider applies unless an exact recovery block count is given.</summary>
    public bool UsesRedundancy => RecoveryCount == null;

    partial void OnRecoveryCountChanged(decimal? value) => Recalculate();

    partial void OnBlockSizeChanged(decimal? value) => Recalculate();

    partial void OnRecursiveChanged(bool value) => Recalculate();

    [RelayCommand]
    private async Task BrowseBaseAsync()
    {
        if (Dialogs != null && await Dialogs.OpenFolderAsync("Base folder (stored names are relative to it)") is { } dir)
        {
            BaseDirectory = dir;
        }
    }

    [ObservableProperty]
    private string? _selectedInput;

    [ObservableProperty]
    private string _estimate = "Add files or folders to protect.";

    public bool IsPar3 => FormatIndex == 1;

    /// <summary>"PAR inside" is offered for PAR3 when the only input is a .zip, .7z or .rar archive.</summary>
    public bool CanInsertIntoArchive => IsPar3 && Inputs.Count == 1 && File.Exists(Inputs[0])
        && Path.GetExtension(Inputs[0]).ToLowerInvariant() is ".zip" or ".7z" or ".rar";

    [ObservableProperty]
    private bool _insideArchive;

    partial void OnFormatIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPar3));
        OnPropertyChanged(nameof(CanInsertIntoArchive));
        if (OutputPath != null)
        {
            OutputPath = Path.ChangeExtension(OutputPath, Extension);
        }

        Recalculate();
    }

    partial void OnRedundancyChanged(double value) => Recalculate();

    partial void OnBlockCountChanged(decimal? value) => Recalculate();

    private string Extension => FormatIndex switch { 1 => ".par3", 2 => ".par", _ => ".par2" };

    private void Recalculate()
    {
        long total = 0;
        int files = 0;
        foreach (var i in Inputs)
        {
            if (Directory.Exists(i))
            {
                foreach (var f in Directory.EnumerateFiles(i, "*", Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    total += new FileInfo(f).Length;
                    files++;
                }
            }
            else if (File.Exists(i))
            {
                total += new FileInfo(i).Length;
                files++;
            }
        }

        if (files == 0)
        {
            Estimate = "Add files or folders to protect.";
        }
        else if (RecoveryCount is decimal count)
        {
            // An exact count: recovery data = count × block size (given, or about total / target block count).
            long block = BlockSize is decimal bs and > 0 ? (long)bs : (long)Math.Ceiling(total / (double)Math.Max(1m, BlockCount ?? 2000));
            Estimate = $"{files:N0} file(s), {Formatting.Bytes(total)} → about {Formatting.Bytes((long)count * block)} of recovery data ({count:N0} block(s)).";
        }
        else
        {
            Estimate = $"{files:N0} file(s), {Formatting.Bytes(total)} → about {Formatting.Bytes((long)(total * Redundancy / 100))} of recovery data ({Redundancy:0.#}%).";
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (Dialogs == null)
        {
            return;
        }

        foreach (var f in await Dialogs.OpenFilesAsync("Files to protect", true))
        {
            Add(f);
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (Dialogs != null && await Dialogs.OpenFolderAsync("Folder to protect") is { } dir)
        {
            Add(dir);
        }
    }

    /// <summary>Adds a file or folder (also used by drag and drop).</summary>
    public void Add(string path)
    {
        if (Inputs.Contains(path))
        {
            return;
        }

        Inputs.Add(path);
        if (OutputPath == null)
        {
            var dir = Directory.Exists(path) ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))! : Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(path));
            OutputPath = Path.Combine(dir, name + Extension);
        }
    }

    [RelayCommand]
    private void RemoveInput(string path) => Inputs.Remove(path);

    [RelayCommand]
    private void ClearInputs() => Inputs.Clear();

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (Dialogs != null && await Dialogs.SaveFileAsync("Save recovery set as", Path.GetFileName(OutputPath ?? "recovery" + Extension), Extension) is { } path)
        {
            OutputPath = path;
        }
    }

    private bool CanCreate() => !string.IsNullOrWhiteSpace(OutputPath) && Inputs.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (InsideArchive && CanInsertIntoArchive)
        {
            var archive = Inputs[0];
            var insert = new ParInsertRequest
            {
                ArchivePath = archive,
                RedundancyPercent = Redundancy,
                RecoveryBlockCount = RecoveryCount is decimal rc ? (int)rc : null,
                BlockSize = BlockSize is decimal ibs and > 0 ? (long)ibs : null,
            };
            var inserted = await RunAsync("Adding recovery data to the archive", ctx => ParClient.Insert(insert, ctx));
            if (inserted != null)
            {
                StatusText = $"{Path.GetFileName(archive)} now carries {inserted.RecoveryBlocks:N0} recovery block(s) of {Formatting.Bytes(inserted.BlockSize)} ({Formatting.Bytes(inserted.OriginalLength)} → {Formatting.Bytes(inserted.NewLength)}). Open it on the Verify page to check or repair it.";
                Log.Add(new LogLine(DateTime.Now, ParLogLevel.Info, StatusText));
            }

            return;
        }

        var request = new ParCreateRequest
        {
            Inputs = Inputs.ToList(),
            OutputPath = OutputPath!,
            Format = FormatIndex switch { 1 => ParFormat.Par3, 2 => ParFormat.Par1, _ => ParFormat.Par2 },
            RedundancyPercent = Redundancy,
            BlockCount = BlockCount is decimal bc ? (int)bc : null,
            BlockSize = BlockSize is decimal bs and > 0 ? (long)bs : null,
            VolumeCount = VolumeCount is decimal vc and > 0 ? (int)vc : null,
            Comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment,
            Par3Code = CodeIndex switch { 1 => Par3Code.Cauchy, 2 => Par3Code.Fft, _ => Par3Code.Auto },
            Par3Dedup = Dedup ? Par3Dedup.Blocks : Par3Dedup.None,
            Par3PackTails = PackTails,
            Par3StoreData = StoreData,
            Par3UnixPermissions = UnixPermissions,
            Par3Reproducible = Reproducible,
            RecoveryBlockCount = RecoveryCount is decimal rc2 ? (int)rc2 : null,
            FirstRecoveryBlock = FirstRecoveryBlock is decimal first ? (int)first : 0,
            UniformVolumes = VolumeSizingIndex == 1,
            LimitVolumeSize = VolumeSizingIndex == 2,
            BaseDirectory = string.IsNullOrWhiteSpace(BaseDirectory) ? null : BaseDirectory,
            Recursive = Recursive,
            Par3FatPermissions = FatPermissions,
            Par3MaxRecoveryBlocks = MaxRecoveryBlocks is decimal max and > 0 ? (int)max : null,
            Par3Interleave = Interleave is decimal il ? (int)il : 0,
            Par3CauchyConvention = CauchyConventionIndex == 1 ? Par3CauchyConvention.Specification : Par3CauchyConvention.Par3cmdline,
        };
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
