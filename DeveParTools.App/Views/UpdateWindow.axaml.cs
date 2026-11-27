using Avalonia.Controls;
using Avalonia.Interactivity;
using DeveParTools.App.ViewModels;

namespace DeveParTools.App.Views;

/// <summary>The update window: what the update does, then the download, extraction and installation progress.</summary>
public sealed partial class UpdateWindow : Window
{
    public UpdateWindow() => InitializeComponent();

    private void OnLater(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Closing the window during a download cancels it (nothing has been changed yet at that point).</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        (DataContext as UpdateViewModel)?.CancelIfRunning();
        base.OnClosing(e);
    }
}
