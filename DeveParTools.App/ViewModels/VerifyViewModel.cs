using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.Core.Operations;

namespace DeveParTools.App.ViewModels;

public sealed partial class FileRowViewModel : ObservableObject
{
    public required ParFileReport Report { get; init; }

    public string Name => Report.Name;

    public string Size => Formatting.Bytes(Report.Size);

    public string StatusText => Report.FoundAs != null ? $"{Report.Status} (found as {System.IO.Path.GetFileName(Report.FoundAs)})" : Report.Status.ToString();

    public bool IsOk => Report.Status == ParFileStatus.Complete;

    public string StatusBrushKey => Report.Status switch
    {
        ParFileStatus.Complete => "BlockFoundBrush",
        ParFileStatus.Damaged => "BlockDamagedBrush",
        _ => "BlockMissingBrush",
    };
}

/// <summary>Verify &amp; repair page: pick a .par/.par2/.par3 file, see every file and block, repair.</summary>
public sealed partial class VerifyViewModel : OperationViewModel
{
    public VerifyViewModel(SettingsViewModel settings)
        : base(settings)
    {
    }

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
        Files.Clear();
        foreach (var f in value?.Files ?? [])
        {
            Files.Add(new FileRowViewModel { Report = f });
        }

        foreach (var name in new[] { nameof(Headline), nameof(HealthIconKey), nameof(Details), nameof(FilesOk), nameof(FilesBad), nameof(MissingBlocks), nameof(RecoveryAvailable), nameof(NeedRatio), nameof(NeedText) })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnSelectedFileChanged(FileRowViewModel? value)
    {
        if (value == null || Map == null)
        {
            Highlight = null;
            return;
        }

        var path = value.Report.Path;
        Highlight = Map.Ownership.Where(kv => kv.Value.Any(o => string.Equals(o.File, path, StringComparison.Ordinal))).Select(kv => kv.Key).ToHashSet();
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (Dialogs == null)
        {
            return;
        }

        var files = await Dialogs.OpenFilesAsync("Open a PAR file", false, ["*.par2", "*.par3", "*.par", "*.p??"]);
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
        Report = null;
        var report = await RunAsync("Verifying", ctx => ParClient.Verify(file, new ParVerifyRequest { ExtraFiles = extra }, ctx));
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

    /// <summary>Opens a file passed on the command line or by a file association.</summary>
    public async Task OpenAsync(string path)
    {
        ParFile = path;
        await VerifyAsync();
    }
}
