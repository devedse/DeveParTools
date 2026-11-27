using Avalonia;
using DeveParTools.App.Services;

namespace DeveParTools.App;

internal static class Program
{
    /// <summary>What the command line asked for: a set to verify, or inputs for a new set.</summary>
    internal static AppArguments Arguments { get; private set; } = new([], null);

    /// <summary>For <c>--create</c>: receives the other items of the same Explorer multi-selection.</summary>
    internal static CreateRequestCollector? Collector { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args is [AppArguments.FinishUpdateSwitch, var program, var processId])
        {
            FinishUpdate(program, int.TryParse(processId, out var id) ? id : null);
            return;
        }

        bool fromDrop = OperatingSystem.IsWindows() && ExplorerDropTarget.IsServerStart(args);
        if (fromDrop)
        {
            // Started by COM for Explorer's "Create recovery set": receive every selected item at once.
            if (!OperatingSystem.IsWindows() || ExplorerDropTarget.ReceiveDrop(TimeSpan.FromSeconds(30)) is not { Count: > 0 } dropped)
            {
                return;
            }

            args = [AppArguments.CreateSwitch, .. dropped];
        }

        Arguments = AppArguments.Parse(args);
        if (Arguments.CreatePaths.Count > 0 && !fromDrop)
        {
            Collector = CreateRequestCollector.StartOrForward(CreateRequestCollector.DefaultName, Arguments.CreatePaths, TimeSpan.FromSeconds(3));
            if (Collector == null)
            {
                return; // handed to the window that this multi-selection already opened
            }
        }

        RepairMovedContextMenu();
        RemoveUpdateLeftovers();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Collector?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>
    /// The last step of an update, run by the new version from the folder it was extracted to: once the old app has
    /// exited, copy this program over it and start it. If that fails, the old program is back in place and is started.
    /// </summary>
    private static void FinishUpdate(string program, int? oldProcessId)
    {
        try
        {
            DeveParTools.Updates.Updater.CompleteStaged(Environment.ProcessPath!, program, oldProcessId, TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not replaced; the old version starts again (and offers the update again).
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false });
    }

    /// <summary>
    /// Deletes what an update left next to the program: on Windows the previous version (deveparui.exe.old) cannot be
    /// deleted while it runs, and right after an update it may still be closing, so this tries for a few seconds.
    /// </summary>
    private static void RemoveUpdateLeftovers()
    {
        if (DeveParTools.Updates.Updater.FindTarget(Environment.ProcessPath, Version.Parse(AppInfo.Version), "deveparui").Executable is not { } self)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                DeveParTools.Updates.Updater.RemoveLeftovers(self, "deveparui");
                if (!File.Exists(self + ".old") && !Directory.Exists(DeveParTools.Updates.Updater.StagingPath(self, "deveparui")))
                {
                    return;
                }

                await Task.Delay(500);
            }
        });
    }

    /// <summary>
    /// When the Explorer entries or the file associations point to an executable that no longer exists (the app was
    /// moved, renamed or updated to another folder), point them to this one.
    /// </summary>
    private static void RepairMovedContextMenu()
    {
        if (ExplorerContextMenu.CurrentExecutable is not { } self)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Re-point entries whose program is gone, and add the drop target to entries an older version made.
                var menu = new ExplorerContextMenu();
                if (menu.RegisteredExecutable() is { } registered && (!File.Exists(registered) || (string.Equals(registered, self, StringComparison.OrdinalIgnoreCase) && !menu.HasDropTarget())))
                {
                    menu.Install(self);
                }
            }

            if (FileAssociations.ForThisPlatform() is { } associations && associations.Read() is { Installed: true, Executable: { } old } && !File.Exists(old))
            {
                associations.Install(self);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
