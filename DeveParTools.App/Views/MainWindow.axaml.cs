using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DeveParTools.App.ViewModels;
using FluentAvalonia.UI.Controls;

namespace DeveParTools.App.Views;

/// <summary>The shell window: navigation selection, Mica backdrop and the native file pickers.</summary>
public sealed partial class MainWindow : Window, IFileDialogs
{
    public MainWindow()
    {
        InitializeComponent();
        Navigation.SelectionChanged += (_, e) =>
        {
            if (DataContext is ShellViewModel shell && e.SelectedItem is FANavigationViewItem { Tag: string tag } && Enum.TryParse<AppPage>(tag, out var page))
            {
                shell.Page = page;
            }
        };
        // Select the page the view model starts on (Verify, or Create when started with --create).
        Opened += (_, _) =>
        {
            var items = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<FANavigationViewItem>().ToList();
            var tag = (DataContext as ShellViewModel)?.Page.ToString();
            Navigation.SelectedItem = items.FirstOrDefault(i => (string?)i.Tag == tag) ?? items.FirstOrDefault();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ShellViewModel shell)
        {
            shell.SetDialogs(this);
            // Keep the navigation selection in sync when the page is changed from code (e.g. opening a file).
            shell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShellViewModel.Page))
                {
                    var tag = shell.Page.ToString();
                    var item = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<FANavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
                    if (item != null && !ReferenceEquals(Navigation.SelectedItem, item))
                    {
                        Navigation.SelectedItem = item;
                    }
                }
            };
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty)
        {
            if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
            {
                Background = Brushes.Transparent;
            }
            else
            {
                Bind(BackgroundProperty, this.GetResourceObservable("SolidBackgroundFillColorBaseBrush"));
            }
        }
    }

    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool multiple, IReadOnlyList<string>? patterns = null)
    {
        var options = new FilePickerOpenOptions { Title = title, AllowMultiple = multiple };
        if (patterns != null)
        {
            options.FileTypeFilter = [new FilePickerFileType("PAR files") { Patterns = patterns }, FilePickerFileTypes.All];
        }

        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close,
        };
        return await dialog.ShowAsync(this) == FAContentDialogResult.Primary;
    }

    public async Task LaunchAsync(Uri uri) => await Launcher.LaunchUriAsync(uri);

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, DefaultExtension = extension });
        return file?.TryGetLocalPath();
    }
}
