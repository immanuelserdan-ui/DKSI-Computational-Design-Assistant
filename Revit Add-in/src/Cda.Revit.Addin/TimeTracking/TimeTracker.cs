using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// The timer manager: exactly one open segment at a time, closed and written the moment
/// the user's context changes.
///
/// THE STATE MACHINE, in full:
///
///   nothing open  --Switch(ctx)-->  open(ctx)
///   open(ctx)     --Switch(ctx)-->  open(ctx)          (same context: no-op, no split)
///   open(a)       --Switch(b)  -->  close(a), open(b)
///   open(a)       --Pause(t)   -->  close(a) AT t, remember a
///   paused(a)     --Resume(b)  -->  open(b ?? a)
///   any           --Flush      -->  close, write, nothing open
///
/// Everything funnels through <see cref="Close"/>, so there is one place where time turns
/// into a row and one place that can get it wrong.
///
/// THREADING: every caller is on Revit's main thread — the ViewActivated event, the Idling
/// tick, the ribbon command. The lock is there because a modal dialog pumps messages, so a
/// second call CAN land while the first is still on the stack.
/// </summary>
internal static class TimeTracker
{
    private static readonly object Gate = new();

    private static TimeTrackingSettings _settings = new();
    private static string _username = Environment.UserName;

    private static WorkContext? _open;
    private static DateTime _openedUtc;

    /// <summary>
    /// The task/phase in effect right now - stamped onto a row when it is WRITTEN, exactly
    /// like <see cref="Write"/>'s <c>description</c> parameter, and for the same reason:
    /// keeping it out of <see cref="WorkContext"/>'s equality means changing it never counts
    /// as a context change, so it never triggers <see cref="Switch"/> to close and reopen the
    /// current segment. A manual pick therefore takes effect immediately with no interruption
    /// to the running elapsed clock - the trade is that a segment spanning a task change is
    /// stamped, whole, with whichever task was current when it finally closed, the same
    /// simplification already accepted for Description.
    /// </summary>
    private static string _taskPhase = string.Empty;
    private static string _taskCategory = string.Empty;

    /// <summary>
    /// Distinct, non-DKSI transaction names seen since the last row was written - UNLIKE
    /// <see cref="_taskPhase"/>, this does NOT persist across a write: it describes what
    /// happened up to that specific row, so a fresh row starts having recorded nothing.
    /// Stamped onto <see cref="TimeEntry.ExternalActivity"/> and cleared in <see cref="Write"/>.
    /// See <see cref="TimeTrackingService.OnDocumentChanged"/> for what decides what lands
    /// here - a best-effort, openly incomplete filter, not a reliable per-add-in measurement.
    /// </summary>
    private static readonly HashSet<string> _externalTransactions = new(StringComparer.Ordinal);

    /// <summary>The context that was open when the timer paused, so Resume can return to it.</summary>
    private static WorkContext? _pausedContext;

    /// <summary>
    /// Seconds from segments too short to deserve a row of their own, held per context.
    ///
    /// This is what makes <see cref="TimeTrackingSettings.MinimumSegmentSeconds"/> a
    /// filter on ROWS rather than on TIME. Click through six views looking for something
    /// and come back: the seconds spent in each are kept against that view and folded into
    /// its next real visit. Nothing is silently thrown away except the final remainder at
    /// the end of a session, which is seconds.
    /// </summary>
    private static readonly Dictionary<WorkContext, (double Seconds, DateTime FirstUtc)> Carry = [];

    public static void Configure(TimeTrackingSettings settings, string username)
    {
        lock (Gate)
        {
            _settings = settings;
            if (!string.IsNullOrWhiteSpace(username)) _username = username;
        }
    }

    /// <summary>
    /// The identity every new row is stamped with - the Windows login until
    /// <see cref="TimeTrackingService"/> upgrades it to the Autodesk sign-in name, same
    /// value <see cref="Configure"/> feeds into segment rows. Exposed so
    /// <see cref="SessionTrackingService"/> stamps sessions with the same account rather
    /// than resolving its own, possibly out-of-step, copy.
    /// </summary>
    public static string Username { get { lock (Gate) return _username; } }

    /// <summary>The task/phase that would be stamped on a row written right now.</summary>
    public static string TaskPhase { get { lock (Gate) return _taskPhase; } }

    /// <summary><see cref="TaskDetection.AutoDetected"/> or <see cref="TaskDetection.ManualOverride"/>.</summary>
    public static string TaskCategory { get { lock (Gate) return _taskCategory; } }

