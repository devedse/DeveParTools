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

    public static (int ExitCode, string Output) Run(string exe, string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
