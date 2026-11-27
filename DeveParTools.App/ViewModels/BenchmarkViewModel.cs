using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeveParTools.Core.Galois;

namespace DeveParTools.App.ViewModels;

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
                BufferSize = (int)Math.Clamp(BufferKiB ?? 256, 4, 1 << 20) * 1024,
                Progress = r => Dispatcher.UIThread.Post(() => AddResult(r)),
            }));
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Buffer size per measurement in KiB (<c>bench --size</c>).</summary>
    [ObservableProperty]
    private decimal? _bufferKiB = 256;

    /// <summary>Milliseconds per measurement, the first third of which warms up (<c>bench --time</c>).</summary>
    [ObservableProperty]
    private decimal? _timeMs = 450;

    /// <summary>Time per measurement; the UI tests shorten it.</summary>
    public TimeSpan TimePerItem
    {
        get => TimeSpan.FromMilliseconds((double)Math.Max(3, TimeMs ?? 450));
        set => TimeMs = (decimal)value.TotalMilliseconds;
    }

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
