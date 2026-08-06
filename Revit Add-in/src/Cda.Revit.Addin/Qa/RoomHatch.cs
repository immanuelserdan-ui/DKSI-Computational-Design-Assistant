using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Qa;

/// <summary>
/// Puts a temporary hatch over the surfaces a room's finish areas were measured from.
///
/// WHY VIEW OVERRIDES AND NOT PAINT
///   The obvious way to colour a room's wall faces is <c>Document.Paint</c>, which applies a
///   material to one face of one element. It is also the wrong tool here, and expensively so:
///   painting is a MODEL change. It writes to the element, it travels to every view and every
///   consultant, it shows up in a material takeoff, and undoing it across a hundred faces is
///   a job. A QA overlay must not leave a trace in the model, and paint leaves the worst kind
///   - one that looks like a design decision.
///
///   <see cref="View.SetElementOverrides"/> is view-specific graphics. It changes nothing
///   about the element, nothing about any other view, and nothing in any schedule. Clearing
///   it restores exactly what was there before.
///
/// WHAT IS WRITTEN TO THE MODEL, HONESTLY
///   One thing: a fill pattern definition named <see cref="PatternName"/>, created once and
///   reused forever. A pattern is a style, not geometry - the same kind of object as a line
///   style - and it has to exist somewhere for any hatch to reference it. Nothing else this
///   class does survives outside the view it was applied in.
///
/// THE LIMITATION TO KNOW BEFORE REPORTING IT
///   An override applies to an ELEMENT, not to a face. Hatching a wall hatches the whole
///   wall, both sides and full length - the same fact that makes isolation show the part
///   running into the next room. Revit offers no per-face override; the per-face mechanism IS
///   paint, and that is the model change this deliberately avoids. The section box is what
///   makes it read correctly.
/// </summary>
public static class RoomHatch
{
    /// <summary>
    /// The pattern's name IS the marker that identifies our overrides later.
    ///
    /// This is what makes Clear a single command that works in a session that did not apply
    /// the hatch: rather than remembering what was overridden - which a list in memory forgets
    /// the moment Revit closes - Clear looks for elements whose override references this
    /// pattern. The model carries its own record.
    /// </summary>
    public const string PatternName = "DKSI QA Temporary Hatch";

    /// <summary>Diagonal lines at 45 degrees, ~3 mm apart on paper.</summary>
    private const double PatternAngleDegrees = 45.0;
    private const double PatternSpacingMm = 3.0;

    /// <summary>
    /// Deliberately washed out. A QA overlay competing with documentation graphics gets
    /// switched off; one that reads as an annotation gets used. Distinct hues per surface so
    /// a glance says which of the three numbers a face contributed to.
    /// </summary>
    public static readonly Color WallColour = new(90, 140, 200);
    public static readonly Color FloorColour = new(110, 170, 110);
    public static readonly Color CeilingColour = new(210, 160, 80);

    /// <summary>The colour a given surface is drawn in, so overlays and hatches agree.</summary>
    public static Color ColourOf(SurfaceKind kind) => kind switch
    {
        SurfaceKind.Floor => FloorColour,
        SurfaceKind.Ceiling => CeilingColour,
        _ => WallColour,
    };

    /// <summary>Surface transparency, 0-100. High enough to read as provisional.</summary>
    private const int Transparency = 35;

    public sealed class Result
    {
        public int Applied { get; init; }
        public int Skipped { get; init; }
        public required string Message { get; init; }
    }

    // -------------------------------------------------------------------- pattern

    /// <summary>
    /// The hatch pattern, found or created. Caller owns the transaction.
    ///
    /// CREATED RATHER THAN LOOKED UP BY NAME. Reaching for a built-in like "Diagonal up"
    /// looks simpler and breaks on this project: pattern names are localised, so a Danish
    /// installation has different ones, and a template may not carry the pattern at all.
    /// Creating our own is deterministic, needs no fallback list, and cannot collide with a
    /// pattern the office uses for real documentation.
    /// </summary>
    public static ElementId EnsurePattern(Document doc)
    {
        try
        {
            var existing = FillPatternElement.GetFillPatternElementByName(
                doc, FillPatternTarget.Drafting, PatternName);

            if (existing is not null) return existing.Id;
        }
        catch
        {
            // Lookup failed; fall through and create.
        }

        try
        {
            var pattern = new FillPattern(
                PatternName,
                FillPatternTarget.Drafting,
                FillPatternHostOrientation.ToView,
                PatternAngleDegrees * Math.PI / 180.0,
                Measure.FromMillimetres(PatternSpacingMm));

            return FillPatternElement.Create(doc, pattern).Id;
        }
        catch (Exception ex)
        {
            Log.Warn($"QA hatch: could not create fill pattern: {ex.Message}");
            return ElementId.InvalidElementId;
        }
    }

    // ---------------------------------------------------------------------- apply

