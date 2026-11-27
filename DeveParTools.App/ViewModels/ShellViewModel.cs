using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.Core.Codes.Fft;
using DeveParTools.Core.Galois;
using DeveParTools.Core.Hashing;

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
            var results = await Task.Run(Measure);
            double max = results.Max(r => r.Item2);
            foreach (var (name, gbps) in results)
            {
                Results.Add(new BenchmarkResult(name, gbps, gbps / max));
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    internal static unsafe List<(string, double)> Measure()
    {
        const int size = 256 * 1024;
        var src = new byte[size];
        var dst = new byte[size];
        new Random(1).NextBytes(src);
        var list = new List<(string, double)>();
        foreach (var level in SimdSupport.SupportedLevels)
        {
            list.Add(($"GF(2^16) multiply-add · {level}", Time(() =>
            {
                using var t = Gf16MulTables.Create(GaloisField16.Par2, [0x1234, 0x4321, 0x9999, 0x0101], level);
                fixed (byte* s = src)
                fixed (byte* d = dst)
                {
                    byte** ptrs = stackalloc byte*[4];
                    for (int i = 0; i < 4; i++)
                    {
                        ptrs[i] = s;
                    }

                    Gf16Kernels.MulAddMulti(t, 0, 4, ptrs, d, size);
                }
            }, 4L * size)));
        }

        list.Add(("BLAKE3", Time(() => Blake3.Hash(src, dst.AsSpan(0, 32)), size)));
        list.Add(("CRC-64-ISO", Time(() => Crc64Iso.Compute(src), size)));
        list.Add(("CRC-32", Time(() => Crc32Ieee.Compute(src), size)));
        list.Add(("MD5", Time(() => System.Security.Cryptography.MD5.HashData(src), size)));

        const int k = 1000, r = 100, bytes = 4096;
        var orig = Enumerable.Range(0, k).Select(_ => (nint)NativeMemory.AlignedAlloc(bytes, 64)).ToArray();
        var work = Enumerable.Range(0, LeopardCodec.EncodeWorkCount(k, r)).Select(_ => (nint)NativeMemory.AlignedAlloc(bytes, 64)).ToArray();
        list.Add(("FFT Reed-Solomon encode (all cores)", Time(() => LeopardCodec.Encode(LeopardField.FF16, orig, r, work, bytes, Environment.ProcessorCount), (long)k * bytes)));
        foreach (var p in orig.Concat(work))
        {
            NativeMemory.AlignedFree((void*)p);
        }

        return list;
    }

    private static double Time(Action action, long bytes)
    {
        action();
        double best = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            int it = 0;
            do
            {
                action();
                it++;
            }
            while (sw.ElapsedMilliseconds < 50);
            best = Math.Min(best, sw.Elapsed.TotalSeconds / it);
        }

        return bytes / best / 1e9;
    }
}
