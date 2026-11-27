using System.Text;
using System.Text.Json;
using DeveParTools.Core.Operations;

namespace DeveParTools.Cli;

/// <summary>
/// Per-invocation output: a progress line (text mode) or NDJSON events (--json), plus the optional block map.
/// </summary>
internal sealed class Common : IParOperationObserver, IDisposable
{
    private readonly bool _json;
    private readonly bool _map;
    private readonly bool _quiet;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly object _lock = new();
    private string _stage = string.Empty;
    private DateTime _lastProgress = DateTime.MinValue;
    private bool _progressLine;

    public Common(bool json, bool map, bool quiet, int? threads, int? memoryMiB, TextWriter output, TextWriter error)
    {
        _json = json;
        _map = map;
        _quiet = quiet;
        _out = output;
        _err = error;
        Context = new OperationContext
        {
            Observer = this,
            Threads = threads is > 0 ? threads.Value : Environment.ProcessorCount,
            MemoryBudget = memoryMiB is > 0 ? memoryMiB.Value * (1L << 20) : OperationContext.Default.MemoryBudget,
        };
    }

    public OperationContext Context { get; }

    public void Stage(string name)
    {
        lock (_lock)
        {
            _stage = name;
            if (_json)
            {
                WriteJson(w =>
                {
                    w.WriteString("type", "stage");
                    w.WriteString("name", name);
                });
            }
            else if (!_quiet)
            {
                EndProgressLine();
                _out.WriteLine($"{name}...");
            }
        }
    }

    public void Progress(double fraction, string? detail = null)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastProgress).TotalMilliseconds < 200 && fraction < 1)
            {
                return;
            }

            _lastProgress = now;
            if (_json)
            {
                WriteJson(w =>
                {
                    w.WriteString("type", "progress");
                    w.WriteString("stage", _stage);
                    w.WriteNumber("fraction", Math.Round(fraction, 4));
                    if (detail != null)
                    {
                        w.WriteString("detail", detail);
                    }
                });
            }
            else if (!_quiet && !Console.IsOutputRedirected && ReferenceEquals(_out, Console.Out))
            {
                int width = 30;
                int filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
                _out.Write($"\r  [{new string('#', filled)}{new string('.', width - filled)}] {fraction * 100,5:0.0}% {detail}".PadRight(Math.Min(120, SafeWidth())));
                _progressLine = true;
            }
        }
    }

    public void Log(ParLogLevel level, string message)
    {
        lock (_lock)
        {
            if (_json)
            {
                WriteJson(w =>
                {
                    w.WriteString("type", "log");
                    w.WriteString("level", level.ToString().ToLowerInvariant());
                    w.WriteString("message", message);
                });
            }
            else if (!_quiet || level >= ParLogLevel.Warning)
            {
                EndProgressLine();
                (level >= ParLogLevel.Warning ? _err : _out).WriteLine(level >= ParLogLevel.Warning ? $"{level}: {message}" : message);
            }
        }
    }

    public void Error(string kind, string message)
    {
        lock (_lock)
        {
            if (_json)
            {
                WriteJson(w =>
                {
                    w.WriteString("type", "error");
                    w.WriteString("kind", kind);
                    w.WriteString("message", message);
                });
            }
            else
            {
                EndProgressLine();
                _err.WriteLine($"Error: {message}");
            }
        }
    }

    /// <summary>The final result: a JSON object or the given text.</summary>
    public void Result(string type, IReadOnlyDictionary<string, object?> values, string text)
    {
        lock (_lock)
        {
            if (_json)
            {
                WriteJson(w =>
                {
                    w.WriteString("type", type);
                    foreach (var (k, v) in values)
                    {
                        w.WritePropertyName(k);
                        WriteValue(w, v);
                    }
                });
            }
            else
            {
                EndProgressLine();
                _out.WriteLine(text);
            }
        }
    }

    public void PrintReport(ParVerifyReport report, bool listOnly = false)
    {
        ShowMap();
        var files = report.Files.Select(f => (object?)new Dictionary<string, object?>
        {
            ["name"] = f.Name,
            ["size"] = f.Size,
            ["status"] = f.Status.ToString().ToLowerInvariant(),
            ["foundAs"] = f.FoundAs,
        }).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{report.Format} set: {report.Files.Count} file(s), {report.BlockCount} block(s){(report.BlockSize > 0 ? $" of {report.BlockSize} bytes" : string.Empty)}.");
        foreach (var c in report.Comments)
        {
            sb.AppendLine($"Comment: {c}");
        }

        foreach (var f in report.Files)
        {
            sb.AppendLine($"  {StatusText(f.Status),-9} {f.Size,14:N0}  {f.Name}{(f.FoundAs != null ? $"  (found as {f.FoundAs})" : string.Empty)}");
        }

        if (!listOnly)
        {
            sb.Append(report.AllFilesComplete
                ? "All files are correct, repair is not required."
                : report.RepairPossible
                    ? $"Repair is possible: {report.MissingBlocks} block(s) missing, {report.RecoveryBlocksAvailable} recovery block(s) available."
                    : $"Repair is NOT possible: {report.MissingBlocks} block(s) missing, only {report.RecoveryBlocksAvailable} recovery block(s) available.");
        }

        Result("verified", new Dictionary<string, object?>
        {
            ["format"] = report.Format.ToString(),
            ["blockSize"] = report.BlockSize,
            ["blockCount"] = report.BlockCount,
            ["missingBlocks"] = report.MissingBlocks,
            ["recoveryBlocksAvailable"] = report.RecoveryBlocksAvailable,
            ["allFilesComplete"] = report.AllFilesComplete,
            ["repairPossible"] = report.RepairPossible,
            ["files"] = files,
        }, sb.ToString().TrimEnd());
    }

    public void ShowMap()
    {
        if (!_map || Context.Blocks == null)
        {
            return;
        }

        lock (_lock)
        {
            EndProgressLine();
            if (_json)
            {
                var states = new byte[Context.Blocks.InputBlockCount];
                Context.Blocks.CopyStates(states);
                WriteJson(w =>
                {
                    w.WriteString("type", "blockmap");
                    w.WriteString("states", Convert.ToBase64String(states));
                });
            }
            else
            {
                _out.Write(TerminalBlockMap.Render(Context.Blocks, Math.Min(100, SafeWidth() - 2), useColor: !Console.IsOutputRedirected && ReferenceEquals(_out, Console.Out)));
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            EndProgressLine();
            _out.Flush();
        }
    }

    private static string StatusText(ParFileStatus s) => s switch
    {
        ParFileStatus.Complete => "OK",
        ParFileStatus.Damaged => "DAMAGED",
        ParFileStatus.Missing => "MISSING",
        _ => "UNKNOWN",
    };

    private void EndProgressLine()
    {
        if (_progressLine)
        {
            _out.WriteLine();
            _progressLine = false;
        }
    }

    private static int SafeWidth()
    {
        try
        {
            return Console.IsOutputRedirected ? 100 : Math.Max(40, Console.WindowWidth);
        }
        catch (IOException)
        {
            return 100;
        }
    }

    private void WriteJson(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        _out.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case IReadOnlyDictionary<string, object?> dict:
                w.WriteStartObject();
                foreach (var (k, val) in dict)
                {
                    w.WritePropertyName(k);
                    WriteValue(w, val);
                }

                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable e:
                w.WriteStartArray();
                foreach (var item in e)
                {
                    WriteValue(w, item);
                }

                w.WriteEndArray();
                break;
            default:
                w.WriteStringValue(v.ToString());
                break;
        }
    }
}