    /// <summary>
    /// Hatches the room's walls, floors and ceilings in the active view. Opens its own
    /// transaction.
    /// </summary>
    public static Result Apply(UIDocument uiDoc, RoomFinishSet set)
    {
        var view = uiDoc.ActiveGraphicalView;

        if (view is null || view.IsTemplate)
        {
            return new Result
            {
                Applied = 0,
                Skipped = 0,
                Message = "Hatch skipped: no active graphical view.",
            };
        }

        var doc = uiDoc.Document;
        var applied = 0;
        var skipped = 0;

        try
        {
            Transactions.Run(doc, "QA - hatch room surfaces", () =>
            {
                var patternId = EnsurePattern(doc);

                if (patternId == ElementId.InvalidElementId)
                    throw new InvalidOperationException("the fill pattern could not be created.");

                foreach (var (ids, colour) in new[]
                         {
                             ((IReadOnlyList<ElementId>)set.Walls, WallColour),
                             ((IReadOnlyList<ElementId>)set.Floors, FloorColour),
                             ((IReadOnlyList<ElementId>)set.Ceilings, CeilingColour),
                         })
                {
                    var overrides = Build(patternId, colour);

                    foreach (var id in ids)
                    {
                        try
                        {
                            view.SetElementOverrides(id, overrides);
                            applied++;
                        }
                        catch
                        {
                            // An element not present in this view, or one the view's template
                            // controls, refuses the override. One refusal must not cost the
                            // other forty.
                            skipped++;
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"QA hatch failed: {ex.Message}");

            return new Result
            {
                Applied = 0,
                Skipped = 0,
                Message = $"Hatch failed: {ex.Message}",
            };
        }

        Log.Info($"QA hatch: {applied} element(s) hatched in view {view.Name}, {skipped} skipped.");

        return new Result
        {
            Applied = applied,
            Skipped = skipped,
            Message =
                $"Hatched {applied} surface(s) in '{view.Name}' - walls blue, floor green, " +
                $"ceiling amber, {Transparency}% transparent." +
                (skipped > 0
                    ? $" {skipped} element(s) would not take an override; a view template " +
                      "controlling visibility/graphics is the usual reason."
                    : string.Empty) +
                " View-only: nothing was written to the elements. Clear scope removes it.",
        };
    }

    /// <summary>
    /// One override: our pattern in front, a wash of colour behind, and transparency.
    ///
    /// The BACKGROUND pattern is set as well as the foreground, and it is what makes the
    /// surface read as tinted rather than as thin lines on whatever was underneath. Without
    /// it a dark material shows straight through the gaps between the hatch lines and the
    /// overlay is invisible on exactly the surfaces someone is checking.
    /// </summary>
    public static OverrideGraphicSettings Build(ElementId patternId, Color colour)
    {
        var overrides = new OverrideGraphicSettings();

        overrides.SetSurfaceForegroundPatternId(patternId);
        overrides.SetSurfaceForegroundPatternColor(colour);
        overrides.SetSurfaceForegroundPatternVisible(true);

        overrides.SetSurfaceBackgroundPatternId(patternId);
        overrides.SetSurfaceBackgroundPatternColor(colour);
        overrides.SetSurfaceBackgroundPatternVisible(true);

        // Cut faces too, so the hatch survives being sliced by the section box. A room
        // isolated in 3D is almost always cut somewhere, and an overlay that vanishes at the
        // cut looks like it failed.
        overrides.SetCutForegroundPatternId(patternId);
        overrides.SetCutForegroundPatternColor(colour);
        overrides.SetCutForegroundPatternVisible(true);

        overrides.SetSurfaceTransparency(Transparency);
        overrides.SetProjectionLineColor(colour);

        return overrides;
    }

    // ---------------------------------------------------------------------- clear

    /// <summary>
    /// Removes every hatch this tool applied in the active view, and nothing else.
    ///
    /// FOUND BY SIGNATURE, NOT BY MEMORY. A list of "what I overrode" held in a field is
    /// wrong in all the ways that matter: it is empty in a session that did not apply the
    /// hatch, it is stale after an undo, and it strands overrides in a view the user has
    /// since left. Asking each element whether its override references OUR pattern is
    /// authoritative, needs no bookkeeping, and cannot clear an override somebody else set.
    /// </summary>
    public static int Clear(UIDocument uiDoc)
    {
        var view = uiDoc.ActiveGraphicalView;
        if (view is null || view.IsTemplate) return 0;

        var doc = uiDoc.Document;

        ElementId patternId;

        try
        {
            var pattern = FillPatternElement.GetFillPatternElementByName(
                doc, FillPatternTarget.Drafting, PatternName);

            // No pattern means nothing was ever hatched in this model.
            if (pattern is null) return 0;

            patternId = pattern.Id;
        }
        catch
        {
            return 0;
        }

        var cleared = 0;

        try
        {
            // Scoped to what the VIEW shows, not the whole model: overrides only exist per
            // view, so anything not in this view cannot be carrying one from it.
            var candidates = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .ToElementIds();

            var blank = new OverrideGraphicSettings();

            var ours = new List<ElementId>();

            foreach (var id in candidates)
            {
                try
                {
                    var current = view.GetElementOverrides(id);

                    if (current.SurfaceForegroundPatternId == patternId ||
                        current.CutForegroundPatternId == patternId)
                        ours.Add(id);
                }
                catch
                {
                    // Element cannot report overrides; it cannot be carrying ours either.
                }
            }

            if (ours.Count == 0) return 0;

            Transactions.Run(doc, "QA - clear room hatch", () =>
            {
                foreach (var id in ours)
                {
                    try
                    {
                        view.SetElementOverrides(id, blank);
                        cleared++;
                    }
                    catch
                    {
                        // Refused the reset; leave it rather than abort the rest.
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"QA hatch clear failed: {ex.Message}");
        }

        if (cleared > 0) Log.Info($"QA hatch: cleared {cleared} override(s) in view {view.Name}.");

        return cleared;
    }
}
