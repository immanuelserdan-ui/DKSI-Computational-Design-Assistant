using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.TimeTracking;
using Microsoft.Win32;

namespace Cda.Revit.Addin.UI;

/// <summary>
/// The one window the feature has: what is being tracked right now, what has been logged,
/// where it is written, and the two actions (manual entry, export) the brief asks for.
///
/// Modal, owned by the Revit main window. A dockable pane was the alternative and was not
/// worth it here: a dockable pane has to be registered at startup whether anyone opens it
/// or not, it survives across documents with state of its own, and none of that buys
/// anything for a window people open twice a day.
/// </summary>
public partial class TimeLogWindow : Window
{
    private readonly TimeTrackingSettings _settings;
    private readonly DispatcherTimer _clock;
    private bool _ready;

    /// <summary>
    /// The project the Modeller Status control is attached to - fixed for the life of this
    /// (modal) window, since Revit cannot switch documents while it is open. Null when there
    /// is nothing to attach a status to (no document, or one with neither a project number
    /// nor a name to file it under).
    /// </summary>
    private WorkContext? _statusContext;
    private bool _statusReady;
    private bool _taskPhaseReady;

    public TimeLogWindow(TimeTrackingSettings settings)
    {
        InitializeComponent();

        _settings = settings;

        EnabledBox.IsChecked = settings.Enabled;
        ForegroundBox.IsChecked = settings.TreatRevitNotForegroundAsIdle;
        PromptBox.IsChecked = settings.PromptOnReturn;
        IdleBox.Text = settings.IdleThresholdMinutes.ToString(CultureInfo.InvariantCulture);
        MinimumBox.Text = settings.MinimumSegmentSeconds.ToString(CultureInfo.InvariantCulture);
        SharedFolderBox.Text = settings.SharedFolder;
        SeparatorBox.SelectedIndex = settings.SeparatorChar == ';' ? 1 : 0;

        PathsLine.Text =
            $"Local log: {TimeLogStore.LocalFileFor(DateTime.Now)}\n" +
            $"Shared copy: {TimeLogStore.SharedFileFor(DateTime.Now, settings) ?? "not configured"}";

        // One second is enough for a running clock and cheap enough that nobody notices.
        // A modal WPF window pumps its own dispatcher, so this fires reliably here even
        // though a DispatcherTimer would be unreliable at Revit startup.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateStatus();
        _clock.Start();

        Closed += (_, _) => _clock.Stop();

        _ready = true;
        UpdateStatus();
        LoadModellerStatus();
        LoadProjectInfo();
        LoadTaskPhase();
        LoadEntries();
        LoadSessions();
    }

    // ------------------------------------------------------------------ task / phase

    /// <summary>
    /// Loaded once, like Modeller Status - but note this reads <see cref="TimeTracker"/>'s
    /// live, in-memory state, not a file, so unlike the other Load* methods there is nothing
    /// here that can fail on a locked or unreachable path.
    /// </summary>
    private void LoadTaskPhase()
    {
        TaskPhaseBox.ItemsSource = TaskDetection.Presets;

        _taskPhaseReady = false;
        // A no-op when the live value is a native Phase name outside the six presets (e.g.
        // "Existing") - the dropdown offers only the fixed list to override with, but
        // TaskPhaseDetail below always reports what is actually in effect regardless.
        TaskPhaseBox.SelectedItem = TimeTracker.TaskPhase;
        TaskPhaseDetail.Text = DescribeTask(TimeTracker.TaskPhase, TimeTracker.TaskCategory);
        _taskPhaseReady = true;
    }

