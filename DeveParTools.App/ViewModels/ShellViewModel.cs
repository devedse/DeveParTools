using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.Core.Galois;

namespace DeveParTools.App.ViewModels;

public enum AppPage
{
    Verify,
    Create,
    Benchmark,
    Settings,
}

/// <summary>The window's view model: navigation between pages that share one settings object.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel()
    {
        Settings = new SettingsViewModel();
        Verify = new VerifyViewModel(Settings);
        Create = new CreateViewModel(Settings);
        Benchmark = new BenchmarkViewModel();
        _currentPage = Verify;
    }

    public SettingsViewModel Settings { get; }

    public VerifyViewModel Verify { get; }

    public CreateViewModel Create { get; }

    public BenchmarkViewModel Benchmark { get; }

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private AppPage _page;

    partial void OnPageChanged(AppPage value) => CurrentPage = value switch
    {
        AppPage.Create => Create,
        AppPage.Benchmark => Benchmark,
        AppPage.Settings => Settings,
        _ => Verify,
    };

    public void SetDialogs(IFileDialogs dialogs)
    {
        Verify.Dialogs = dialogs;
        Create.Dialogs = dialogs;
    }
}

/// <summary>Settings: threads, memory budget, SIMD kernel override and theme. Stored in the user's app-data folder.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly string _path;

    public SettingsViewModel(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeveParTools", "settings.txt");
        Kernels = SimdSupport.SupportedLevels.Select(l => l.ToString()).ToArray();
        _kernel = SimdSupport.Best.ToString();
        Load();
    }

    public string[] Kernels { get; }

    public string[] Themes { get; } = ["System", "Light", "Dark"];

    public string CpuFeatures => SimdSupport.Describe();

    public int MaxThreads => Environment.ProcessorCount;

    public string Version => typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string Runtime => $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";

    [ObservableProperty]
    private int _threads = Environment.ProcessorCount;

    [ObservableProperty]
    private int _memoryMiB = (int)Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4 / (1 << 20), 256, 8192);

    [ObservableProperty]
    private string _kernel;

    [ObservableProperty]
    private string _theme = "System";

    partial void OnThreadsChanged(int value) => Save();

    partial void OnMemoryMiBChanged(int value) => Save();

    partial void OnKernelChanged(string value)
    {
        if (Enum.TryParse<SimdLevel>(value, out var level) && SimdSupport.IsSupported(level))
        {
            Gf16Kernels.Level = level;
            Gf8Kernels.Level = level;
        }

        Save();
    }

    partial void OnThemeChanged(string value)
    {
        ApplyTheme();
        Save();
    }

    public void ApplyTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = Theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        }
    }

    private bool _loading;

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        _loading = true;
        try
        {
            foreach (var line in File.ReadAllLines(_path))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "threads" when int.TryParse(parts[1], out var t) && t > 0:
                        Threads = Math.Min(t, Environment.ProcessorCount);
                        break;
                    case "memory" when int.TryParse(parts[1], out var m) && m >= 64:
                        MemoryMiB = m;
                        break;
                    case "kernel" when Kernels.Contains(parts[1]):
                        Kernel = parts[1];
                        break;
                    case "theme" when Themes.Contains(parts[1]):
                        Theme = parts[1];
                        break;
                }
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, [$"threads={Threads}", $"memory={MemoryMiB}", $"kernel={Kernel}", $"theme={Theme}"]);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record BenchmarkResult(string Name, double GigabytesPerSecond, double Fraction)
{
    public string Text => $"{GigabytesPerSecond:F2} GB/s";
}

/// <summary>Measures the kernels, hashes and the FFT code on this machine.</summary>
public sealed partial class BenchmarkViewModel : ObservableObject
{
    public ObservableCollection<BenchmarkResult> Results { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _summary = $"CPU: {SimdSupport.Describe()} · {Environment.ProcessorCount} threads";

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        Results.Clear();
        try
        {
            // Results appear one by one; bars are scaled to the fastest result so far.
            await Task.Run(() => ParBenchmark.Run(new ParBenchmarkOptions
            {
                TimePerItem = TimePerItem,
                Progress = r => Dispatcher.UIThread.Post(() => AddResult(r)),
            }));
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Time per measurement; the UI tests shorten it.</summary>
    public TimeSpan TimePerItem { get; set; } = TimeSpan.FromMilliseconds(450);

    private void AddResult(ParBenchmarkResult r)
    {
        var all = Results.Select(x => (x.Name, x.GigabytesPerSecond)).Append((r.Name, r.GigabytesPerSecond)).ToList();
        double max = all.Max(x => x.GigabytesPerSecond);
        Results.Clear();
        foreach (var (name, gbps) in all)
        {
            Results.Add(new BenchmarkResult(name, gbps, max > 0 ? gbps / max : 0));
        }
    }
}
