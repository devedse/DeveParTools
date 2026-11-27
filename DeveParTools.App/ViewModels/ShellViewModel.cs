using CommunityToolkit.Mvvm.ComponentModel;
using DeveParTools.App.Services;

namespace DeveParTools.App.ViewModels;

public enum AppPage
{
    Verify,
    Create,
    Benchmark,
    Settings,
}

/// <summary>One entry of the navigation pane.</summary>
/// <param name="Page">The page it opens; null for an entry that only shows something (the version).</param>
/// <param name="IconKey">Resource key of its icon (Themes/Icons.axaml).</param>
public sealed record NavigationItem(AppPage? Page, string Title, string IconKey)
{
    /// <summary>Whether clicking it selects it (false for the version entry).</summary>
    public bool Selectable => Page != null;
}

/// <summary>The window's view model: navigation between pages that share one settings object.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    /// <summary>The pages at the top of the navigation pane.</summary>
    public IReadOnlyList<NavigationItem> MenuPages { get; } =
    [
        new(AppPage.Verify, "Verify & repair", "IconSafe"),
        new(AppPage.Create, "Create", "IconAdd"),
        new(AppPage.Benchmark, "Benchmark", "IconActivity"),
    ];

    /// <summary>The bottom of the navigation pane: the version (not clickable), then Settings.</summary>
    public IReadOnlyList<NavigationItem> FooterPages { get; } =
    [
        new(null, "Version " + AppInfo.Version, "IconInfo"),
        new(AppPage.Settings, "Settings", "IconSettings"),
    ];

    /// <summary>The window title, with the version: "DeveParTools 1.0.33".</summary>
    public string Title => AppInfo.Title;

    /// <summary>The navigation pane's selection: always the entry of <see cref="Page"/>.</summary>
    public NavigationItem SelectedNavigation
    {
        get => MenuPages.Concat(FooterPages).First(p => p.Page == Page);
        set
        {
            if (value?.Page is AppPage page)
            {
                Page = page;
            }
        }
    }

    /// <param name="settings">The settings (default: the user's; the UI checks pass their own).</param>
    public ShellViewModel(SettingsViewModel? settings = null)
    {
        Settings = settings ?? new SettingsViewModel();
        Verify = new VerifyViewModel(Settings);
        Create = new CreateViewModel(Settings);
        Benchmark = new BenchmarkViewModel();
        _currentPage = Verify;
    }

    public SettingsViewModel Settings { get; }

    /// <summary>The update banner and window (owned by Settings, which has the "check automatically" switch).</summary>
    public UpdateViewModel Updates => Settings.Updates;

    public VerifyViewModel Verify { get; }

    public CreateViewModel Create { get; }

    public BenchmarkViewModel Benchmark { get; }

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private AppPage _page;

    partial void OnPageChanged(AppPage value)
    {
        CurrentPage = value switch
        {
            AppPage.Create => Create,
            AppPage.Benchmark => Benchmark,
            AppPage.Settings => Settings,
            _ => Verify,
        };
        OnPropertyChanged(nameof(SelectedNavigation));
    }

    public void SetDialogs(IViewServices dialogs)
    {
        Verify.Dialogs = dialogs;
        Create.Dialogs = dialogs;
        Settings.Dialogs = dialogs;
        Settings.Updates.Dialogs = dialogs;
    }
}
