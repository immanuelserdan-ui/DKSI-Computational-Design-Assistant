using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Qa;

/// <summary>
/// Answers "is this still the same model?" across the life of a modeless window.
///
/// WHY REFERENCE EQUALITY IS THE WRONG TEST, AND WHY IT LOOKS LIKE THE RIGHT ONE
///   A <see cref="Document"/> handed back by the API is a managed WRAPPER around a native
///   document. Revit does not promise to hand back the same wrapper twice, and in practice it
///   does not: <c>UIApplication.ActiveUIDocument.Document</c> can produce a different object
///   on every call while the model on screen never changed.
///
///   So <c>ReferenceEquals(active, captured)</c> compares two wrappers around one model and
///   reports them as different documents. The first version of the QA windows did exactly
///   that, and refused to highlight anything with "the model this window was opened against
///   is no longer the active one" - while pointing at that very model.
///
///   The trap is that the test is not merely wrong, it is wrong INTERMITTENTLY. Sometimes the
///   same wrapper does come back and the guard passes, which is what makes this class of bug
///   survive a casual test.
///
/// WHAT IS STABLE
///   <see cref="Document.CreationGUID"/> identifies the document itself, not the wrapper. It
///   survives every wrapper the API creates, and it distinguishes two models that happen to
///   share a title - a detached local and its central, say - which a path or name comparison
///   would not.
/// </summary>
internal static class DocumentIdentity
{
    /// <summary>
    /// The document's stable identity, or <see cref="Guid.Empty"/> if it cannot be read
    /// (a closed document, or one whose wrapper has already been invalidated).
    /// </summary>
    public static Guid Of(Document? doc)
    {
        if (doc is null) return Guid.Empty;

        try
        {
            return doc.IsValidObject ? doc.CreationGUID : Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the document identified by
    /// <paramref name="expected"/>.
    ///
    /// An unreadable identity on EITHER side returns false, and that direction is deliberate:
    /// the thing being guarded is selecting element ids in the wrong model, where ids are
    /// unique per document and would light up unrelated elements. Refusing when the answer is
    /// unknown costs the user a retry; guessing wrong costs them trust in the tool.
    /// </summary>
    public static bool Matches(Document? candidate, Guid expected)
    {
        if (expected == Guid.Empty) return false;

        var actual = Of(candidate);

        if (actual == Guid.Empty)
        {
            Log.Warn("QA: the active document has no readable CreationGUID; " +
                     "treating it as a different model.");
            return false;
        }

        return actual == expected;
    }
}
