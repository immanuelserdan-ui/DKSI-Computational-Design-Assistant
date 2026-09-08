using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// What kind of work the active document/view says is happening, before anyone touches the
/// dropdown - "Family Creation", "Construction Documentation", or the model's own Phase name.
///
/// STRONG vs SOFT SIGNALS. Being in a family editor or on a sheet is not a guess - there is
/// no other kind of work those states represent, so they always win, even over something the
/// user picked by hand a minute ago (see <see cref="TimeTracker.ApplyAutoTask"/>). Reading the
/// view's own Phase is a much softer signal - most views are on some Phase whether or not
/// anyone is doing phase-relevant work - so it only fills in a blank, never overwrites a
/// choice the user actually made.
/// </summary>
internal static class TaskDetection
{
    /// <summary>
    /// The fixed list for the dropdown. Free text is deliberately not offered - six
    /// consistent labels are what makes "time by task" summable across an office, and a
    /// stray typo'd category is invisible in a rollup until someone goes looking for it.
    /// </summary>
    public static readonly IReadOnlyList<string> Presets =
    [
        "Schematic Design", "Design Development", "Construction Documentation",
        "Clash Detection", "Family Creation", "General Modeling",
    ];

    public const string AutoDetected = "Auto-Detected";
    public const string ManualOverride = "Manual Override";

    /// <summary>
    /// One auto-detection pass for a newly activated document/view.
    /// </summary>
    /// <param name="phase">The detected task/phase label.</param>
    /// <param name="isStrong">
    /// True when this reading must win even over a standing manual override (family editor,
    /// sheet); false when it should only apply while nothing has been manually chosen yet.
    /// </param>
    public static (string Phase, bool IsStrong) Detect(Document? doc, View? view)
    {
        if (doc is not null && doc.IsFamilyDocument)
            return ("Family Creation", true);

        if (view is ViewSheet)
            return ("Construction Documentation", true);

        var nativePhase = ReadViewPhase(doc, view);
        if (nativePhase.Length > 0)
            return (nativePhase, false);

        return ("General Modeling", false);
    }

    /// <summary>
    /// The project Phase a view is set to (Existing, New Construction, ...) via its own
    /// VIEW_PHASE parameter - Revit's native phasing, not the six-item task list above. Not
    /// every view carries one (schedules, legends, and some 3D views do not), so a miss here
    /// is ordinary, not an error.
    /// </summary>
    private static string ReadViewPhase(Document? doc, View? view)
    {
        if (doc is null || view is null) return string.Empty;

        try
        {
            var parameter = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (parameter is null || parameter.StorageType != StorageType.ElementId) return string.Empty;

            var phaseId = parameter.AsElementId();
            if (phaseId is null || phaseId == ElementId.InvalidElementId) return string.Empty;

            return (doc.GetElement(phaseId) as Phase)?.Name ?? string.Empty;
        }
        catch
        {
            // Some view types (schedules, legends) throw rather than returning null for a
            // parameter that does not apply to them.
            return string.Empty;
        }
    }
}
