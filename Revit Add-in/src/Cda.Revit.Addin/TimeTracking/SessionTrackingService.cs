using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// Marks the start and end of a Revit session on a project - independent of
/// <see cref="TimeTrackingService"/>, which marks the start and end of a SEGMENT (one
/// view-visit). A session spans from the document opening to it closing, however many
/// segments happen inside it.
///
/// Registered and torn down from <see cref="CdaApplication"/> alongside
/// <see cref="TimeTrackingService"/>, and just as tolerant of its own failures: a session
/// tracker that stops a document from opening is a worse trade than the feature it adds.
///
/// ONE OPEN SESSION PER DOCUMENT, not one globally - Revit allows several project documents
/// open in the same process (multiple tabs), and each is its own session with its own
/// start time. Keyed through <see cref="DocumentScoped{T}"/>, the same per-document state
/// map the finish and opening automations use, rather than a raw dictionary on the
/// Document reference - see that class for why identity by path/title, not by object, is
/// what a document-keyed map needs.
/// </summary>
internal static class SessionTrackingService
{
    /// <summary>Mutable, per-document, in the shape <see cref="DocumentScoped{T}"/> expects.</summary>
    private sealed class SessionState
    {
        public bool IsOpen { get; set; }
        public DateTime StartedLocal { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public string ProjectNumber { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public DateTime? DateLastModified { get; set; }
    }

    private static readonly DocumentScoped<SessionState> Sessions = new();

    /// <summary>
    /// A still-open session, exposed for the Time Tracking window's Project Info panel -
    /// which needs "session started at" and "how stale was the file" for the session the
    /// user is CURRENTLY in, one that by definition has no row in <see cref="SessionLogStore"/>
    /// yet, since a row is only written when the session closes.
    /// </summary>
    public sealed record OpenSessionInfo(
        string ProjectName, string ProjectNumber, DateTime StartedLocal, DateTime? DateLastModified);

    /// <summary>
    /// The most recently opened session in this process, tracked separately from the
    /// per-document map above because the window has only a <see cref="WorkContext"/> to
    /// match against, not a Document reference. Good enough for the common case of one
    /// project open at a time; with several open, this follows whichever was opened last.
    /// </summary>
    private static OpenSessionInfo? _mostRecentlyOpened;

    /// <summary>The open session matching <paramref name="context"/>'s project, or null.</summary>
    public static OpenSessionInfo? CurrentFor(WorkContext context)
    {
        var snapshot = _mostRecentlyOpened;
        if (snapshot is null) return null;

        return string.Equals(KeyOf(context.ProjectNumber, context.ProjectName),
            KeyOf(snapshot.ProjectNumber, snapshot.ProjectName), StringComparison.OrdinalIgnoreCase)
            ? snapshot
            : null;
    }

    private static string KeyOf(string projectNumber, string projectName) =>
        projectNumber.Length > 0 ? projectNumber : projectName;

    public static void Register(UIControlledApplication application)
    {
        try
        {
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
            application.ControlledApplication.DocumentClosing += OnDocumentClosing;

            Log.Info("Session tracking registered.");
        }
        catch (Exception ex)
        {
            Log.Error("Session tracking could not be registered.", ex);
        }
    }

    public static void Unregister(UIControlledApplication application)
    {
        try
        {
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        }
        catch (Exception ex)
        {
            Log.Warn($"Session tracking shutdown was untidy: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens a session for a document that has just loaded.
    ///
    /// WHAT IT REFUSES TO TRACK: linked documents - somebody else's model, loaded as a
    /// reference by whatever the user actually opened, and counting it as its own session
    /// would double-book the same stretch of time under two rows.
    /// </summary>
    private static void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e)
    {
        try
        {
            var doc = e.Document;
            if (doc is null || doc.IsLinked) return;

            var context = WorkContext.From(doc, null, TimeTrackingService.Settings);
            // File.Exists first: GetLastWriteTime does not throw for a path that is not a
            // real local file - a cloud (BIM 360/ACC) model's PathName is not one - it
            // silently returns 1601-01-01, which would misreport as "never modified".
            var lastModified = Safe(() =>
                !string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName)
                    ? File.GetLastWriteTime(doc.PathName)
                    : (DateTime?)null);

            var state = Sessions.For(doc);
            state.IsOpen = true;
            state.StartedLocal = DateTime.Now;
            state.ProjectName = context.ProjectName;
            state.ProjectNumber = context.ProjectNumber;
            state.FileName = context.FileName;
            state.DateLastModified = lastModified;

            _mostRecentlyOpened = new OpenSessionInfo(
                context.ProjectName, context.ProjectNumber, state.StartedLocal, lastModified);

            Log.Debug($"Session tracking: session opened on '{context.ProjectName}'.");
        }
        catch (Exception ex)
        {
            // Runs during document open - the worst possible moment to let an exception
            // reach Revit's own handler.
            Log.Error("Session tracking: DocumentOpened handler failed.", ex);
        }
    }

    private static void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
    {
        try
        {
            var doc = e.Document;
            if (doc is null || !Sessions.Has(doc)) return;

            var state = Sessions.For(doc);
            Sessions.Forget(doc);

            if (!state.IsOpen) return;

            // Only clear the "currently open" snapshot if THIS is the session it describes -
            // a second document closing must not erase the first one's still-open snapshot.
            if (_mostRecentlyOpened is not null &&
                string.Equals(KeyOf(state.ProjectNumber, state.ProjectName),
                    KeyOf(_mostRecentlyOpened.ProjectNumber, _mostRecentlyOpened.ProjectName),
                    StringComparison.OrdinalIgnoreCase))
                _mostRecentlyOpened = null;

            var entry = new SessionEntry
            {
                SessionStart = state.StartedLocal,
                SessionEnd = DateTime.Now,
                Username = TimeTracker.Username,
                ProjectName = state.ProjectName,
                ProjectNumber = state.ProjectNumber,
                FileName = state.FileName,
                DateLastModified = state.DateLastModified,
            };

            SessionLogStore.Append(entry, TimeTrackingService.Settings);
            Log.Info($"Session tracking: {(entry.SessionEnd - entry.SessionStart).TotalMinutes:0.0} " +
                     $"minute(s) on '{state.ProjectName}'.");
        }
        catch (Exception ex)
        {
            Log.Error("Session tracking: DocumentClosing handler failed.", ex);
        }
    }

    private static T? Safe<T>(Func<T?> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
