using System.Diagnostics;
using DeveParTools.Core.Operations;

namespace DeveParTools.Par3;

/// <summary>
/// Progress of an FFT encode or repair, which goes through the blocks in passes (as many as the memory budget needs):
/// every pass reads a slice of each input block, computes, and writes a slice of each output. All three show, with
/// what is happening now, e.g. "pass 3 of 9: computing the recovery data".
/// </summary>
/// <remarks>
/// <para>Each pass is 1/passes of the total. Within a pass, reading, computing and writing get shares of it: a guess for
/// the first pass (they depend on the disk, the processor and the number of blocks), then the times the earlier passes
/// really took, so the bar moves at an even pace. Example: pass 1 took 2 s to read, 1.5 s to compute and 0.5 s to
/// write, so from pass 2 on reading is 50% of a pass, computing 37.5% and writing 12.5%.</para>
/// <para>A pass's input is read in the background while the pass before it is computed and written (see
/// <c>Par3Fft.RunPasses</c>), so the reading phase is only the wait for what is left of that read, often nothing.</para>
/// <para>Reports come from several threads (the computing is parallel); the value never goes back.</para>
/// </remarks>
internal sealed class FftPassProgress(IProgress<double>? progress, int passes, string computing, string writing)
{
    public enum Phase
    {
        Reading,
        Computing,
        Writing,
    }

    private readonly Lock _lock = new();
    private readonly double[] _share = [0.5, 0.3, 0.2];
    private readonly double[] _measured = new double[3];
    private readonly Stopwatch _clock = new();
    private int _pass = -1;
    private Phase _phase;
    private double _last;

    /// <summary>The share of a pass each phase gets now (for tests).</summary>
    internal IReadOnlyList<double> Shares => _share;

    /// <summary>Starts the next pass with reading.</summary>
    public void StartPass()
    {
        lock (_lock)
        {
            if (_pass >= 0)
            {
                EndPhase();
                double total = _measured.Sum();
                if (total > 0)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        _share[i] = _measured[i] / total;
                    }
                }
            }

            _pass++;
            _phase = Phase.Reading;
            _clock.Restart();
        }
    }

    public void Begin(Phase phase)
    {
        lock (_lock)
        {
            EndPhase();
            _phase = phase;
            _clock.Restart();
        }
    }

    public void Reading(long done, long total) => Report(Phase.Reading, (double)done / Math.Max(1, total), $"reading the input blocks ({done:N0} of {total:N0})");

    public void Computing(double fraction) => Report(Phase.Computing, fraction, computing);

    public void Writing(long done, long total) => Report(Phase.Writing, (double)done / Math.Max(1, total), $"{writing} ({done:N0} of {total:N0})");

    /// <summary>The pass is complete: exactly (pass + 1) ÷ passes.</summary>
    public void EndPass()
    {
        lock (_lock)
        {
            Publish((_pass + 1.0) / passes, null);
        }
    }

    private void Report(Phase phase, double fraction, string what)
    {
        lock (_lock)
        {
            double within = 0;
            for (int i = 0; i < (int)phase; i++)
            {
                within += _share[i];
            }

            within += _share[(int)phase] * Math.Clamp(fraction, 0, 1);
            Publish((_pass + within) / passes, passes > 1 ? $"pass {_pass + 1} of {passes}: {what}" : what);
        }
    }

    private void Publish(double value, string? detail)
    {
        _last = Math.Max(_last, Math.Min(1, value));
        progress.ReportDetail(_last, detail);
    }

    private void EndPhase() => _measured[(int)_phase] += _clock.Elapsed.TotalSeconds;
}
