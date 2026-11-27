using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeveParTools.App;
using DeveParTools.App.Services;
using DeveParTools.App.ViewModels;
using DeveParTools.App.Views;

// Headless checks of the desktop app: real PAR operations on temporary files, every page rendered in light and dark.
// Screenshots go to the output folder (default artifacts/ui-tests).
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
// The view models await background work; their continuations must come back to this (the UI) thread.
SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());
var output = args.Length == 0 ? "artifacts/ui-tests" : args[0];
Directory.CreateDirectory(output);

var work = Path.Combine(Path.GetTempPath(), "deveparttools-ui", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(work, "photos"));
var rnd = new Random(7);
foreach (var (name, size) in new[] { ("photos/holiday-001.jpg", 2_400_000), ("photos/holiday-002.jpg", 1_900_000), ("photos/holiday-003.jpg", 2_100_000), ("video.mp4", 6_000_000), ("notes.txt", 12_345) })
{
    var data = new byte[size];
    rnd.NextBytes(data);
    File.WriteAllBytes(Path.Combine(work, name), data);
}

// Command line: "--create" collects existing files and folders; otherwise the first existing file is verified.
var parsed = AppArguments.Parse(["--create", Path.Combine(work, "notes.txt"), Path.Combine(work, "missing.bin"), Path.Combine(work, "photos"), Path.Combine(work, "notes.txt")]);
Check(parsed.OpenFile == null && parsed.CreatePaths.SequenceEqual([Path.Combine(work, "notes.txt"), Path.Combine(work, "photos")]), "--create keeps existing files and folders once each");
Check(AppArguments.Parse(["-x", Path.Combine(work, "notes.txt")]) is { CreatePaths.Count: 0, OpenFile: not null }, "a plain file argument is opened for verification");

