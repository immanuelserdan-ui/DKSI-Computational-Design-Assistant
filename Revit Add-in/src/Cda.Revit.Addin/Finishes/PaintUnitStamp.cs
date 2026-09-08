using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Fills the carriers' "enh" parameter with the literal text "m²", so the surface
/// schedules can show a unit column beside the quantity without a genuine Calculated Value
/// (Formula) schedule field.
///
/// WHY THIS EXISTS INSTEAD OF A CALCULATED VALUE FIELD. That is what the office reference
/// schedule actually uses - Discipline Common, Type Text, Formula "m²" - and it is the
/// obvious way to show a constant label. But the Revit API exposes no way to create or
/// configure one: <see cref="ScheduleDefinition"/> and <see cref="ScheduleField"/> have no
/// method to set a calculated field's name, formula text, discipline or type - confirmed
/// against RevitAPI.dll's public surface, 2026-09-08, not assumed. A Calculated Value field
/// can only be built by hand, in the Schedule Properties dialog, once per schedule, forever.
///
/// "enh" ALREADY EXISTS FOR THIS. It is an ordinary (non-shared) project parameter, already
/// bound to Generic Models - confirmed live: every carrier already carries an empty "enh"
/// string parameter, the same one Walls and other categories in this template carry. Nothing
/// needed binding; this only needed something to WRITE the constant into it, which the API
/// can do freely because "enh" is a real parameter, not a Calculated Value field.
///
/// NEVER OVERWRITES A VALUE THAT IS ALREADY THERE, same rule and same reason as
/// <see cref="PaintHostTypeStamp"/>: a person may have written something else into one row's
/// "enh" on purpose, and this must converge, not fight them.
///
/// SAME TRIGGER AS <see cref="PaintHostTypeStamp"/>, registered beside it for the same reason:
/// both react to the vendor takeoff placing fresh carriers, DocumentChanged after its own
/// transaction rather than a call into its command. See that class for why reaching into the
/// vendor's API would break on its next build.
///
/// TAKES EFFECT ON THE NEXT TAKEOFF RUN, NOT RETROACTIVELY. Carriers that already exist when
/// this ships get "m²" the next time "Painted Surface Area" places fresh ones - the vendor
/// deletes and recreates every carrier on each run, so that is also the point everything else
/// this add-in stamps (Paint Type, room overrides) refreshes. Running "Surface Schedules"
/// alone does not touch carrier data and will show a blank "enh" column until then.
/// </summary>
internal static class PaintUnitStamp
{
    private const string UnitText = "m²";

    public static void Register(UIControlledApplication application)
    {
        try
        {
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint unit stamp: could not register ({ex.Message}).");
        }
    }

    public static void Unregister(UIControlledApplication application)
    {
        try
        {
            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint unit stamp: could not unregister ({ex.Message}).");
        }
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            if (doc is null || doc.IsFamilyDocument || doc.IsLinked) return;

            if (!PaintTakeoffTrigger.JustPlacedCarriers(e, doc)) return;

            // Captured here, not re-queried inside the posted work - see PaintHostTypeStamp for
            // why DocumentChangedEventArgs cannot be trusted past this handler's own return.
            var addedIds = e.GetAddedElementIds().ToList();

            RevitTaskQueue.Post("Stamp paint unit", app =>
            {
                var target = app.ActiveUIDocument?.Document;

                if (!DocumentIdentity.IsSame(target, doc))
                {
                    Log.Debug("Paint unit stamp: active document " +
                              $"('{target?.Title}') did not match the one that changed " +
                              $"('{doc.Title}'); skipped.");
                    return;
                }

                var stamped = 0;

                Transactions.Run(target, "DKSI paint unit: stamp",
                    () => stamped = Stamp(target!, addedIds), swallowWarnings: true);

                if (stamped > 0)
                    Log.Info($"Paint unit: {stamped} carrier(s) stamped with '{UnitText}'.");
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint unit stamp ignored a document change: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes "m²" onto every one of <paramref name="candidateIds"/> that is a carrier
    /// with an empty "enh". Returns how many were written.
    /// </summary>
    private static int Stamp(Document doc, IReadOnlyList<ElementId> candidateIds)
    {
        var settings = new FinishSettings();
        var stamped = 0;

        foreach (var id in candidateIds)
        {
            // Not every added id is necessarily a carrier - same caveat and same fix as
            // PaintHostTypeStamp.Stamp.
            if (doc.GetElement(id) is not { } carrier) continue;
            if (carrier.Category?.Id.Value != (long)BuiltInCategory.OST_GenericModel) continue;

            try
            {
                var unitParameter = ParameterHelper.Find(carrier, settings.PaintUnitParameter);
                if (unitParameter is null || unitParameter.IsReadOnly) continue;

                if (!string.IsNullOrWhiteSpace(unitParameter.AsString())) continue;

                // Not every Generic Model carrying an empty "enh" is a takeoff row - a real
                // Generic Model that happens to share the category and the parameter. Only one
                // this add-in or the vendor takeoff ever writes tells the two apart: a carrier
                // always has a "Paint Segment", a genuine model element never does.
                var segment = ParameterHelper.Find(carrier, "Paint Segment")?.AsString();
                if (string.IsNullOrWhiteSpace(segment)) continue;

                unitParameter.Set(UnitText);
                stamped++;
            }
            catch
            {
                // One carrier must not cost the rest of the pass.
            }
        }

        return stamped;
    }
}
