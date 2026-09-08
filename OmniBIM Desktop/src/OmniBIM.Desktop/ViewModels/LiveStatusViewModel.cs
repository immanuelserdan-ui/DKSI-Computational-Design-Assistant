using System.Windows.Threading;
using OmniBIM.Desktop.Services;

namespace OmniBIM.Desktop.ViewModels;

/// <summary>
/// Polls LiveStatusClient once a second for "what's happening right now" - a direct read of
/// the Revit add-in's in-memory state, no CSV round-trip. Purely additive: if nothing answers
/// (Revit not running, add-in not loaded, tracking disabled), IsLive stays false and every
/// other page keeps working exactly as before, off the CSV ledger.
/// </summary>
public sealed class LiveStatusViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;

    private bool _isLive;
    public bool IsLive { get => _isLive; private set => SetField(ref _isLive, value); }

    private string _projectName = string.Empty;
    public string ProjectName { get => _projectName; private set => SetField(ref _projectName, value); }

    private string _viewName = string.Empty;
    public string ViewName { get => _viewName; private set => SetField(ref _viewName, value); }

    private string _taskPhase = string.Empty;
    public string TaskPhase { get => _taskPhase; private set => SetField(ref _taskPhase, value); }

    private bool _isIdle;
    public bool IsIdle { get => _isIdle; private set => SetField(ref _isIdle, value); }

    private string _elapsedLabel = string.Empty;
    public string ElapsedLabel { get => _elapsedLabel; private set => SetField(ref _elapsedLabel, value); }

    public LiveStatusViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
    }

    private async Task Poll()
    {
        try
        {
            var status = await LiveStatusClient.TryGetStatusAsync();

            if (status is null || !status.HasOpenSegment)
            {
                IsLive = false;
                return;
            }

            IsLive = true;
            ProjectName = status.ProjectName;
            ViewName = status.ViewName;
            TaskPhase = status.TaskPhase;
            IsIdle = status.IsIdle;

            var elapsed = TimeSpan.FromSeconds(status.ElapsedSeconds);
            ElapsedLabel = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
                : $"{elapsed.Minutes}m {elapsed.Seconds}s";
        }
        catch
        {
            // The one-second timer must survive any single bad tick - the next one is a
            // second away either way.
            IsLive = false;
        }
    }

    public void Dispose() => _timer.Stop();
}