    /// <summary>
    /// A person picked this from the dropdown. Stands until they pick again, or until a
    /// STRONG auto-detection (family editor, sheet) overrides it - see
    /// <see cref="ApplyAutoTask"/>.
    /// </summary>
    public static void SetManualTask(string phase)
    {
        lock (Gate)
        {
            _taskPhase = phase;
            _taskCategory = TaskDetection.ManualOverride;
        }
    }

    /// <summary>
    /// Called from <see cref="TimeTrackingService"/> on every view activation with the
    /// result of <see cref="TaskDetection.Detect"/>.
    /// </summary>
    /// <param name="isStrong">
    /// True for family-editor and sheet detections, which describe what is unambiguously
    /// happening and so must win even over a standing manual choice; false for the softer
    /// "read the view's Phase" and "General Modeling" fallback readings, which only fill in
    /// a blank and must never quietly erase something the user picked by hand.
    /// </param>
    public static void ApplyAutoTask(string phase, bool isStrong)
    {
        lock (Gate)
        {
            if (!isStrong && _taskCategory == TaskDetection.ManualOverride) return;

            _taskPhase = phase;
            _taskCategory = TaskDetection.AutoDetected;
        }
    }

    /// <summary>Notes a model-writing transaction this add-in did not author.</summary>
    public static void RecordExternalTransaction(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (Gate) _externalTransactions.Add(name);
    }

    /// <summary>The context currently being timed, or null when nothing is open.</summary>
    public static WorkContext? Open { get { lock (Gate) return _open; } }

    /// <summary>The context tracking will return to when the user comes back.</summary>
    public static WorkContext? PausedContext { get { lock (Gate) return _pausedContext; } }

    public static bool IsPaused { get { lock (Gate) return _pausedContext is not null; } }

    /// <summary>How long the open segment has been running. Zero when paused or idle.</summary>
    public static TimeSpan Elapsed
    {
        get
        {
            lock (Gate) return _open is null ? TimeSpan.Zero : DateTime.UtcNow - _openedUtc;
        }
    }

    // ------------------------------------------------------------------ transitions

    /// <summary>
    /// Point the timer at a new context. Called from ViewActivated, so it runs several times
    /// a minute on a busy session and must be cheap when nothing has changed.
    /// </summary>
    public static void Switch(WorkContext context)
    {
        lock (Gate)
        {
            if (!_settings.Enabled) return;

            // Same project, same view, same template — the user never left. Splitting here
            // would turn one hour of work into forty rows.
            if (_open is not null && _open == context) return;

            var now = DateTime.UtcNow;
            Close(now);

            _open = context;
            _openedUtc = now;
            _pausedContext = null;

            Log.Debug($"Time tracking: now on '{context.Describe()}'.");
        }
    }

    /// <summary>
    /// Stops the clock as of <paramref name="atUtc"/> — the moment of the LAST REAL INPUT,
    /// not the moment idleness was noticed. Polling every ten seconds means those differ by
    /// the whole idle threshold, and billing that gap is exactly the over-reporting the
    /// feature exists to avoid.
    /// </summary>
    public static void Pause(DateTime atUtc)
    {
        lock (Gate)
        {
            if (_open is null) return;

            _pausedContext = _open;
            Close(atUtc);

            Log.Debug($"Time tracking: paused on '{_pausedContext.Describe()}'.");
        }
    }

    /// <summary>
    /// Restarts the clock, on <paramref name="context"/> if one is given and on whatever was
    /// open before the pause otherwise.
    /// </summary>
    public static void Resume(WorkContext? context = null)
    {
        lock (Gate)
        {
            if (!_settings.Enabled) return;

            var target = context ?? _pausedContext;
            if (target is null) return;

            _open = target;
            _openedUtc = DateTime.UtcNow;
            _pausedContext = null;

            Log.Debug($"Time tracking: resumed on '{target.Describe()}'.");
        }
    }

