using System.IO;
using System.Reflection;
using System.Text.Json;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// Everything about time tracking a user or an IT deployment can change, persisted as JSON.
///
/// Two files, and the FIRST ONE THAT EXISTS in this order wins outright — this is a
/// whole-file fallback, not a per-property merge:
///
///   1. <c>%LOCALAPPDATA%\Cda\RevitAddin\time-tracking.json</c> — the user's own choices,
///      written by the Settings panel.
///   2. <c>time-tracking.defaults.json</c> BESIDE THE DLL — the deployed copy, so IT can
///      push a shared folder path to everyone by dropping one file into the add-ins
///      folder. Without this, "configure the network path" is a per-machine manual step
///      that half the office never does, and half the timesheets never arrive.
///
/// Whole-file rather than merged because a merge cannot tell "the user left the shared
/// folder empty" from "the user never opened the settings", so a pushed default would
/// silently reappear every time someone deliberately cleared it.
///
/// Saving only ever writes (1). The deployed defaults are never rewritten by the add-in.
/// </summary>
public sealed class TimeTrackingSettings
{
    /// <summary>Master switch. Off means no events, no polling, no rows.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minutes of no input before the timer pauses. The brief's default.
    /// </summary>
    public int IdleThresholdMinutes { get; set; } = 5;

    /// <summary>
    /// Count "Revit is not the foreground window" as idle, even while the user is busy
    /// typing in another application.
    ///
    /// This matters more than the keyboard test. <c>GetLastInputInfo</c> is system-wide, so
    /// an hour spent writing an email with Revit open behind it registers as an hour of
    /// continuous input and would be billed to whatever view was last active. That is the
    /// single biggest way an automatic tracker over-reports.
    ///
    /// The trade is real and is why the return prompt exists: reading a PDF specification
    /// beside the model IS project work, and this counts it as idle. The user gets it back
    /// with "Keep" or reassigns it to the right project.
    /// </summary>
    public bool TreatRevitNotForegroundAsIdle { get; set; } = true;

    /// <summary>Ask keep/discard/reassign on return. Off silently discards idle time.</summary>
    public bool PromptOnReturn { get; set; } = true;

    /// <summary>
    /// Segments shorter than this are not written as their own row; the seconds are carried
    /// into the next segment with the same project and view instead.
    ///
    /// Without it, clicking through six views to find something produces six rows of four
    /// seconds each, and a month of that is a timesheet nobody can read. Nothing is lost —
    /// see the carry logic in <see cref="TimeTracker"/>.
    /// </summary>
    public int MinimumSegmentSeconds { get; set; } = 45;

    /// <summary>
    /// Shared company folder the log is appended to, as well as the local copy. Empty means
    /// local only. Each user writes their OWN file in there — see <see cref="TimeLogStore"/>
    /// for why one shared file per person rather than one for everybody.
    /// </summary>
    public string SharedFolder { get; set; } = string.Empty;

    /// <summary>
    /// CSV field separator.
    ///
    /// Defaults to ',' because the brief asks for standard comma-separated values, and it is
    /// what <c>FinishSurfaceAreaCommand</c> already writes. NOTE for Danish Excel: a
    /// double-click on a comma-separated file lands everything in column A. Set this to ";"
    /// if the log is meant to be opened rather than imported — <c>ReportWriter</c> uses ';'
    /// for exactly that reason.
    /// </summary>
    public string Separator { get; set; } = ",";

    /// <summary>
    /// Seconds between idle checks. Ten is enough resolution for a five-minute threshold
    /// and cheap enough to run off Revit's Idling event without anyone noticing.
    /// </summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>
    /// Seconds an open segment is allowed to run before it is flushed to disk as a heartbeat
    /// row, without closing it - see <see cref="TimeTracker.Heartbeat"/>. Zero or less turns
    /// this off, matching the original behaviour: a segment writes nothing until it actually
    /// closes, which can be an hour or more of continuous work in one view. Exists for
    /// consumers reading the LEDGER (not the live-status pipe, which already updates every
    /// second regardless of this) - the weekly hours, task/project breakdowns and other
    /// aggregates in OmniBIM only move when a row lands, so this is what bounds how stale
    /// those figures can get while someone sits in one view. Seconds, not minutes, so it can
    /// go below a minute without falling back to fractional values - default is 30.
    /// </summary>
    public int HeartbeatSeconds { get; set; } = 30;

    // ---- Project Information carried onto every row ---------------------------

    /// <summary>
    /// Names of the Project Information parameters recorded with each entry.
    ///
    /// CAPTURED WHEN THE TIME IS LOGGED, not when the timesheet is exported. The ledger
    /// spans every project you touch, so reading these at export time would stamp last
    /// month's rows with today's open model — a timesheet that quietly re-labels history is
    /// worse than one that leaves the columns blank.
    ///
    /// They are settings rather than constants because they are office conventions, not
    /// Revit built-ins: another team's template calls them something else, and a renamed
    /// parameter should be a line in a JSON file rather than a rebuild.
    /// </summary>
    public string AfdelingParameter { get; set; } = "Afdeling";

    public string ClientNumberParameter { get; set; } = "Client Number";

    public string OperatorParameter { get; set; } = "Operator";

    public string SelskabParameter { get; set; } = "Selskab";

    /// <summary>Project Information &gt; QA (the reviewer of record, alongside Operator).</summary>
    public string QAParameter { get; set; } = "QA";

    // ------------------------------------------------------------------ persistence

    public static string UserSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "time-tracking.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public char SeparatorChar => string.IsNullOrEmpty(Separator) ? ',' : Separator[0];

    public TimeSpan IdleThreshold => TimeSpan.FromMinutes(Math.Max(1, IdleThresholdMinutes));

    public static TimeTrackingSettings Load()
    {
        var settings = ReadOrNull(DeployedDefaultsPath()) ?? new TimeTrackingSettings();

        var user = ReadOrNull(UserSettingsPath);
        if (user is not null) settings = user;

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath)!);
            File.WriteAllText(UserSettingsPath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            // A settings file that cannot be written must not cost the user their tracking.
            Log.Warn($"Time tracking settings could not be saved: {ex.Message}");
        }
    }

    private static string DeployedDefaultsPath()
    {
        try
        {
            var folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return string.IsNullOrEmpty(folder)
                ? string.Empty
                : Path.Combine(folder, "time-tracking.defaults.json");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static TimeTrackingSettings? ReadOrNull(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TimeTrackingSettings>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Warn($"Time tracking settings at '{path}' are unreadable, ignoring them: {ex.Message}");
            return null;
        }
    }
}
