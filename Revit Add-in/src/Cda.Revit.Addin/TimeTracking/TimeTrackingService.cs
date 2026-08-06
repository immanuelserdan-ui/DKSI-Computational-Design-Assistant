using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.UI;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// Wires the tracker to Revit and owns the session.
///
/// Registered once from <see cref="CdaApplication.OnStartup"/>, after the ribbon, and torn
/// down in OnShutdown. Every failure in here is logged and swallowed: a time tracker that
/// stops the modelling tools loading is worse than no time tracker.
///
/// THE FOUR HOOKS
///
///   ViewActivated    the context signal. Fires on every view change AND on every document
///                    switch, which makes it the one event that answers "what is the user
///                    looking at now?" without polling.
///   DocumentClosing  close the segment while the document is still readable. A moment
///                    later its title and project number are gone.
///   Idling           the clock for <see cref="IdleMonitor"/>, and a safe place to put a
///                    dialog in front of the user — Revit is by definition doing nothing.
///   OnShutdown       the last chance to write the open segment.
/// </summary>
internal static class TimeTrackingService
{
    private static TimeTrackingSettings _settings = new();
    private static IdleMonitor? _monitor;
    private static UIApplication? _uiApp;

    /// <summary>
    /// True while a dialog raised from the Idling handler is on screen.
    ///
    /// Load-bearing. A modal dialog pumps the Windows message loop, so Revit keeps raising
    /// Idling while it is open — without this guard the return prompt stacks copies of
    /// itself on top of the one the user has not answered yet.
    /// </summary>
    private static bool _promptOpen;

    /// <summary>The idle span waiting on a keep/discard/reassign answer.</summary>
    private static (WorkContext Context, DateTime FromUtc, DateTime ToUtc)? _pendingIdle;

    public static TimeTrackingSettings Settings => _settings;

    public static bool IsIdle => _monitor?.IsIdle ?? false;

    public static TimeSpan IdleFor => _monitor?.IdleFor ?? TimeSpan.Zero;

    // ------------------------------------------------------------------ lifecycle

    public static void Register(UIControlledApplication application)
    {
        try
        {
            _settings = TimeTrackingSettings.Load();

            // The Windows login for now. The Autodesk sign-in name is the better answer —
            // it is what matches a licence and a resourcing plan — but it hangs off
            // Application, not ControlledApplication, and OnStartup has no UIApplication to
            // reach it through. EnsureUsername upgrades it the first time one turns up.
            TimeTracker.Configure(_settings, Environment.UserName);

            _monitor = new IdleMonitor(_settings);
            _monitor.WentIdle += OnWentIdle;
            _monitor.CameBack += OnCameBack;

            application.ViewActivated += OnViewActivated;
            application.Idling += OnIdling;
            application.ControlledApplication.DocumentClosing += OnDocumentClosing;

            Log.Info($"Time tracking registered (enabled={_settings.Enabled}, " +
                     $"idle={_settings.IdleThresholdMinutes} min, " +
                     $"shared='{(_settings.SharedFolder.Length == 0 ? "none" : _settings.SharedFolder)}').");
        }
        catch (Exception ex)
        {
            Log.Error("Time tracking could not be registered.", ex);
        }
    }

    public static void Unregister(UIControlledApplication application)
    {
        try
        {
            application.ViewActivated -= OnViewActivated;
            application.Idling -= OnIdling;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;

            // The whole point of a shutdown hook: without it, closing Revit silently loses
            // however long the user spent in the view they were last looking at.
            TimeTracker.Flush("Revit is closing");
        }
        catch (Exception ex)
        {
            Log.Warn($"Time tracking shutdown was untidy: {ex.Message}");
        }
    }

