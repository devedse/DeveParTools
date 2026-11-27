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
        Arguments = AppArguments.Parse(args);
        if (Arguments.CreatePaths.Count > 0)
        {
            Collector = CreateRequestCollector.StartOrForward(CreateRequestCollector.DefaultName, Arguments.CreatePaths, TimeSpan.FromSeconds(3));
            if (Collector == null)
            {
                return; // handed to the window that this multi-selection already opened
            }
        }

        RepairMovedContextMenu();
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
    /// When the Explorer entries point to an executable that no longer exists (the app was moved or updated to
    /// another folder), point them to this one.
    /// </summary>
    private static void RepairMovedContextMenu()
    {
        if (!OperatingSystem.IsWindows() || ExplorerContextMenu.CurrentExecutable is not { } self)
        {
            return;
        }

        try
        {
            var menu = new ExplorerContextMenu();
            if (menu.RegisteredExecutable() is { } registered && !File.Exists(registered))
            {
                menu.Install(self);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
