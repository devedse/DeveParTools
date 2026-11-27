using System.Diagnostics;

namespace DeveParTools.InteropTests;

/// <summary>
/// Locates reference implementations used as test oracles. Paths come from environment variables
/// (DEVEPARTOOLS_PAR2CMDLINE, DEVEPARTOOLS_PAR2TURBO, DEVEPARTOOLS_PAR3CMDLINE) or the PATH.
/// </summary>
internal static class ExternalTool
{
    public static string? Find(string envVar, params string[] namesOnPath)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (var name in namesOnPath)
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p))
                {
                    return p;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Runs a reference tool. Its stdin is closed right away, because some tools ask questions on the console and would
    /// otherwise wait forever. A tool that runs longer than <paramref name="timeoutSeconds"/> is killed and the test fails.
    /// </summary>
    public static (int ExitCode, string Output) Run(string exe, string workingDir, params string[] args) => Run(exe, workingDir, 120, args);

    public static (int ExitCode, string Output) Run(string exe, string workingDir, int timeoutSeconds, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit();
            throw new TimeoutException($"{Path.GetFileName(exe)} {string.Join(' ', args)} did not finish within {timeoutSeconds} s:\n{stdout.Result}{stderr.Result}");
        }

        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
