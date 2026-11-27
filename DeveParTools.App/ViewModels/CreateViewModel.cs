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
                foreach (var f in Directory.EnumerateFiles(i, "*", SearchOption.AllDirectories))
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

        Estimate = files == 0
            ? "Add files or folders to protect."
            : $"{files:N0} file(s), {Formatting.Bytes(total)} → about {Formatting.Bytes((long)(total * Redundancy / 100))} of recovery data ({Redundancy:0.#}%).";
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
            var percent = Redundancy;
            var inserted = await RunAsync("Adding recovery data to the archive", ctx => ParClient.Insert(new ParInsertRequest { ArchivePath = archive, RedundancyPercent = percent }, ctx));
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
