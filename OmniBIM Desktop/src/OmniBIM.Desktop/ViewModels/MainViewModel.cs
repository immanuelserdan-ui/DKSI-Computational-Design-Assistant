using OmniBIM.Desktop.Services;

namespace OmniBIM.Desktop.ViewModels;

/// <summary>
/// Owns the repository and file watcher for the app's lifetime and re-runs aggregation
/// whenever the watched folders change. One instance, held by App/MainWindow.
/// </summary>
public sealed class MainViewModel : IDisposable
{
    private readonly TimeTrackingRepository _repository = new();
    private readonly TimeTrackingWatcher _watcher;

    public TimeManagementViewModel TimeManagement { get; } = new();
    public ProjectStatusViewModel ProjectStatus { get; } = new();

    /// <summary>
    /// Null when no shared folder is configured anywhere (this app's own settings, or the
    /// add-in's) - the UI should show a "local data only" notice rather than a silently
    /// empty team dashboard, since that's indistinguishable from "the team did no work".
    /// </summary>
    public string? SharedFolder => _repository.SharedFolder is { Length: > 0 } f ? f : null;

    public MainViewModel()
    {
        _watcher = new TimeTrackingWatcher(_repository.ActiveFolders());
        _watcher.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        TimeManagement.Load(_repository, DateTime.Now);
        ProjectStatus.Load(_repository);
    }

    public void Dispose() => _watcher.Dispose();
}
