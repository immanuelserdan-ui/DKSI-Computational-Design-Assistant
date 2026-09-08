using System.IO;
using System.Windows.Threading;

namespace OmniBIM.Desktop.Services;

/// <summary>
/// Watches the local and (when configured) shared time-tracking folders for changes and
/// raises <see cref="Changed"/>, debounced, so the dashboard can re-read and re-aggregate.
///
/// DEBOUNCED ON PURPOSE. A single Revit segment append is one FileSystemWatcher event; a busy
/// office with a dozen people modelling against a shared folder can produce a burst of them
/// within the same second, and re-parsing the whole folder per event would make the watcher
/// itself the performance problem the brief asks to avoid. Coalescing into one refresh per
/// quiet period keeps the read cost proportional to how often data actually changes, not to
/// how many people are typing.
///
/// A missing or unreachable shared folder (mapped drive not connected, VPN down) is not an
/// error here - watching simply does not start for that folder, and the repository's own read
/// already tolerates a folder that has disappeared since the last refresh.
/// </summary>
public sealed class TimeTrackingWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DispatcherTimer _debounce;

    public event Action? Changed;

    public TimeTrackingWatcher(IEnumerable<string> folders, TimeSpan? debounceInterval = null)
    {
        _debounce = new DispatcherTimer
        {
            Interval = debounceInterval ?? TimeSpan.FromSeconds(2),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;

            var watcher = new FileSystemWatcher(folder, "*.csv")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Renamed += OnFileEvent;

            // project-status.json lives in the same folder but does not match "*.csv" -
            // a second watcher instance keeps the filter simple rather than widening it to
            // "*" and having to ignore the add-in's own pending-writes.json churn.
            var statusWatcher = new FileSystemWatcher(folder, "project-status.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            statusWatcher.Changed += OnFileEvent;

            _watchers.Add(watcher);
            _watchers.Add(statusWatcher);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher events arrive on a thread-pool thread; DispatcherTimer must be
        // touched on the UI thread it was created on.
        _debounce.Dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        _debounce.Stop();
    }
}
