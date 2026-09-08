using System.Diagnostics.CodeAnalysis;
using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// "Is this the same document", and "which document is this" as a dictionary key.
///
/// Two situations need it, and getting either wrong writes to the wrong file:
///
///   DEFERRED WORK. A DocumentChanged handler captures a Document from the event, defers work
///   through RevitTaskQueue or an ExternalEvent, and by the time that work runs must decide
///   whether UIApplication.ActiveUIDocument.Document is still that same document before acting
///   on it. <see cref="IsSame"/> answers that.
///
///   QUEUED WORK. An automation banks ElementIds while the user edits and drains the queue on a
///   later idle tick. ElementIds are only meaningful inside the document that minted them, so
///   the queue has to be keyed by document rather than held globally. <see cref="KeyOf"/> is
///   that key.
///
/// WHY NOT ReferenceEquals
///   That was the first version, in both PaintRoomOverrideAutoReapply and
///   LegacySurfaceScheduleCleanup, and it is why neither ever did its actual work even after
///   PaintTakeoffTrigger started firing correctly - confirmed by a run whose log showed the
///   trigger matching and then nothing else at all. Revit's interop layer is not guaranteed to
///   hand back the identical .NET object for "the same" document across two different API
///   paths - DocumentChangedEventArgs.GetDocument() on one side, UIApplication.ActiveUIDocument
///   on the other - so a reference comparison between them silently failed on every single run,
///   in the one case (a single open document) this whole check exists to let through.
///
/// WHY PathName FIRST AND Title ONLY AS A FALLBACK
///   Title is the file NAME, not an identity. A job model and its backup, or the same template
///   opened once from a local path and once from the network, are two different documents with
///   one Title - and a Title-only comparison lets work queued against one land on the other.
///   PathName is the full path and is unambiguous whenever it is populated.
///
///   It is empty for a document that has never been saved, which - for editing a .rte template
///   directly, as this add-in does on this machine - is a real case rather than an exotic one.
///   So PathName decides when BOTH documents have one, and Title decides only when at least one
///   does not. That keeps the unsaved-template case working exactly as before while removing
///   the false match between two saved files that happen to share a name.
/// </summary>
internal static class DocumentIdentity
{
    public static bool IsSame([NotNullWhen(true)] Document? a, Document? b)
    {
        if (a is null || b is null) return false;
        if (ReferenceEquals(a, b)) return true;

        try
        {
            var pathA = a.PathName ?? string.Empty;
            var pathB = b.PathName ?? string.Empty;

            // Both saved: the path is the identity, and two files with the same name in
            // different folders are correctly told apart here.
            if (pathA.Length > 0 && pathB.Length > 0)
                return string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase);

            // At least one has never been saved and has no path to compare. Title is all
            // there is, and it is what this class used exclusively before.
            return string.Equals(a.Title, b.Title, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A stable dictionary key for one document, for the automations that bank per-document
    /// work between events.
    ///
    /// Built from PathName when the document has been saved and from Title when it has not,
    /// matching <see cref="IsSame"/>'s ordering - so a document keyed while unsaved and then
    /// saved changes key, which drops its banked queue rather than applying it to the wrong
    /// file. Losing a queue costs one recalculation; keeping a stale one costs correctness.
    /// </summary>
    public static string KeyOf(Document? doc)
    {
        if (doc is null) return string.Empty;

        try
        {
            var path = doc.PathName ?? string.Empty;
            return path.Length > 0 ? path : "title:" + doc.Title;
        }
        catch
        {
            return string.Empty;
        }
    }
}