    /// <summary>Persists changed settings and re-points everything that holds a copy.</summary>
    public static void Apply(TimeTrackingSettings settings)
    {
        _settings = settings;
        _settings.Save();

        TimeTracker.Configure(_settings, string.Empty);

        _monitor = new IdleMonitor(_settings);
        _monitor.WentIdle += OnWentIdle;
        _monitor.CameBack += OnCameBack;

        if (!_settings.Enabled)
        {
            TimeTracker.Flush("tracking was switched off");
        }
        else if (_uiApp is not null)
        {
            // Switching tracking back on has to start a segment now. Waiting for the next
            // ViewActivated means a day spent in one plan records nothing, and the feature
            // reads as still off.
            SyncToActiveView(_uiApp);
        }

        Log.Info($"Time tracking settings applied (enabled={_settings.Enabled}, " +
                 $"idle={_settings.IdleThresholdMinutes} min).");
    }

    /// <summary>
    /// Starts timing the view that is already open. Registration happens before any document
    /// exists, so without this the first segment does not begin until the user changes view —
    /// which on a day spent in one plan is never.
    /// </summary>
    public static void SyncToActiveView(UIApplication uiApp)
    {
        _uiApp = uiApp;
        EnsureUsername();

        if (!_settings.Enabled) return;

        var doc = Safe(() => uiApp.ActiveUIDocument?.Document);
        if (doc is null) return;

        var view = Safe(() => uiApp.ActiveUIDocument?.ActiveView);
        TimeTracker.Switch(WorkContext.From(doc, view, _settings));
    }

    // ------------------------------------------------------------------ Revit events

    private static void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        if (!_settings.Enabled) return;

