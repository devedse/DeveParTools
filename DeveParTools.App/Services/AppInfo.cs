namespace DeveParTools.App.Services;

/// <summary>The app's name and version (the build sets the version: 1.0.&lt;build number&gt; in CI, 1.0.0 locally).</summary>
public static class AppInfo
{
    public const string Name = "DeveParTools";

    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>The window title, e.g. "DeveParTools 1.0.33".</summary>
    public static string Title => $"{Name} {Version}";
}
