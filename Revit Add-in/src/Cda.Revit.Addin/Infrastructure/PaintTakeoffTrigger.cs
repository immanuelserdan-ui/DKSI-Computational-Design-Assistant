using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Answers one question for anything that needs to react after the vendor's "Painted Surface
/// Area" takeoff runs: did this DocumentChanged commit just place a fresh batch of its carrier
/// DirectShapes?
///
/// WHY NOT MATCH ON THE TRANSACTION NAME
///   That was the first version, matching "Painted Surface Area takeoff" against
///   GetTransactionNames(). It worked against the source in this repository and failed
///   silently in production: the DLL actually loaded on this machine is a rebuilt override at
///   %LocalAppData%\Cda\RevitAddin\takeoff-override\PaintedMaterialTakeoff.dll - see
///   RibbonBuilder.OverridePath - and a byte-level check of that assembly found no occurrence
///   of the string "Painted Surface Area takeoff" anywhere in it.
///
/// WHY TWO SIGNALS, NOT JUST ApplicationId
///   The second version matched only DirectShape.ApplicationId == "PaintedMaterialTakeoff" -
///   SegmentElementWriter.Write's own stamp, per its source - and it ALSO never fired against
///   the live model, confirmed by a full re-run producing zero log activity from either
///   consumer. ApplicationId cannot be read back through the tooling available to verify it
///   live, so whether this override build actually sets it is unconfirmed either way.
///
///   What CAN be confirmed live, against the actual carriers in the actual model, is their
///   DirectShapeType name: querying them directly returned typeName "Paint Takeoff Segment" on
///   every one, matching SegmentElementWriter.EnsureType's own hardcoded name exactly. That is
///   now checked as well, and either signal firing is enough - between an unverifiable stamp
///   and a directly-observed type name, there is no reason to depend on only one.
///
/// IF THIS STILL DOES NOT FIRE
///   OnAdded below logs every commit that adds a Generic Model DirectShape, matched or not,
///   with its ApplicationId and type name - so the next run answers definitively rather than
///   requiring a fourth guess.
/// </summary>
internal static class PaintTakeoffTrigger
{
    private const string ApplicationId = "PaintedMaterialTakeoff";
    private const string CarrierTypeName = "Paint Takeoff Segment";

    /// <summary>
    /// True when this commit added at least one DirectShape that is either stamped with the
    /// vendor's ApplicationId or typed "Paint Takeoff Segment" - i.e. the takeoff just placed a
    /// fresh batch of carriers.
    /// </summary>
    public static bool JustPlacedCarriers(DocumentChangedEventArgs e, Document doc)
    {
        try
        {
            var addedGenericModels = 0;

            foreach (var id in e.GetAddedElementIds())
            {
                if (doc.GetElement(id) is not DirectShape shape) continue;

                bool isGenericModel;
                try { isGenericModel = shape.Category?.Id.Value == (long)BuiltInCategory.OST_GenericModel; }
                catch { isGenericModel = false; }

                if (!isGenericModel) continue;

                addedGenericModels++;

                string appId;
                try { appId = shape.ApplicationId; }
                catch { appId = string.Empty; }

                string typeName;
                try { typeName = doc.GetElement(shape.GetTypeId())?.Name ?? string.Empty; }
                catch { typeName = string.Empty; }

                var matched = string.Equals(appId, ApplicationId, StringComparison.Ordinal) ||
                              string.Equals(typeName, CarrierTypeName, StringComparison.Ordinal);

                if (matched)
                {
                    Log.Debug($"PaintTakeoffTrigger: matched on element {id.Value} " +
                              $"(ApplicationId='{appId}', TypeName='{typeName}').");
                    return true;
                }

                // NOT A MATCH, BUT WORTH SEEING. If the takeoff really did just run and neither
                // signal caught it, this is the line that says what the real ApplicationId and
                // type name actually were, instead of a third silent failure.
                Log.Debug($"PaintTakeoffTrigger: added Generic Model {id.Value} matched neither " +
                          $"signal (ApplicationId='{appId}', TypeName='{typeName}').");
            }

            if (addedGenericModels == 0)
            {
                var addedCount = e.GetAddedElementIds().Count;
                if (addedCount > 0)
                    Log.Debug($"PaintTakeoffTrigger: {addedCount} element(s) added this commit, " +
                              "none of them a Generic Model DirectShape.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check whether the paint takeoff just ran: {ex.Message}");
        }

        return false;
    }
}