// Explorer entries: the quoting of the command lines, and reading the program back from one.
const string ProgramFilesExe = @"C:\Program Files\DeveParTools\deveparui.exe";
var entries = ExplorerContextMenu.Entries(ProgramFilesExe);
Check(entries.Count(e => e.Text == ExplorerContextMenu.CreateText && e.MultiSelect) == 2 && entries.Count(e => e.KeyPath.EndsWith(@"\shell\" + ExplorerContextMenu.VerifyVerb, StringComparison.Ordinal)) == 3, "create entries for files and folders, verify entries for .par/.par2/.par3");
Check(entries[0].Command == $"\"{ProgramFilesExe}\" --create \"%1\"" && entries[2].Command == $"\"{ProgramFilesExe}\" \"%1\"", "menu command lines quote the program and the item: " + entries[0].Command);
Check(ExplorerContextMenu.ExecutableOf(entries[0].Command) == ProgramFilesExe && ExplorerContextMenu.ExecutableOf(@"C:\x.exe ""%1""") == @"C:\x.exe" && ExplorerContextMenu.ExecutableOf("  ") == null, "the program is read back from a command line");

// A multi-selection in Explorer starts one process per item; the first collects the others' paths.
{
    var name = "DeveParTools.App.Tests." + Guid.NewGuid().ToString("N")[..12];
    using var first = CreateRequestCollector.StartOrForward(name, ["item0"], TimeSpan.FromSeconds(2));
    Check(first is { IsCollecting: true }, "the first process of a multi-selection collects");
    var arrived = new List<string>();
    var senders = Enumerable.Range(1, 6).Select(i => Task.Run(() => CreateRequestCollector.StartOrForward(name, [$"item{i}", $"extra{i}"], TimeSpan.FromSeconds(2)))).ToArray();
    Task.WaitAll(senders);
    first!.Attach(paths =>
    {
        lock (arrived)
        {
            arrived.AddRange(paths);
        }
    });
    Check(senders.All(t => t.Result == null), "the other processes hand their paths over and exit");
    lock (arrived)
    {
        Check(arrived.Count == 12 && Enumerable.Range(1, 6).All(i => arrived.Contains($"item{i}") && arrived.Contains($"extra{i}")), $"all paths arrive in the first window ({arrived.Count})");
    }

    var until = DateTime.UtcNow.AddSeconds(10);
    while (first.IsCollecting && DateTime.UtcNow < until)
    {
        Thread.Sleep(50);
    }

    Check(!first.IsCollecting, "the first window stops collecting once the selection stops arriving");
    using var later = CreateRequestCollector.StartOrForward(name, ["later"], TimeSpan.FromSeconds(1));
    Check(later is { IsCollecting: true }, "a later, separate right-click opens its own window");
}

// Started with --create, the window opens on the Create page (the navigation must not jump back to Verify).
{
    var createShell = new ShellViewModel();
    createShell.Page = AppPage.Create;
    var createWindow = new MainWindow { DataContext = createShell, Width = 1000, Height = 700 };
    createWindow.Show();
    Dispatcher.UIThread.RunJobs();
    Check(createShell.Page == AppPage.Create && createShell.CurrentPage == createShell.Create, "a window started on the Create page stays there");
    createWindow.Close();
}

var shell = new ShellViewModel();
var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
window.Show();
Dispatcher.UIThread.RunJobs();
Check(window.Title == "DeveParTools " + DeveParTools.App.Services.AppInfo.Version && shell.FooterPages[0].Title == "Version " + DeveParTools.App.Services.AppInfo.Version, "the version is in the title bar and above Settings: " + window.Title);
shell.SelectedNavigation = shell.FooterPages[0];
Check(shell.Page == AppPage.Verify && !shell.FooterPages[0].Selectable, "the version entry is not a page");

// Create page with inputs, then create a PAR3 set through the view model.
shell.Page = AppPage.Create;
shell.Create.Add(Path.Combine(work, "photos"));
shell.Create.Add(Path.Combine(work, "video.mp4"));
shell.Create.Add(Path.Combine(work, "notes.txt"));
shell.Create.FormatIndex = 0;
shell.Create.Redundancy = 30;
shell.Create.BlockCount = 3000;
shell.Create.OutputPath = Path.Combine(work, "backup.par3");
Await(shell.Create.Planned);
Check(shell.Create.Plan is { InputBlocks: > 2500 and <= 3000, Files.Count: 5 } && shell.Create.RecoveryFiles.Count > 0, $"the plan shows the real set: {shell.Create.Plan?.InputBlocks} blocks of {shell.Create.BlockSizeText}");
Save(window, output, "create");
Await(shell.Create.CreateCommand.ExecuteAsync(null));
Check(File.Exists(Path.Combine(work, "backup.par3")), "PAR3 set created");
Save(window, output, "create-done");

// Damage: overwrite part of a photo, delete another, shift the video.
var photo = File.ReadAllBytes(Path.Combine(work, "photos/holiday-001.jpg"));
Array.Clear(photo, 500_000, 300_000);
File.WriteAllBytes(Path.Combine(work, "photos/holiday-001.jpg"), photo);
File.Delete(Path.Combine(work, "photos/holiday-003.jpg"));
var video = File.ReadAllBytes(Path.Combine(work, "video.mp4")).ToList();
video.InsertRange(1_000_000, new byte[777]);
File.WriteAllBytes(Path.Combine(work, "video.mp4"), video.ToArray());

shell.Page = AppPage.Verify;
Await(shell.Verify.OpenAsync(Path.Combine(work, "backup.par3")));
if (shell.Verify.Report is not { AllFilesComplete: false, RepairPossible: true })
{
    foreach (var l in shell.Verify.Log)
    {
        Console.WriteLine("    log: " + l.Text);
    }

    Console.WriteLine($"    report: {shell.Verify.Report?.AllFilesComplete} {shell.Verify.Report?.RepairPossible} {shell.Verify.Report?.MissingBlocks}/{shell.Verify.Report?.RecoveryBlocksAvailable} status={shell.Verify.StatusText}");
}

Check(shell.Verify.Report is { AllFilesComplete: false, RepairPossible: true }, "damage detected, repair possible");
shell.Verify.SelectedBlock = 0;
Settle();
Check(shell.Verify.SelectedBlockInfo?.StartsWith("Block", StringComparison.Ordinal) == true, "the grid describes the selected block to the view model: " + shell.Verify.SelectedBlockInfo?.Split('\n')[0]);
shell.Verify.SelectedBlock = -1;
shell.Verify.SelectedFile = shell.Verify.Files.First(f => !f.IsOk);
Save(window, output, "verify-damaged");
Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
Save(window, output, "verify-damaged-dark");
Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

shell.Verify.SelectedFile = null;
Await(shell.Verify.RepairCommand.ExecuteAsync(null));
Check(shell.Verify.StatusText?.StartsWith("Repaired") == true, "repair succeeded: " + shell.Verify.StatusText);
Save(window, output, "verify-repaired");

// Extend: the set is complete again, so more recovery data can be added.
Check(shell.Verify.CanOfferExtend && shell.Verify.ExtendCommand.CanExecute(null), "extend is offered for a complete PAR3 set");
int recoveryBefore = shell.Verify.RecoveryAvailable;
shell.Verify.ExtendPercent = 3; // the FFT set was made for up to 1024 recovery blocks and has 899
Await(shell.Verify.ExtendCommand.ExecuteAsync(null));
Check(shell.Verify.RecoveryAvailable > recoveryBefore && shell.Verify.StatusText?.StartsWith("Added") == true, $"extend added recovery blocks: {recoveryBefore} -> {shell.Verify.RecoveryAvailable} ({shell.Verify.StatusText})");

// PAR inside: protect a ZIP through the Create page, then verify the ZIP itself.
var zipPath = Path.Combine(work, "photos.zip");
System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(work, "photos"), zipPath);
var zipOriginal = File.ReadAllBytes(zipPath);
shell.Page = AppPage.Create;
shell.Create.ClearInputsCommand.Execute(null);
shell.Create.Add(zipPath);
shell.Create.FormatIndex = 0;
Check(shell.Create.CanInsertIntoArchive, "PAR inside is offered for a single archive");
shell.Create.InsideArchive = true;
shell.Create.Redundancy = 5;
Await(shell.Create.CreateCommand.ExecuteAsync(null));
Check(shell.Create.StatusText?.Contains("now carries", StringComparison.Ordinal) == true, "archive protected: " + shell.Create.StatusText);
shell.Page = AppPage.Verify;
Await(shell.Verify.OpenAsync(zipPath));
Check(shell.Verify.Report is { AllFilesComplete: true, Format: DeveParTools.ParFormat.Par3 }, "the archive verifies itself");
Save(window, output, "verify-inside-zip");

// Remove the recovery data from the archive again: it gets its original bytes back (after a confirmation).
Check(shell.Verify.CanOfferRemoveInside, "removing the recovery data is offered for a protected archive");
shell.Verify.Dialogs = new ConfirmingDialogs();
Await(shell.Verify.RemoveInsideCommand.ExecuteAsync(null));
shell.SetDialogs(window);
Check(File.ReadAllBytes(zipPath).AsSpan().SequenceEqual(zipOriginal) && shell.Verify.Report == null, "the archive is back to its original bytes: " + shell.Verify.StatusText);

// Advanced create options: exact recovery count, first block number, equal-size files.
shell.Page = AppPage.Create;
shell.Create.ClearInputsCommand.Execute(null);
shell.Create.Add(Path.Combine(work, "video.mp4"));
shell.Create.Add(Path.Combine(work, "notes.txt"));
shell.Create.FormatIndex = 1;
shell.Create.BlockCount = 200;
shell.Create.RecoveryCount = 6;
shell.Create.FirstRecoveryBlock = 10;
shell.Create.VolumeSizingIndex = 1;
shell.Create.VolumeCount = 2;
shell.Create.OutputPath = Path.Combine(work, "advanced.par2");
Await(shell.Create.Planned);
Check(shell.Create.RecoveryCount == 6 && shell.Create.RecoveryFiles.Count == 3 && shell.Create.RecoveryFiles[0].Blocks == "index" && shell.Create.RecoveryFiles[1].Name.StartsWith("advanced.vol10+", StringComparison.Ordinal),
    "an exact recovery count, first block and equal files are planned: " + string.Join(", ", shell.Create.RecoveryFiles.Select(f => f.Name)));
Settle();
foreach (var expander in window.GetVisualDescendants().OfType<Expander>().Where(e => e.Header as string == "Advanced options"))
{
    expander.IsExpanded = true;
}

Save(window, output, "create-advanced");
shell.Create.FormatIndex = 0;
Save(window, output, "create-advanced-par3");
shell.Create.FormatIndex = 1;
Await(shell.Create.CreateCommand.ExecuteAsync(null));
var volumes = Directory.GetFiles(work, "advanced.vol*.par2").Select(Path.GetFileName).Order().ToList();
Check(volumes.Count == 2 && volumes[0]!.StartsWith("advanced.vol10+", StringComparison.Ordinal), "6 recovery blocks from number 10 in 2 files: " + string.Join(", ", volumes));

// Extend with an exact count, a file count and another file name.
shell.Page = AppPage.Verify;
Await(shell.Verify.OpenAsync(Path.Combine(work, "advanced.par2")));
Check(shell.Verify.RecoveryAvailable == 6, "the advanced set has 6 recovery blocks: " + shell.Verify.RecoveryAvailable);
shell.Verify.ExtendCount = 4;
shell.Verify.ExtendVolumes = 1;
shell.Verify.ExtendOutput = Path.Combine(work, "more.par2");
Settle();
foreach (var expander in window.GetVisualDescendants().OfType<Expander>().Where(e => e.Header as string == "More options"))
{
    expander.IsExpanded = true;
}

Save(window, output, "verify-options");
Await(shell.Verify.ExtendCommand.ExecuteAsync(null));
var more = Directory.GetFiles(work, "more.vol*.par2").Select(Path.GetFileName).ToList();
Check(more.Count == 1 && more[0]!.StartsWith("more.vol16+", StringComparison.Ordinal), "4 more blocks from number 16 in 1 file: " + string.Join(", ", more));

// The linked controls show what will really be made.
shell.Page = AppPage.Create;
shell.Create.Redundancy = 25;
var linked = shell.Create.Plan!;
Check(linked.RecoveryBlocks == (int)Math.Ceiling(linked.InputBlocks * 0.25) && shell.Create.RecoveryCount == linked.RecoveryBlocks, $"the redundancy slider sets the recovery count: {linked.RecoveryBlocks} of {linked.InputBlocks}");
shell.Create.BlockCount = 100;
Check(shell.Create.BlockCount is > 0 and <= 100 && shell.Create.BlockSize == shell.Create.Plan!.BlockSize, $"a target block count gives the real count and size: {shell.Create.BlockCount} × {shell.Create.BlockSize}");
shell.Create.BlockSize = 4097;
long videoLength = new FileInfo(Path.Combine(work, "video.mp4")).Length, notesLength = new FileInfo(Path.Combine(work, "notes.txt")).Length;
Check(shell.Create.BlockSize == 4100 && shell.Create.BlockCount == (videoLength + 4099) / 4100 + (notesLength + 4099) / 4100, $"a typed block size is rounded up to a multiple of 4 and gives the count: {shell.Create.BlockCount}");
shell.Create.RecoveryCount = 7;
Check(shell.Create.RedundancyText == $"{700.0 / (double)shell.Create.BlockCount!:0.00}%", "an exact recovery count shows the real percentage: " + shell.Create.RedundancyText);
shell.Create.BlockSlider = 5;
Check(shell.Create.BlockCount is > 0 and <= 32, "the block slider (log scale) sets a target of 2^5 = 32 blocks: " + shell.Create.BlockCount);
Await(shell.Create.Planned);
Check(shell.Create.RebuildText.Contains(" of 2", StringComparison.Ordinal) && shell.Create.EfficiencyText.EndsWith('%') && shell.Create.ParFilesText.EndsWith("file(s)", StringComparison.Ordinal)
    && shell.Create.RecoveryFiles[0].Blocks == "index", $"rebuild {shell.Create.RebuildText}, PAR files {shell.Create.ParFilesText}, efficiency {shell.Create.EfficiencyText}");
shell.Create.FormatIndex = 2;
Check(!shell.Create.HasBlockChoice && shell.Create.BlockCount == 2, "PAR1: one block per file, no block choice");
shell.Create.FormatIndex = 1;

shell.Page = AppPage.Benchmark;
shell.Benchmark.TimePerItem = TimeSpan.FromMilliseconds(120);
shell.Benchmark.BufferKiB = 64;
Await(shell.Benchmark.RunCommand.ExecuteAsync(null));
Check(shell.Benchmark.Results.Count > 0, "benchmark produced results");
Save(window, output, "benchmark");


// File associations, Linux style (plain files, so this runs everywhere): desktop entry, MIME types, default apps.
{
    var xdg = Path.Combine(work, "xdg");
    var linux = new LinuxFileAssociations(Path.Combine(xdg, "data"), Path.Combine(xdg, "config"), () => new MemoryStream([1, 2, 3]), runTools: false);
    Directory.CreateDirectory(Path.Combine(xdg, "config"));
    File.WriteAllLines(linux.MimeAppsList, ["[Default Applications]", "text/plain=gedit.desktop", "application/x-par2=other-par.desktop", "", "[Added Associations]", "image/png=eog.desktop;"]);
    const string WeirdExe = "/opt/my apps/$dir \"x\"/deveparui";
    var settings = new SettingsViewModel(Path.Combine(work, "settings-linux.txt"), new ExplorerContextMenu(), WeirdExe, linux);
    Check(settings is { AssociationsSupported: true, AssociateFiles: false, AssociationsEditable: true }, "file associations are offered and off");
    settings.AssociateFiles = true;
    var desktop = File.ReadAllText(linux.DesktopFile);
    var mimeApps = File.ReadAllLines(linux.MimeAppsList);
    Check(desktop.Contains("MimeType=application/x-par2;application/x-par3;application/x-par;", StringComparison.Ordinal) && File.Exists(linux.MimePackage) && File.ReadAllBytes(linux.IconFile).Length == 3,
        "the desktop entry, MIME types and icon are written");
    Check(LinuxFileAssociations.UnquoteExec(LinuxFileAssociations.QuoteExec(WeirdExe)) == WeirdExe && linux.Read().Executable == WeirdExe, "a program path with spaces, $ and quotes survives the Exec quoting: " + desktop.Split('\n').First(l => l.StartsWith("Exec=", StringComparison.Ordinal)));
    Check(mimeApps.Contains("application/x-par2=deveparui.desktop") && mimeApps.Contains("application/x-par3=deveparui.desktop") && mimeApps.Contains("text/plain=gedit.desktop") && mimeApps.Contains("image/png=eog.desktop;"),
        "the PAR types default to DeveParTools; other defaults are kept");
    Check(settings.AssociationStatus == $"PAR files open with {WeirdExe}.", "the status names the program: " + settings.AssociationStatus);

    // The user picks another program for .par3 afterwards.
    var lines = File.ReadAllLines(linux.MimeAppsList).Select(l => l == "application/x-par3=deveparui.desktop" ? "application/x-par3=someone.desktop" : l).ToArray();
    File.WriteAllLines(linux.MimeAppsList, lines);
    settings.ReadAssociations();
    Check(settings.AssociationStatus?.StartsWith(".par3 files still open with someone.desktop", StringComparison.Ordinal) == true && !settings.CanOpenDefaultApps, "a type the user gave to another program is reported: " + settings.AssociationStatus);

    settings.AssociateFiles = false;
    mimeApps = File.ReadAllLines(linux.MimeAppsList);
    Check(!File.Exists(linux.DesktopFile) && !File.Exists(linux.MimePackage) && !mimeApps.Any(l => l.Contains("deveparui.desktop", StringComparison.Ordinal)) && mimeApps.Contains("text/plain=gedit.desktop") && mimeApps.Contains("application/x-par3=someone.desktop"),
        "turning it off removes only DeveParTools' files and entries");
    var fresh = new List<string>();
    LinuxFileAssociations.SetEntry(fresh, "Default Applications", "a/b", "x.desktop");
    LinuxFileAssociations.SetEntry(fresh, "Default Applications", "a/b", "y.desktop");
    Check(fresh.SequenceEqual(["[Default Applications]", "a/b=y.desktop"]) && LinuxFileAssociations.GetEntry(fresh, "Other", "a/b") == null, "mimeapps.list editing: add, replace, missing section");
}
shell.Page = AppPage.Settings;
Save(window, output, "settings");
if (OperatingSystem.IsWindows())
{
    // The Settings switch writes the entries into a scratch key that stands in for HKCU\Software\Classes.
    var root = $@"Software\DeveParTools.Tests\{Guid.NewGuid():N}";
    const string Exe = @"C:\Tools\deveparui.exe";
    var menu = new ExplorerContextMenu(root);
    var settings = new SettingsViewModel(Path.Combine(work, "settings.txt"), menu, Exe);
    Check(settings is { ExplorerMenuSupported: true, ExplorerMenu: false, ExplorerMenuEditable: true }, "the Explorer option is offered and off");
    settings.ExplorerMenu = true;
    using (var command = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{root}\SystemFileAssociations\.par2\shell\{ExplorerContextMenu.VerifyVerb}\command"))
    using (var create = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{root}\Directory\shell\{ExplorerContextMenu.CreateVerb}"))
    {
        Check((string?)command?.GetValue(null) == $"\"{Exe}\" \"%1\"" && (string?)create?.GetValue("MUIVerb") == ExplorerContextMenu.CreateText && (string?)create?.GetValue("MultiSelectModel") == "Player", "turning it on writes the entries");
    }

    Check(menu.RegisteredExecutable() == Exe && settings.ExplorerMenuStatus?.Contains(Exe, StringComparison.Ordinal) == true, "the switch shows which program the entries start");
    Check(new SettingsViewModel(Path.Combine(work, "settings.txt"), menu, @"D:\Other\deveparui.exe").ExplorerMenuStatus?.Contains("another copy", StringComparison.Ordinal) == true, "another copy of the app sees that the entries are not its own");
    Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{root}\SystemFileAssociations\.par3\shell\SomeoneElse").Dispose();

    settings.ExplorerMenu = false;
    using (var classes = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root))
    {
        Check(menu.RegisteredExecutable() == null && classes?.OpenSubKey(@"SystemFileAssociations\.par2") == null && classes?.OpenSubKey(@"*\shell") == null, "turning it off removes the entries and the keys they left empty");
        Check(classes?.OpenSubKey(@"SystemFileAssociations\.par3\shell\SomeoneElse") != null, "another program's entry next to ours is kept");
    }

    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root);

    // File associations in a scratch key standing in for HKCU\Software.
    var softwareRoot = $@"Software\DeveParTools.Tests\{Guid.NewGuid():N}";
    var classesRoot = softwareRoot + @"\Classes";
    var user = Microsoft.Win32.Registry.CurrentUser;
    using (var par2 = user.CreateSubKey(classesRoot + @"\.par2"))
    {
        par2.SetValue(null, "MultiPar.par2");
    }

    var windows = new WindowsFileAssociations(softwareRoot);
    var dialogs = new ConfirmingDialogs();
    var assoc = new SettingsViewModel(Path.Combine(work, "settings-win.txt"), new ExplorerContextMenu(classesRoot), Exe, windows) { Dialogs = dialogs };
    Check(assoc is { AssociationsSupported: true, AssociateFiles: false }, "file associations are offered and off");
    assoc.AssociateFiles = true;
    using (var par2 = user.OpenSubKey(classesRoot + @"\.par2"))
    using (var open = user.OpenSubKey(classesRoot + @"\DeveParTools.par2\shell\open\command"))
    using (var p05 = user.OpenSubKey(classesRoot + @"\.p05\OpenWithProgids"))
    using (var app = user.OpenSubKey(classesRoot + @"\Applications\deveparui.exe\SupportedTypes"))
    using (var registered = user.OpenSubKey(softwareRoot + @"\RegisteredApplications"))
    {
        Check((string?)par2?.GetValue(null) == "DeveParTools.par2" && (string?)par2?.GetValue("DeveParTools.Previous") == "MultiPar.par2" && (string?)open?.GetValue(null) == $"\"{Exe}\" \"%1\"",
            "the types open with DeveParTools; the previous default is remembered");
        Check(p05?.GetValueNames().Contains("DeveParTools.par") == true && app?.GetValueNames().Contains(".par3") == true && (string?)registered?.GetValue("DeveParTools") == softwareRoot + @"\DeveParTools\Capabilities",
            "PAR1 volumes, Open with and Default apps registration");
    }

    Check(assoc.AssociationStatus == $"PAR files open with {Exe}." && !assoc.CanOpenDefaultApps, "the status names the program: " + assoc.AssociationStatus);
    using (var choice = user.CreateSubKey(softwareRoot + @"\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.par2\UserChoice"))
    {
        choice.SetValue("ProgId", "MultiPar.par2");
    }

    assoc.ReadAssociations();
    Check(assoc.CanOpenDefaultApps && assoc.AssociationStatus?.Contains("still open with MultiPar.par2", StringComparison.Ordinal) == true, "a type the user gave to another program is reported: " + assoc.AssociationStatus);
    Await(assoc.OpenDefaultAppsCommand.ExecuteAsync(null));
    Check(dialogs.Launched.SingleOrDefault()?.ToString() == "ms-settings:defaultapps?registeredAppUser=DeveParTools", "\"Choose in Windows\" opens the Default apps page");

    assoc.AssociateFiles = false;
    using (var classes = user.OpenSubKey(classesRoot))
    {
        Check((string?)classes?.OpenSubKey(".par2")?.GetValue(null) == "MultiPar.par2" && classes?.OpenSubKey(".par3") == null && classes?.OpenSubKey("DeveParTools.par2") == null && classes?.OpenSubKey(@"Applications\deveparui.exe") == null,
            "turning it off gives .par2 back to the previous program and removes the rest");
    }

    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(softwareRoot);
    Save(window, output, "settings-windows");
}

Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
Save(window, output, "settings-dark");

window.Close();
Directory.Delete(work, true);
Console.WriteLine("App checks passed. Screenshots in " + Path.GetFullPath(output));

static void Await(Task task)
{
    var until = DateTime.UtcNow.AddMinutes(5);
    while (!task.IsCompleted)
    {
        if (DateTime.UtcNow > until)
        {
            throw new TimeoutException("Operation did not finish.");
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(5);
    }

    task.GetAwaiter().GetResult();
    Dispatcher.UIThread.RunJobs();
}

static void Check(bool condition, string what)
{
    if (!condition)
    {
        throw new Exception("Check failed: " + what);
    }

    Console.WriteLine("  ok: " + what);
}

// Lets layout and rendering catch up (e.g. after switching pages, before looking for a page's controls).
static void Settle()
{
    for (var i = 0; i < 40; i++)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
    }
}

static void Save(Window window, string output, string name)
{
    Settle();

    using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame for " + name);
    frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    Console.WriteLine("  " + name + ".png");
}

/// <summary>Dialogs for the checks: confirms everything, picks nothing.</summary>
internal sealed class ConfirmingDialogs : IViewServices
{
    public Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool multiple, IReadOnlyList<string>? patterns = null) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> OpenFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task<string?> SaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult<string?>(null);

    public Task<bool> ConfirmAsync(string title, string message, string confirm) => Task.FromResult(true);

    public List<Uri> Launched { get; } = [];

    public Task LaunchAsync(Uri uri)
    {
        Launched.Add(uri);
        return Task.CompletedTask;
    }
}