        try
        {
            _uiApp = sender as UIApplication ?? _uiApp;
            EnsureUsername();

            // A failed or cancelled activation means the view the user asked for never
            // opened; timing it would attribute work to a view they never saw.
            if (e.Status != RevitAPIEventStatus.Succeeded || e.CurrentActiveView is null) return;

            // Activating a view is input. Saying so here means opening a model and reading a
            // drawing for six minutes without touching the mouse does not read as idle.
            _monitor?.MarkActive();

            TimeTracker.Switch(WorkContext.From(e.Document, e.CurrentActiveView, _settings));
        }
        catch (Exception ex)
        {
            // This runs inside Revit's view activation. Throwing here can leave the UI in a
            // half-switched state.
            Log.Error("Time tracking: ViewActivated handler failed.", ex);
        }
    }

    private static void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
    {
        if (!_settings.Enabled) return;

        try
        {
            var open = TimeTracker.Open;
            if (open is null) return;

            // Only flush for the document being closed — closing a linked or secondary model
            // must not stop the clock on the one the user is still working in.
            var title = Safe(() => e.Document?.Title) ?? string.Empty;
            if (title.Length > 0 && title != open.FileName) return;

            TimeTracker.Flush($"'{title}' is closing");
        }
        catch (Exception ex)
        {
            Log.Error("Time tracking: DocumentClosing handler failed.", ex);
        }
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        if (!_settings.Enabled) return;

        // Re-entrancy: a TaskDialog or WPF window opened below pumps messages, and Revit
        // raises Idling again while it is up.
        if (_promptOpen) return;

        try
        {
            _uiApp = sender as UIApplication ?? _uiApp;
            _monitor?.Tick();
        }
        catch (Exception ex)
        {
            Log.Error("Time tracking: idle check failed.", ex);
        }
    }

    // ------------------------------------------------------------------ idle handling

    private static void OnWentIdle(DateTime lastActiveUtc)
    {
        var context = TimeTracker.Open;
        if (context is null) return;

        // Closed AT the last input, not at now: the gap between them is the idle threshold,
        // and billing it would defeat the purpose.
        TimeTracker.Pause(lastActiveUtc);

        _pendingIdle = (context, lastActiveUtc, lastActiveUtc);

        Log.Info($"Time tracking: idle since {lastActiveUtc.ToLocalTime():HH:mm:ss} " +
                 $"on '{context.Describe()}'; timer paused.");
    }

    private static void OnCameBack(DateTime fromUtc, DateTime toUtc)
    {
        if (_pendingIdle is null)
        {
            TimeTracker.Resume();
            return;
        }

        var pending = _pendingIdle.Value;

        // A second gap before the first was answered: fold them together rather than
        // queueing two dialogs at the user.
        _pendingIdle = (pending.Context, pending.FromUtc, toUtc);

        if (!_settings.PromptOnReturn)
        {
            Log.Info($"Time tracking: {(toUtc - pending.FromUtc).TotalMinutes:0.0} idle minute(s) discarded.");
            _pendingIdle = null;
            TimeTracker.Resume();
            return;
        }

        Prompt(_pendingIdle.Value);
    }

    /// <summary>
    /// The keep / discard / reassign question, asked once on return.
    ///
    /// Shown straight from the Idling handler. That is a legitimate API context — Revit is
    /// idle by definition — and it is the only way to catch the user at the moment the
    /// question makes sense. Closing the dialog without choosing counts as DISCARD: time
    /// nobody vouched for should not end up on an invoice.
    /// </summary>
    private static void Prompt((WorkContext Context, DateTime FromUtc, DateTime ToUtc) idle)
    {
        var minutes = (idle.ToUtc - idle.FromUtc).TotalMinutes;
        if (minutes <= 0)
        {
            _pendingIdle = null;
            TimeTracker.Resume();
            return;
        }

        _promptOpen = true;

        try
        {
            var dialog = new TaskDialog("DKSI Time Tracking")
            {
                MainInstruction = $"You've been idle for {minutes:0} minute(s).",
                MainContent =
                    $"The timer paused at {idle.FromUtc.ToLocalTime():HH:mm} while you were on " +
                    $"{idle.Context.Describe()}.\n\nWhat should happen to that time?",
                FooterText = BuildInfo.Describe(),
                CommonButtons = TaskDialogCommonButtons.None,
                AllowCancellation = true,
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Keep it",
                $"Book {minutes:0} minute(s) to {idle.Context.ProjectName} as normal project time.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Discard it",
                "The time is not recorded. This is what happens if you close this dialog.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Reassign it…",
                "Log it against another project or as an off-model task — a meeting, a call, a review.");

            var answer = dialog.Show();

            switch (answer)
            {
                case TaskDialogResult.CommandLink1:
                    TimeTracker.KeepIdle(idle.Context, idle.FromUtc, idle.ToUtc, "Away from Revit — kept");
                    Log.Info($"Time tracking: {minutes:0.0} idle minute(s) kept.");
                    break;

                case TaskDialogResult.CommandLink3:
                    Reassign(idle, minutes);
                    break;

                default:
                    Log.Info($"Time tracking: {minutes:0.0} idle minute(s) discarded.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Time tracking: the idle prompt failed; the time was discarded.", ex);
        }
        finally
        {
            _pendingIdle = null;
            _promptOpen = false;

            _monitor?.MarkActive();
            TimeTracker.Resume();
        }
    }

    private static void Reassign((WorkContext Context, DateTime FromUtc, DateTime ToUtc) idle, double minutes)
    {
        var window = new ManualEntryWindow(
            idle.Context.ProjectName,
            idle.Context.ProjectNumber,
            idle.FromUtc.ToLocalTime(),
            minutes,
            "Reassign the time you were away. It is pre-filled with the gap the tracker measured.");

        if (RevitWindow.ShowDialog(window, _uiApp) != true)
        {
            Log.Info($"Time tracking: reassignment cancelled; {minutes:0.0} minute(s) discarded.");
            return;
        }

        TimeTracker.LogManual(
            window.StartedLocal, window.Minutes, window.ProjectName, window.ProjectNumber,
            window.Description, idle.Context);
    }

    /// <summary>
    /// Upgrades the recorded user from the Windows login to the Autodesk sign-in name, once
    /// a UIApplication exists to ask. Runs at most once — after that <c>_usernameResolved</c>
    /// makes it a single bool test on a path that fires on every view change.
    /// </summary>
    private static void EnsureUsername()
    {
        if (_usernameResolved || _uiApp is null) return;
        _usernameResolved = true;

        var username = Safe(() => _uiApp!.Application.Username);
        if (string.IsNullOrWhiteSpace(username)) return;

        TimeTracker.Configure(_settings, username);
        Log.Info($"Time tracking: entries are recorded against Autodesk user '{username}'.");
    }

    private static bool _usernameResolved;

    private static T? Safe<T>(Func<T?> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
