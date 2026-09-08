using System.Collections.ObjectModel;
using OmniBIM.Desktop.Models;

namespace OmniBIM.Desktop.ViewModels;

/// <summary>
/// The app shell: sidebar navigation plus the one MainViewModel instance (repository +
/// watcher + Time Management + Project Status) that every data-backed page shares - Home,
/// the full Time Management page and the full Project Status page all read the SAME live
/// view models rather than each holding their own copy that could drift out of sync.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IDisposable
{
    public MainViewModel Dashboard { get; } = new();

    /// <summary>The Home page's AI chat panel. Depends on Dashboard, so it's built after it.</summary>
    public ChatViewModel Chat { get; }

    /// <summary>"What's happening right now", read directly from Revit's live status pipe - independent of Dashboard's CSV-driven refresh.</summary>
    public LiveStatusViewModel LiveStatus { get; } = new();

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    private AppPage _currentPage = AppPage.Home;
    public AppPage CurrentPage { get => _currentPage; private set => SetField(ref _currentPage, value); }

    private const string NoDataSourceYet =
        "Nothing wired up yet - this app currently only has a live data source for time-tracking " +
        "(the Revit add-in's CSV ledgers) and project status. This section will read real data once one exists for it.";

    public PlaceholderPageViewModel Projects { get; } = new("Projects", NoDataSourceYet);
    public PlaceholderPageViewModel ScanToBim { get; } = new("Scan to BIM", NoDataSourceYet);
    public PlaceholderPageViewModel Workflow { get; } = new("Workflow", NoDataSourceYet);
    public PlaceholderPageViewModel Teams { get; } = new("Teams & Collaboration", NoDataSourceYet);
    public PlaceholderPageViewModel Reports { get; } = new("Reports", NoDataSourceYet);

    /// <summary>Real, not a placeholder - a hand-curated snapshot of real articles. See TrendNewsViewModel.</summary>
    public TrendNewsViewModel TrendNews { get; } = new();

    /// <summary>Real, not a placeholder - see SettingsViewModel.</summary>
    public SettingsViewModel Settings { get; } = new();

    public ShellViewModel()
    {
        Chat = new ChatViewModel(Dashboard);

        NavItems =
        [
            new NavItemViewModel("Home", AppPage.Home, Navigate),
            new NavItemViewModel("Projects", AppPage.Projects, Navigate),
            new NavItemViewModel("Scan to BIM", AppPage.ScanToBim, Navigate),
            new NavItemViewModel("Workflow", AppPage.Workflow, Navigate),
            new NavItemViewModel("Time Management", AppPage.TimeManagement, Navigate),
            new NavItemViewModel("Project Status", AppPage.ProjectStatus, Navigate),
            new NavItemViewModel("Trend News", AppPage.TrendNews, Navigate),
            new NavItemViewModel("Teams & Collaboration", AppPage.Teams, Navigate),
            new NavItemViewModel("Reports", AppPage.Reports, Navigate),
            new NavItemViewModel("Settings", AppPage.Settings, Navigate),
        ];

        UpdateSelection();
    }

    private void Navigate(AppPage page)
    {
        CurrentPage = page;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        foreach (var item in NavItems) item.IsSelected = item.Page == CurrentPage;
    }

    public void Dispose()
    {
        Dashboard.Dispose();
        LiveStatus.Dispose();
    }
}
