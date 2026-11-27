using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
            if (Program.Arguments.CreatePaths.Count > 0)
            {
                shell.Page = AppPage.Create;
                foreach (var path in Program.Arguments.CreatePaths)
                {
                    shell.Create.Add(path);
                }
            }
            else if (Program.Arguments.OpenFile is { } file)
            {
                window.Opened += async (_, _) => await shell.Verify.OpenAsync(file);
            }

            // The rest of an Explorer multi-selection arrives from the other processes it started.
            Program.Collector?.Attach(paths => Dispatcher.UIThread.Post(() =>
            {
                foreach (var path in paths)
                {
                    shell.Create.Add(path);
                }
            }));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
