using CommunityToolkit.Mvvm.ComponentModel;

namespace DeveParTools.App.ViewModels;

public enum AppPage
{
    Verify,
    Create,
    Benchmark,
    Settings,
}

/// <summary>One entry of the navigation pane.</summary>
/// <param name="IconKey">Resource key of its icon (Themes/Icons.axaml).</param>
public sealed record NavigationItem(AppPage Page, string Title, string IconKey);

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

    /// <summary>The pages at the bottom of the navigation pane.</summary>
    public IReadOnlyList<NavigationItem> FooterPages { get; } = [new(AppPage.Settings, "Settings", "IconSettings")];

    /// <summary>The navigation pane's selection: always the entry of <see cref="Page"/>.</summary>
    public NavigationItem SelectedNavigation
    {
        get => MenuPages.Concat(FooterPages).First(p => p.Page == Page);
        set
        {
            if (value != null)
            {
                Page = value.Page;
            }
        }
    }

    public ShellViewModel()
    {
        Settings = new SettingsViewModel();
        Verify = new VerifyViewModel(Settings);
        Create = new CreateViewModel(Settings);
        Benchmark = new BenchmarkViewModel();
        _currentPage = Verify;
    }

    public SettingsViewModel Settings { get; }

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
    }
}
