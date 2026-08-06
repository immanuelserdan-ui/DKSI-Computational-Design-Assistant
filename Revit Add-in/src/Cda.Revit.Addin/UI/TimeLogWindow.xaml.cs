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
        LoadEntries();
    }

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

    private void LoadEntries()
    {
        try
        {
            var (from, to) = SelectedRange();
            var entries = TimeLogStore.Read(from, to, _settings);

            EntryList.ItemsSource = entries
                .OrderByDescending(e => e.Started)
                .Select(e => new Row
                {
                    Started = e.Started.ToString("ddd dd MMM HH:mm", CultureInfo.CurrentCulture),
                    Minutes = e.DurationMinutes.ToString("0.0", CultureInfo.InvariantCulture),
                    Project = e.ProjectName,
                    View = e.ViewName,
                    Template = e.ViewTemplate,
                    Type = e.Type.ToString(),
                    Description = e.Description,
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
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadEntries();

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
    private sealed class Row
    {
        public string Started { get; init; } = string.Empty;
        public string Minutes { get; init; } = string.Empty;
        public string Project { get; init; } = string.Empty;
        public string View { get; init; } = string.Empty;
        public string Template { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
    }
}
