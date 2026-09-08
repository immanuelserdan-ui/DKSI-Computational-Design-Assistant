using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Fills the carriers' "Paint Type" parameter with the TYPE NAME of the wall, floor or ceiling
/// each row was measured on - "YV_Beton - 200mm", "IV_Mal - 100mm" - so the surface schedules
/// can show a Type column beside the quantity.
///
/// WHY THIS IS NEEDED AT ALL, WHEN THE PARAMETER ALREADY EXISTS. "Paint Type" is bound to the
/// carriers and is exactly the right field for this - FinishSettings.PaintTypeParameter
/// documents it as "the TYPE of the element the row was measured on", and this add-in's own
/// PaintTakeoffBuilder writes it on every row it places. But the carriers in this model are
/// placed by the VENDOR takeoff (PaintedMaterialTakeoff), which never writes that parameter.
/// Measured 2026-09-02: carrier 29338529 carries 'Paint Host Id' = 29307636 and a 'Paint
/// Segment' of "YV_Beton - 200mm #29307636 - Face 0.0 R2", and an EMPTY 'Paint Type'. The type
/// name is sitting there in two places and in neither of them can a schedule column read it.
///
/// WHY NOT PARSE 'Paint Segment'. Its leading text is the type name today, and it is a display
/// string the vendor composes - a build that changes the separator, or a type name containing
/// the separator, turns a parse into silently wrong data. 'Paint Host Id' is an id, it is
/// written for exactly this kind of lookup, and resolving it through the document gives Revit's
/// own answer rather than a guess about someone else's formatting.
///
/// NEVER OVERWRITES A VALUE THAT IS ALREADY THERE. A carrier placed by this add-in's own
/// builder already holds the right type, and a person may have corrected one by hand. Only an
/// EMPTY 'Paint Type' is filled, so this converges and then does nothing on every later run.
///
/// SAME TRIGGER AND SAME REASONING AS <see cref="PaintRoomOverrideAutoReapply"/>: DocumentChanged
/// after the vendor's own transaction, not a call into its command. See that class for why
/// reaching into the vendor's API would break on its next build.
/// </summary>
internal static class PaintHostTypeStamp
{
    public static void Register(UIControlledApplication application)
    {
        try
        {
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint host type stamp: could not register ({ex.Message}).");
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
            Log.Warn($"Paint host type stamp: could not unregister ({ex.Message}).");
        }
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        try
        {
            var doc = e.GetDocument();
            if (doc is null || doc.IsFamilyDocument || doc.IsLinked) return;

            if (!PaintTakeoffTrigger.JustPlacedCarriers(e, doc)) return;

            // CAPTURED HERE, NOT RE-QUERIED FROM INSIDE THE POSTED WORK. DocumentChangedEventArgs
            // is only guaranteed valid for the duration of this handler, and by the time
            // RevitTaskQueue's callback runs it is on a later tick entirely.
            //
            // WHY THIS IS THE COMPLETE SET OF CARRIERS TO STAMP, NOT A SUBSET OF IT. The
            // vendor's own source confirms every "Painted Surface Area" run deletes ALL of its
            // previously-placed carrier geometry before creating fresh carriers -
            // SegmentElementWriter.DeletePrevious()/DeleteAllAddinGeometry() in
            // ../../installer/PaintTakeoff/PaintedMaterialTakeoff.source.cs. That means every
            // carrier that exists in the document once this commit lands was added IN THIS
            // COMMIT - GetAddedElementIds() is not an approximation of "every carrier", it IS
            // every carrier, without also re-walking whatever real furniture, equipment or
            // other Generic Models the project happens to contain. A whole-category
            // FilteredElementCollector over OST_GenericModel - the previous approach - paid to
            // re-scan and parameter-check every one of those on every single takeoff run, for
            // no correctness this narrower set does not already give in full.
            var addedIds = e.GetAddedElementIds().ToList();

            RevitTaskQueue.Post("Stamp paint host type", app =>
            {
                var target = app.ActiveUIDocument?.Document;

                // See PaintRoomOverrideAutoReapply for why this is not a reference comparison.
                if (!DocumentIdentity.IsSame(target, doc))
                {
                    Log.Debug("Paint host type stamp: active document " +
                              $"('{target?.Title}') did not match the one that changed " +
                              $"('{doc.Title}'); skipped.");
                    return;
                }

                var stamped = 0;
                var unresolved = 0;

                Transactions.Run(target, "DKSI paint host type: stamp",
                    () => stamped = Stamp(target!, addedIds, out unresolved), swallowWarnings: true);

                if (stamped > 0 || unresolved > 0)
                {
                    Log.Info($"Paint host type: {stamped} carrier(s) stamped" +
                             (unresolved > 0
                                 ? $", {unresolved} could not be resolved to a host type."
                                 : "."));
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Paint host type stamp ignored a document change: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the host's type name onto every one of <paramref name="candidateIds"/> that is a
    /// carrier with an empty "Paint Type". Returns how many were written;
    /// <paramref name="unresolved"/> counts those with a host id that no longer resolves - a
    /// deleted wall, most often - which are left blank rather than guessed.
    /// </summary>
    private static int Stamp(Document doc, IReadOnlyList<ElementId> candidateIds, out int unresolved)
    {
        var settings = new FinishSettings();
        var stamped = 0;
        unresolved = 0;

        foreach (var id in candidateIds)
        {
            // Not every added id is necessarily a carrier - a takeoff run's transaction could
            // in principle carry other additions alongside its carriers. Filtered here, per
            // element, exactly as the whole-category scan this replaced filtered by category
            // before iterating - same check, just against a narrower, already-correct list
            // instead of the whole document.
            if (doc.GetElement(id) is not { } carrier) continue;
            if (carrier.Category?.Id.Value != (long)BuiltInCategory.OST_GenericModel) continue;

            try
            {
                var typeParameter = ParameterHelper.Find(carrier, settings.PaintTypeParameter);
                if (typeParameter is null || typeParameter.IsReadOnly) continue;

                // ONLY THE EMPTY ONES. A carrier this add-in placed already holds the right
                // answer, and a hand-corrected one must not be overwritten by a derived value.
                if (!string.IsNullOrWhiteSpace(typeParameter.AsString())) continue;

                var hostText = ParameterHelper.Find(carrier, settings.PaintHostParameter)?.AsString();

                // No host id at all means this is not a takeoff carrier - a real Generic Model
                // that happens to share the category. Skipped silently and NOT counted as
                // unresolved: nothing about it is wrong.
                if (string.IsNullOrWhiteSpace(hostText)) continue;

                var typeName = HostTypeName(doc, hostText);

                if (string.IsNullOrEmpty(typeName))
                {
                    unresolved++;
                    continue;
                }

                typeParameter.Set(typeName);
                stamped++;
            }
            catch
            {
                // One carrier must not cost the rest of the pass.
                unresolved++;
            }
        }

        return stamped;
    }

    /// <summary>
    /// The type name of the element the id names, falling back to the element's own name when
    /// it has no type. Deliberately the same rule as RoomFinishCalculator.HostTypeName, so a
    /// carrier stamped here and a row written by this add-in's own builder agree.
    /// </summary>
    private static string HostTypeName(Document doc, string hostText)
    {
        if (!long.TryParse(hostText, out var hostId) || hostId < 0) return string.Empty;

        try
        {
            var element = doc.GetElement(new ElementId(hostId));
            if (element is null) return string.Empty;

            var typeName = doc.GetElement(element.GetTypeId())?.Name;
            return string.IsNullOrWhiteSpace(typeName) ? element.Name : typeName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
