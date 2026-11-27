using System.CommandLine;
using System.Diagnostics;
using DeveParTools.Updates;

namespace DeveParTools.Cli;

/// <summary>
/// "devepar update" (and "update --check"), and the one-line notice after other commands when a newer release exists.
/// </summary>
/// <remarks>
/// <para>The notice: while a command runs, GitHub is asked for the latest release in the background, at most once a day
/// (the answer is cached, see <see cref="UpdateCheckCache"/>). When the command is done and a newer version is known,
/// two lines go to standard error. A command never waits more than <see cref="NoticeBudget"/> for GitHub, counted from
/// its start, so a slow network only means no notice.</para>
/// <para>No check happens when the output is for a program rather than a person (standard error redirected,
/// <c>--json</c>), with <c>--quiet</c>, in CI (the CI variable is set), for development builds, or when
/// DEVEPAR_NO_UPDATE_CHECK is set to anything but 0.</para>
/// </remarks>
public static partial class CliApp
{
    /// <summary>The longest a command waits for the background update check, from its start.</summary>
    internal static readonly TimeSpan NoticeBudget = TimeSpan.FromSeconds(2.5);

    /// <summary>The running version (1.0.&lt;build&gt; in releases, 1.0.0 locally).</summary>
    internal static Version Version { get; set; } = Updater.Normalize(typeof(CliApp).Assembly.GetName().Version ?? new Version(1, 0, 0));

    /// <summary>Makes the updater; tests replace it with one that talks to a fake GitHub.</summary>
    internal static Func<Updater> NewUpdater { get; set; } = () => new Updater("devepar", Version);

    /// <summary>The running program (Environment.ProcessPath); tests point it to a scratch file.</summary>
    internal static Func<string?> ProcessPath { get; set; } = () => Environment.ProcessPath;

    /// <summary>Where the answer of the last check is kept.</summary>
    internal static Func<UpdateCheckCache> Cache { get; set; } = () => new UpdateCheckCache(UpdateCheckCache.DefaultPath);

    /// <summary>
    /// The program's entry point: cleans up after an earlier update, runs the command, and prints the update notice
    /// when one is due.
    /// </summary>
    public static int RunProgram(string[] args)
    {
        var target = Updater.FindTarget(ProcessPath(), Version, "devepar");
        if (target.Executable != null)
        {
            Updater.RemoveLeftovers(target.Executable, "devepar");
        }

        var started = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource(NoticeBudget);
        Task<Version?>? latest = null;
        if (target.Executable != null && WantsUpdateNotice(Build(TextWriter.Null, TextWriter.Null).Parse(args), Console.IsErrorRedirected, Environment.GetEnvironmentVariable))
        {
            latest = Task.Run(async () =>
            {
                using var updater = NewUpdater();
                return await updater.LatestForAutomaticCheckAsync(Cache(), DateTimeOffset.UtcNow, stop.Token);
            });
        }

        int code = Run(args);
        var wait = NoticeBudget - started.Elapsed;
        if (latest != null && WaitQuietly(latest, wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(50)) && latest.Result is { } version && version > Version)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(Notice(Version, version));
        }

        return code;
    }

    /// <summary>Waits for a task; false when it is not done in time or failed (a failed check only means no notice).</summary>
    internal static bool WaitQuietly(Task task, TimeSpan wait)
    {
        try
        {
            return task.Wait(wait) && task.IsCompletedSuccessfully;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    /// <summary>The notice after a command: "devepar 1.0.41 is available (you have 1.0.40)…".</summary>
    internal static string Notice(Version current, Version latest)
        => $"devepar {latest} is available (you have {current}). Run \"devepar update\" to install it.";

    /// <summary>Whether this command line should check for a newer version in the background (see the class remarks).</summary>
    internal static bool WantsUpdateNotice(ParseResult parse, bool errorRedirected, Func<string, string?> environment)
    {
        if (errorRedirected || parse.Errors.Count > 0 || parse.CommandResult.Command.Name == "update")
        {
            return false;
        }

        if (parse.GetValue<bool>("--json") || parse.GetValue<bool>("--quiet"))
        {
            return false;
        }

        bool Set(string name) => environment(name) is { Length: > 0 } v && v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
        return !Set("DEVEPAR_NO_UPDATE_CHECK") && !Set("CI");
    }

    private static Command BuildUpdate(Func<ParseResult, Common> context)
    {
        var checkOnly = new Option<bool>("--check") { Description = "Only say whether a newer version exists; change nothing." };
        var update = new Command("update", "Update devepar to the latest release from GitHub (replaces this program file).");
        update.Options.Add(checkOnly);
        update.SetAction(p => Guard(context(p), c => Update(c, p.GetValue(checkOnly))));
        return update;
    }

    /// <summary>Checks GitHub, then (unless only checking) downloads and installs the new version over this program.</summary>
    internal static int Update(Common c, bool checkOnly)
    {
        using var updater = NewUpdater();
        string? executable = null;
        if (!checkOnly)
        {
            var target = Updater.FindTarget(ProcessPath(), updater.Current, "devepar");
            executable = target.Executable ?? throw new UpdateException(target.Reason!);
        }

        c.Stage("Checking for updates");
        var check = updater.CheckAsync(Cache()).GetAwaiter().GetResult();
        string current = check.Current.ToString(), latest = check.Latest.Version.ToString();
        var values = new Dictionary<string, object?>
        {
            ["current"] = current,
            ["latest"] = latest,
            ["updateAvailable"] = check.UpdateAvailable,
            ["page"] = check.Latest.Page.ToString(),
        };
        if (!check.UpdateAvailable)
        {
            c.Result("update", values, $"devepar {current} is the latest version.");
            return ExitCodes.Success;
        }

        if (checkOnly)
        {
            c.Result("update", values, $"{Notice(check.Current, check.Latest.Version)}\nWhat's new: {check.Latest.Page}");
            return ExitCodes.Success;
        }

        var asset = check.Asset ?? throw new UpdateException($"Release {latest} has no devepar for this system ({updater.RuntimeId ?? "unknown"}). See {check.Latest.Page}.");
        UpdateStage? stage = null;
        var progress = new SyncProgress<UpdateProgress>(p =>
        {
            if (p.Stage != stage)
            {
                stage = p.Stage;
                c.Stage(p.Stage switch
                {
                    UpdateStage.Downloading => $"Downloading {asset.Name} ({Megabytes(p.Total)})",
                    UpdateStage.Extracting => "Extracting",
                    _ => "Installing",
                });
            }

            c.Progress(p.Fraction, p.Stage == UpdateStage.Downloading ? $"{Megabytes(p.Done)} of {Megabytes(p.Total)}" : null);
        });
        updater.InstallAsync(asset, executable!, progress).GetAwaiter().GetResult();
        values["path"] = executable;
        c.Result("updated", values, $"Updated {executable} from {current} to {latest}.");
        return ExitCodes.Success;
    }

    internal static string Megabytes(long bytes) => $"{bytes / 1e6:0.0} MB";

    /// <summary>An IProgress that reports on the calling thread, in order (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
