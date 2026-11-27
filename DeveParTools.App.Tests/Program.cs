using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using DeveParTools.App;
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

var shell = new ShellViewModel();
var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
window.Show();

// Create page with inputs, then create a PAR3 set through the view model.
shell.Page = AppPage.Create;
shell.Create.Add(Path.Combine(work, "photos"));
shell.Create.Add(Path.Combine(work, "video.mp4"));
shell.Create.Add(Path.Combine(work, "notes.txt"));
shell.Create.FormatIndex = 1;
shell.Create.Redundancy = 30;
shell.Create.BlockCount = 3000;
shell.Create.OutputPath = Path.Combine(work, "backup.par3");
Await(shell.Create.CreateCommand.ExecuteAsync(null));
Check(File.Exists(Path.Combine(work, "backup.par3")), "PAR3 set created");
Save(window, output, "create");

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
shell.Verify.SelectedFile = shell.Verify.Files.First(f => !f.IsOk);
Save(window, output, "verify-damaged");
Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
Save(window, output, "verify-damaged-dark");
Application.Current!.RequestedThemeVariant = ThemeVariant.Light;

shell.Verify.SelectedFile = null;
Await(shell.Verify.RepairCommand.ExecuteAsync(null));
Check(shell.Verify.StatusText?.StartsWith("Repaired") == true, "repair succeeded: " + shell.Verify.StatusText);
Save(window, output, "verify-repaired");

shell.Page = AppPage.Benchmark;
shell.Benchmark.TimePerItem = TimeSpan.FromMilliseconds(120);
Await(shell.Benchmark.RunCommand.ExecuteAsync(null));
Check(shell.Benchmark.Results.Count > 0, "benchmark produced results");
Save(window, output, "benchmark");

shell.Page = AppPage.Settings;
Save(window, output, "settings");
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

static void Save(Window window, string output, string name)
{
    for (var i = 0; i < 40; i++)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(5);
    }

    using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame for " + name);
    frame.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
    Console.WriteLine("  " + name + ".png");
}
