using Avalonia;

namespace DeveParTools.App;

internal static class Program
{
    /// <summary>A .par/.par2/.par3 file to open (from the command line or a file association).</summary>
    internal static string? OpenFile { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        OpenFile = args.FirstOrDefault(a => File.Exists(a));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
