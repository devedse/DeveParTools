using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeveParTools.App.ViewModels;
using DeveParTools.App.Views;

namespace DeveParTools.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // FluentAvalonia's system-theme detection shells out to gsettings on Linux (missing on many systems),
            // so it is only enabled on Windows and macOS; elsewhere Avalonia's own detection is used.
            if ((OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && Styles.OfType<FluentAvalonia.Styling.FluentAvaloniaTheme>().FirstOrDefault() is { } fluent)
            {
                fluent.PreferSystemTheme = true;
            }

            var shell = new ShellViewModel();
            shell.Settings.ApplyTheme();
            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;
            if (Program.OpenFile is { } file)
            {
                window.Opened += async (_, _) => await shell.Verify.OpenAsync(file);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
