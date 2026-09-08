using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Per-document state for the automations that bank work between events.
///
/// THE BUG THIS EXISTS TO REMOVE, STATED PLAINLY
///   Every automation here banks ElementIds in a DocumentChanged handler (read-only, cannot
///   write) and drains them later from an ExternalEvent callback, which takes its document from
///   UIApplication.ActiveUIDocument. Those two moments are separated by an idle debounce - eight
///   seconds by default - and the user is free to switch documents in between.
///
///   Held in a plain static HashSet, the queue has no idea which document it came from. An
///   ElementId is only meaningful inside the document that minted it, so a queue banked against
///   project A and drained against project B resolves to whatever elements happen to carry those
///   ids in B: rooms whose upper limits are then raised, walls that are then void-cut, finish
///   parameters overwritten from a scope computed for a different building. And because the
///   drain clears the queue on the way out, A's real work is discarded unrun.
///
///   Refusing to act when the document does not match would fix the corruption but not the loss:
///   the second document's work would sit banked forever. Keying by document fixes both - each
///   document keeps its own queue, and each is drained when that document is the one in front of
///   the user.
///
/// <typeparamref name="T"/> is created on demand, so a document that never queues anything never
/// allocates. <see cref="Forget"/> is called from DocumentClosing so the map cannot grow across a
/// long session of opening and closing files.
/// </summary>
internal sealed class DocumentScoped<T> where T : new()
{
    private readonly Dictionary<string, T> _byDocument = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This document's state, created empty on first use. Returns a detached instance for a
    /// document with no usable key rather than throwing - the caller's work is then dropped,
    /// which is the correct outcome for a document that cannot be identified.
    /// </summary>
    public T For(Document? doc)
    {
        var key = DocumentIdentity.KeyOf(doc);
        if (key.Length == 0) return new T();

        if (!_byDocument.TryGetValue(key, out var state))
            _byDocument[key] = state = new T();

        return state;
    }

    /// <summary>
    /// True when this document has state banked. Distinct from <see cref="For"/> because asking
    /// the question must not allocate an empty entry for every document that is merely looked at.
    /// </summary>
    public bool Has(Document? doc)
    {
        var key = DocumentIdentity.KeyOf(doc);
        return key.Length > 0 && _byDocument.ContainsKey(key);
    }

    /// <summary>Drops this document's state. Called when its work has been done.</summary>
    public void Forget(Document? doc)
    {
        var key = DocumentIdentity.KeyOf(doc);
        if (key.Length > 0) _byDocument.Remove(key);
    }
}

/// <summary>
/// A single mutable flag, so <see cref="DocumentScoped{T}"/> can carry per-document booleans and
/// timestamps without a dictionary of its own for each one.
/// </summary>
internal sealed class DocumentFlags
{
    /// <summary>Finish areas are owed a recalculation in this document.</summary>
    public bool FinishDirty { get; set; }

    /// <summary>This document was just opened and owes the opening resolvers a forced pass.</summary>
    public bool OpenedPending { get; set; }

    /// <summary>The opening resolvers (Udvendig, lining) are owed a pass in this document.</summary>
    public bool OpeningDirty { get; set; }

    /// <summary>A whole-model casework sweep is owed in this document.</summary>
    public bool CaseworkSweepOwed { get; set; }

    /// <summary>When this document last saw a real, uncontested edit. Drives the idle debounce.</summary>
    public DateTime LastChangeUtc { get; set; } = DateTime.MinValue;
}