    private void OnTaskPhaseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_taskPhaseReady) return;
        if (TaskPhaseBox.SelectedItem is not string phase) return;

        TimeTracker.SetManualTask(phase);
        TaskPhaseDetail.Text = DescribeTask(TimeTracker.TaskPhase, TimeTracker.TaskCategory);
    }

    private static string DescribeTask(string phase, string category) =>
        phase.Length == 0
            ? "Nothing detected yet - open a project view."
            : $"Currently logging as: {phase} ({category}).";

    // ------------------------------------------------------------------ project info

    /// <summary>
    /// The single facts about whichever project is open. Loaded once, off the same
    /// <see cref="_statusContext"/> <see cref="LoadModellerStatus"/> resolves - it must run
    /// first, in the constructor above.
    /// </summary>
    private void LoadProjectInfo()
    {
        if (_statusContext is null)
        {
            foreach (var block in new[]
                     {
                         InfoProjectName, InfoAfdeling, InfoSelskab, InfoOperator, InfoQA,
                         InfoDateStarted, InfoLastModified, InfoAutodeskAccount,
                     })
                block.Text = "—";
            return;
        }

        var context = _statusContext;

        InfoProjectName.Text = Blank(context.ProjectName);
        InfoAfdeling.Text = Blank(context.Afdeling);
        InfoSelskab.Text = Blank(context.Selskab);
        InfoOperator.Text = Blank(context.Operator);
        InfoQA.Text = Blank(context.QA);
        InfoAutodeskAccount.Text = Blank(TimeTracker.Username);

        try
        {
            var status = ProjectStatusStore.Get(context, _settings);
            InfoDateStarted.Text = status is null
                ? "—"
                : status.DateStarted.ToString("dd MMM yyyy", CultureInfo.CurrentCulture);
        }
        catch (Exception ex)
        {
            Log.Warn($"Project info: date started could not be read: {ex.Message}");
            InfoDateStarted.Text = "—";
        }

        // The still-open session first - it has no row in the ledger yet, so it is the only
        // place "how stale was this file when I opened it" can come from right now. Falling
        // back to the most recently CLOSED session covers the (unusual) case of the window
        // being reached with no live session matched, rather than showing nothing.
        var open = SessionTrackingService.CurrentFor(context);
        var lastModified = open?.DateLastModified;

        if (lastModified is null)
        {
            try
            {
                var recent = SessionLogStore
                    .Read(DateTime.Today.AddYears(-2), DateTime.Today, _settings)
                    .Where(s => SameProject(s, context))
                    .OrderByDescending(s => s.SessionStart)
                    .FirstOrDefault();

                lastModified = recent?.DateLastModified;
            }
            catch (Exception ex)
            {
                Log.Warn($"Project info: date last modified could not be read: {ex.Message}");
            }
        }

        InfoLastModified.Text = lastModified is { } modified
            ? modified.ToString("dd MMM yyyy HH:mm", CultureInfo.CurrentCulture)
            : "—";
    }

    private static bool SameProject(SessionEntry entry, WorkContext context)
    {
        var entryKey = entry.ProjectNumber.Length > 0 ? entry.ProjectNumber : entry.ProjectName;
        var contextKey = ProjectStatusStore.KeyFor(context);
        return string.Equals(entryKey, contextKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string Blank(string value) => value.Length == 0 ? "—" : value;

    // ------------------------------------------------------------------ sessions

    /// <summary>
    /// One row per Revit session on the current project, for the selected date range, plus a
    /// synthetic top row for a session that is still open - see
    /// <see cref="SessionTrackingService.CurrentFor"/> for why that one cannot come from the
    /// file.
    /// </summary>
    private void LoadSessions()
    {
        if (_statusContext is null)
        {
            SessionList.ItemsSource = null;
            return;
        }

        var context = _statusContext;

        try
        {
            var (from, to) = SelectedRange();

            var rows = SessionLogStore.Read(from, to, _settings)
                .Where(s => SameProject(s, context))
                .OrderByDescending(s => s.SessionStart)
                .Select(s => new SessionRow
                {
                    Start = s.SessionStart.ToString("ddd dd MMM HH:mm", CultureInfo.CurrentCulture),
                    End = s.SessionEnd.ToString("ddd dd MMM HH:mm", CultureInfo.CurrentCulture),
                    Duration = FormatHoursMinutes((s.SessionEnd - s.SessionStart).TotalMinutes),
                })
                .ToList();

            var open = SessionTrackingService.CurrentFor(context);
            if (open is not null)
            {
                rows.Insert(0, new SessionRow
                {
                    Start = open.StartedLocal.ToString("ddd dd MMM HH:mm", CultureInfo.CurrentCulture),
                    End = "(in progress)",
                    Duration = FormatHoursMinutes((DateTime.Now - open.StartedLocal).TotalMinutes),
                });
            }

            SessionList.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            Log.Warn($"Sessions could not be read: {ex.Message}");
            SessionList.ItemsSource = null;
        }
    }

    private sealed class SessionRow
    {
        public string Start { get; init; } = string.Empty;
        public string End { get; init; } = string.Empty;
        public string Duration { get; init; } = string.Empty;
    }

    // ------------------------------------------------------------------ modeller status

    /// <summary>
    /// Loads the current status for whichever project is open, once - not on every clock
    /// tick like <see cref="UpdateStatus"/>, both because the project cannot change while
    /// this modal window is up and because re-reading a shared-folder JSON file every second
    /// would be needless network chatter.
    /// </summary>
    private void LoadModellerStatus()
    {
        ModellerStatusBox.ItemsSource = ProjectStatusStore.Statuses;

        _statusContext = TimeTracker.Open ?? TimeTracker.PausedContext;

        if (_statusContext is null || ProjectStatusStore.KeyFor(_statusContext).Length == 0)
        {
            ModellerStatusBox.IsEnabled = false;
            ModellerStatusDetail.Text = "Open a project to set its status.";
            return;
        }

        try
        {
            var record = ProjectStatusStore.Get(_statusContext, _settings);

            _statusReady = false;
            ModellerStatusBox.SelectedItem = record?.Status;
            ModellerStatusDetail.Text = DescribeStatus(record);
        }
        catch (Exception ex)
        {
            Log.Warn($"Modeller status could not be read: {ex.Message}");
            ModellerStatusDetail.Text = "Status could not be read - see the add-in log.";
        }
        finally
        {
            _statusReady = true;
        }
    }

    private void OnModellerStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires while ItemsSource/SelectedItem are being applied above, before there is a
        // context to save against.
        if (!_statusReady || _statusContext is null) return;
        if (ModellerStatusBox.SelectedItem is not string status) return;

        try
        {
            ProjectStatusStore.SetStatus(_statusContext, status, TimeTracker.Username, _settings);
            ModellerStatusDetail.Text = DescribeStatus(ProjectStatusStore.Get(_statusContext, _settings));
        }
        catch (Exception ex)
        {
            Log.Warn($"Modeller status could not be saved: {ex.Message}");
            ModellerStatusDetail.Text = "Status could not be saved - see the add-in log.";
        }
    }

    private static string DescribeStatus(ProjectStatusRecord? record) =>
        record is null
            ? "No status set yet."
            : $"Set by {record.LastChangedBy} on {record.LastChangedUtc.ToLocalTime():dd MMM HH:mm}.";

    // ------------------------------------------------------------------ status

    private void UpdateStatus()
    {
        if (!_settings.Enabled)
        {
            StatusLine.Text = "Automatic tracking is OFF.";
            StatusDetail.Text = "Manual entries still work. Turn tracking on under Settings below.";
            return;
        }

        var open = TimeTracker.Open;

        if (TimeTrackingService.IsIdle || open is null)
        {
            var paused = TimeTracker.PausedContext;

            StatusLine.Text = paused is null
                ? "Waiting — nothing is being timed."
                : $"PAUSED — idle for {Format(TimeTrackingService.IdleFor)}.";

            StatusDetail.Text = paused is null
                ? "Open a project view and the timer starts by itself."
                : $"The clock stopped on {paused.Describe()} and picks up again when you come back. " +
                  "You will be asked whether to keep, discard or reassign the gap.";
            return;
        }

        StatusLine.Text = $"Tracking {open.Describe()} — {Format(TimeTracker.Elapsed)}";

        var template = string.IsNullOrWhiteSpace(open.ViewTemplate) ? "none" : open.ViewTemplate;
        StatusDetail.Text =
            $"File: {open.FileName}   ·   Project number: " +
            $"{(open.ProjectNumber.Length == 0 ? "—" : open.ProjectNumber)}   ·   View template: {template}";
    }

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours} h {span.Minutes} min"
            : $"{span.Minutes} min {span.Seconds} s";

    // ------------------------------------------------------------------ the list

    private (DateTime From, DateTime To) SelectedRange()
    {
        var today = DateTime.Today;

        return RangeBox.SelectedIndex switch
        {
            1 => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today),   // Monday-based week
            2 => (new DateTime(today.Year, today.Month, 1), today),
            3 => (today.AddDays(-90), today),
            _ => (today, today),
        };
    }

    /// <summary>
    /// Rolled up by day and project - the same grouping <c>TimesheetReport</c>'s Summary
    /// sheet computes for the Excel export - rather than one row per raw view-visit segment.
    /// The CSV ledger keeps every segment exactly as before; this is a reading of it, and
    /// re-reads the full detail every time <see cref="TotalLine"/> below needs it.
    /// </summary>
    private void LoadEntries()
    {
        try
        {
            var (from, to) = SelectedRange();
            var entries = TimeLogStore.Read(from, to, _settings);

            EntryList.ItemsSource = entries
                .GroupBy(e => (e.Started.Date, e.ProjectName, TaskPhase: e.TaskPhase.Length == 0 ? "(none)" : e.TaskPhase))
                .OrderByDescending(g => g.Key.Date)
                .ThenBy(g => g.Key.ProjectName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(g => g.Key.TaskPhase, StringComparer.CurrentCultureIgnoreCase)
                .Select(g =>
                {
                    var minutes = g.Sum(e => e.DurationMinutes);
                    return new Row
                    {
                        Date = g.Key.Date.ToString("ddd dd MMM", CultureInfo.CurrentCulture),
                        Project = g.Key.ProjectName,
                        TaskPhase = g.Key.TaskPhase,
                        Hours = (minutes / 60.0).ToString("0.00", CultureInfo.InvariantCulture),
                        Time = FormatHoursMinutes(minutes),
                        Entries = g.Count().ToString(CultureInfo.InvariantCulture),
                    };
                })
                .ToList();

            var total = entries.Sum(e => e.DurationMinutes);
            var manual = entries.Where(e => e.Type == TimeEntryType.Manual).Sum(e => e.DurationMinutes);

            TotalLine.Text =
                $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}   ·   " +
                $"{total / 60.0:0.00} h total   ·   {manual / 60.0:0.00} h of that entered by hand";
        }
        catch (Exception ex)
        {
            Log.Error("Time tracking: the log could not be read.", ex);
            TotalLine.Text = "The log could not be read — see the add-in log.";
        }
    }

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged fires while XAML applies IsSelected, before the fields exist.
        if (!_ready) return;
        LoadEntries();
        LoadSessions();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        LoadEntries();
        LoadSessions();
    }

    // ------------------------------------------------------------------ actions

    private void OnAddManual(object sender, RoutedEventArgs e)
    {
        var open = TimeTracker.Open ?? TimeTracker.PausedContext;

        var window = new ManualEntryWindow(
            open?.ProjectName ?? string.Empty,
            open?.ProjectNumber ?? string.Empty,
            DateTime.Now,
            0)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        if (window.ShowDialog() != true) return;

        TimeTracker.LogManual(
            window.StartedLocal, window.Minutes, window.ProjectName, window.ProjectNumber,
            window.Description, open);

        LoadEntries();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var (from, to) = SelectedRange();

        // Excel first, and the default. The raw CSV is the event log - correct for a machine,
        // unreadable for a person - so the readable form is what the button offers first.
        // It also sidesteps the comma-versus-semicolon problem entirely: a workbook has
        // columns, not separators.
        var dialog = new SaveFileDialog
        {
            Title = "Export time log",
            Filter = "Excel timesheet (*.xlsx)|*.xlsx|Raw event log, CSV (*.csv)|*.csv",
            FilterIndex = 1,
            DefaultExt = ".xlsx",
            FileName = $"timesheet-{Environment.UserName}-{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx",
            InitialDirectory = Directory.Exists(_settings.SharedFolder)
                ? _settings.SharedFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            int rows;
            var asCsv = dialog.FilterIndex == 2 ||
                        dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

            if (asCsv)
            {
                rows = TimeLogStore.Export(dialog.FileName, from, to, _settings);
            }
            else
            {
                var entries = TimeLogStore.Read(from, to, _settings);
                TimesheetReport.WriteXlsx(dialog.FileName, entries, from, to, Environment.UserName);
                rows = entries.Count;
            }

            var result = MessageBox.Show(
                this,
                $"{rows} entr{(rows == 1 ? "y" : "ies")} written to\n{dialog.FileName}\n\n" +
                (asCsv
                    ? "This is the raw event log — one row per view visit."
                    : "Three sheets: Summary by day, By view, and the full Detail.") +
                "\n\nOpen it now?",
                "Export time log",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo { FileName = dialog.FileName, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Time tracking: export failed.", ex);
            MessageBox.Show(this, ex.Message, "Export time log", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Shared folder for the time log",
            InitialDirectory = Directory.Exists(SharedFolderBox.Text)
                ? SharedFolderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog(this) == true) SharedFolderBox.Text = dialog.FolderName;
    }

    /// <summary>
    /// Proves the shared folder before anyone relies on it. Deliberately reports the exact
    /// file entries will land in, so the answer to "where does my time go?" is on screen
    /// rather than inferred from a naming convention.
    /// </summary>
    private void OnTestPath(object sender, RoutedEventArgs e)
    {
        var folder = SharedFolderBox.Text.Trim();
        var (ok, message) = TimeLogStore.TestPath(folder);

        MessageBox.Show(
            this,
            message,
            ok ? "Shared folder is usable" : "Shared folder problem",
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IdleBox.Text.Trim(), out var idle) || idle < 1)
        {
            MessageBox.Show(this, "The idle threshold must be a whole number of minutes, at least 1.",
                "Time Tracking", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(MinimumBox.Text.Trim(), out var minimum) || minimum < 0)
        {
            MessageBox.Show(this, "The shortest visit must be a whole number of seconds.",
                "Time Tracking", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folder = SharedFolderBox.Text.Trim();

        // The real test, not Directory.Exists. A read-only share exists and still loses
        // every entry, which is the failure worth catching before someone relies on it.
        if (folder.Length > 0)
        {
            var (ok, message) = TimeLogStore.TestPath(folder);

            if (!ok)
            {
                var proceed = MessageBox.Show(this,
                    message + "\n\nSave it anyway? Entries are always written locally first, and " +
                    "the queued shared copies are retried - including after a restart - so a folder " +
                    "that is merely offline right now will catch up by itself.",
                    "Time Tracking", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (proceed != MessageBoxResult.Yes) return;
            }
        }

        _settings.Enabled = EnabledBox.IsChecked == true;
        _settings.TreatRevitNotForegroundAsIdle = ForegroundBox.IsChecked == true;
        _settings.PromptOnReturn = PromptBox.IsChecked == true;
        _settings.IdleThresholdMinutes = idle;
        _settings.MinimumSegmentSeconds = minimum;
        _settings.SharedFolder = folder;
        _settings.Separator = SeparatorBox.SelectedIndex == 1 ? ";" : ",";

        TimeTrackingService.Apply(_settings);

        PathsLine.Text =
            $"Local log: {TimeLogStore.LocalFileFor(DateTime.Now)}\n" +
            $"Shared copy: {TimeLogStore.SharedFileFor(DateTime.Now, _settings) ?? "not configured"}";

        UpdateStatus();
        LoadEntries();

        MessageBox.Show(this, "Saved.", "Time Tracking", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Flat row for the grid. The entries themselves stay unformatted.</summary>
    /// <summary>H:MM, for reading - same convention as <c>TimesheetReport</c>'s export.</summary>
    private static string FormatHoursMinutes(double minutes)
    {
        var rounded = (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
        return $"{rounded / 60}:{rounded % 60:00}";
    }

    /// <summary>One day, one project - the grid's row shape after the rollup in <see cref="LoadEntries"/>.</summary>
    private sealed class Row
    {
        public string Date { get; init; } = string.Empty;
        public string Project { get; init; } = string.Empty;
        public string TaskPhase { get; init; } = string.Empty;
        public string Hours { get; init; } = string.Empty;
        public string Time { get; init; } = string.Empty;
        public string Entries { get; init; } = string.Empty;
    }
}