    /// <summary>
    /// Closes and writes whatever is open. Called on document close and on Revit shutdown —
    /// the two moments where the alternative is losing the segment entirely.
    /// </summary>
    public static void Flush(string reason)
    {
        lock (Gate)
        {
            var had = _open is not null;
            Close(DateTime.UtcNow);
            _pausedContext = null;

            // Carried fragments have nowhere left to go: write the ones big enough to be
            // worth a row and let the rest go. Holding them until "next time" would mean
            // holding them until never.
            foreach (var (context, carried) in Carry.ToList())
            {
                if (carried.Seconds >= _settings.MinimumSegmentSeconds)
                    Write(context, carried.FirstUtc, DateTime.UtcNow, carried.Seconds, TimeEntryType.Automated,
                        "Accumulated short visits");
            }

            Carry.Clear();

            if (had) Log.Info($"Time tracking: flushed ({reason}).");
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Books the idle span the user asked to keep, against the context they were in when
    /// they walked away.
    /// </summary>
    public static void KeepIdle(WorkContext context, DateTime fromUtc, DateTime toUtc, string description)
    {
        var seconds = (toUtc - fromUtc).TotalSeconds;
        if (seconds <= 0) return;

        lock (Gate) Write(context, fromUtc, toUtc, seconds, TimeEntryType.Automated, description);
    }

    /// <summary>Stores a hand-typed entry. Same schema, same file, same export.</summary>
    /// <param name="context">
    /// The model that was open when the entry was typed, if any. It contributes only the
    /// Project Information fields — a meeting about a project belongs to that project's
    /// company and department, even though it has no view.
    /// </param>
    public static void LogManual(
        DateTime startedLocal,
        double minutes,
        string projectName,
        string projectNumber,
        string description,
        WorkContext? context = null)
    {
        if (minutes <= 0) return;

        var entry = new TimeEntry
        {
            Started = startedLocal,
            Ended = startedLocal.AddMinutes(minutes),
            DurationMinutes = Math.Round(minutes, 2),
            Username = _username,
            Type = TimeEntryType.Manual,
            ProjectName = projectName,
            ProjectNumber = projectNumber,
            Description = description,
            Selskab = context?.Selskab ?? string.Empty,
            Afdeling = context?.Afdeling ?? string.Empty,
            ClientNumber = context?.ClientNumber ?? string.Empty,
            Operator = context?.Operator ?? string.Empty,
            QA = context?.QA ?? string.Empty,
            TaskPhase = _taskPhase,
            TaskCategory = _taskCategory,
        };

        TimeLogStore.Append(entry, _settings);
        Log.Info($"Time tracking: manual entry, {entry.DurationMinutes:0.00} min on '{projectName}'.");
    }

    /// <summary>
    /// Closes the open segment at <paramref name="endUtc"/>. Must be called under the lock.
    /// </summary>
    private static void Close(DateTime endUtc)
    {
        if (_open is null) return;

        var context = _open;
        var startUtc = _openedUtc;
        _open = null;

        var seconds = (endUtc - startUtc).TotalSeconds;

        // A clock change, or a pause timestamped before the segment even started. Neither is
        // billable and a negative duration would poison every sum downstream.
        if (seconds <= 0) return;

        var carried = Carry.TryGetValue(context, out var held) ? held : (Seconds: 0.0, FirstUtc: startUtc);
        var total = seconds + carried.Seconds;

        if (total < _settings.MinimumSegmentSeconds)
        {
            // Too short to be a row. Keep it against this context; its first start time is
            // what the eventual row will be stamped with.
            Carry[context] = (total, carried.Seconds > 0 ? carried.FirstUtc : startUtc);
            return;
        }

        Carry.Remove(context);

        // Stamped from the CARRIED start when there was one, so the row covers the period it
        // actually describes. This is why DurationMinutes can exceed Ended - Started.
        var stampUtc = carried.Seconds > 0 ? carried.FirstUtc : startUtc;
        Write(context, stampUtc, endUtc, total, TimeEntryType.Automated, string.Empty);
    }

    private static void Write(
        WorkContext context,
        DateTime startUtc,
        DateTime endUtc,
        double seconds,
        TimeEntryType type,
        string description)
    {
        var entry = new TimeEntry
        {
            Started = startUtc.ToLocalTime(),
            Ended = endUtc.ToLocalTime(),
            DurationMinutes = Math.Round(seconds / 60.0, 2),
            Username = _username,
            Type = type,
            ProjectName = context.ProjectName,
            ProjectNumber = context.ProjectNumber,
            FileName = context.FileName,
            ViewName = context.ViewName,
            ViewTemplate = context.ViewTemplate,
            Description = description,
            Selskab = context.Selskab,
            Afdeling = context.Afdeling,
            ClientNumber = context.ClientNumber,
            Operator = context.Operator,
            QA = context.QA,
            TaskPhase = _taskPhase,
            TaskCategory = _taskCategory,
            ExternalActivity = _externalTransactions.Count == 0
                ? string.Empty
                : string.Join("; ", _externalTransactions.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
        };

        // Cleared here, not preserved like _taskPhase - this describes what happened UP TO
        // this row, and a row that has just been written has nothing left to describe.
        _externalTransactions.Clear();

        TimeLogStore.Append(entry, _settings);
    }
}
