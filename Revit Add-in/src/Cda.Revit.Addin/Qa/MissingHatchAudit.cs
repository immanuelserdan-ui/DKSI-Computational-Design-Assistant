using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Qa;

/// <summary>Why a surface in the room's enclosure contributed no measured paint area.</summary>
public enum MissingReason
{
    /// <summary>
    /// The element encloses the room but Revit does not consider it to BOUND it - Room
    /// Bounding switched off, or a separation line doing the bounding instead.
    ///
    /// The serious one. Paint on this face reaches no room parameter at all, so if it really
    /// does face the room, the room's finish AND paint areas are both under-reporting.
    /// </summary>
    NotRoomBounding,

    /// <summary>
    /// It bounds the room and its faces carry no paint. Legitimate where the finish is a
    /// layer material rather than applied paint - and a real omission where it is not.
    /// </summary>
    NotPainted,

    /// <summary>
    /// Painted faces were found but the boolean clip failed, so the engine fell back to an
    /// arithmetic measurement. The number is in the parameter; only the geometry is missing.
    /// The least alarming of the three, and the only one that is a tooling limit rather than
    /// a fact about the model.
    /// </summary>
    ClipFailed,
}

/// <summary>One surface flagged for having no hatch.</summary>
public sealed class MissingHatchFinding
{
    public required ElementId Id { get; init; }
    public required MissingReason Reason { get; init; }
    public required string Description { get; init; }
}

/// <summary>
/// Finds surfaces that SHOULD carry a paint hatch and do not, and paints them red.
///
/// WHY AN ELEMENT OVERRIDE IS THE RIGHT TOOL HERE, HAVING BEEN THE WRONG ONE BEFORE
///   The paint overlay had to abandon <see cref="View.SetElementOverrides"/> because it
///   colours a whole element when the thing being shown was a specific measured region. This
///   check is the exact opposite case. There IS no region - that is the entire finding - so
///   there is nothing to draw and no geometry to be imprecise about. "This ELEMENT
///   contributed nothing" is an element-level statement, and an element-level override says
///   it exactly.
///
/// THE DISTINCTION THAT MAKES THIS WORTH HAVING
///   A blank surface in the overlay has three quite different causes and they need opposite
///   responses. Two of them are facts about the model that somebody has to fix; one is a
///   limit of the measurement. Flagging all three the same colour would be a checklist that
///   cries wolf, so the reason travels with the finding and is reported per element.
/// </summary>
public static class MissingHatchAudit
{
    /// <summary>Below this a surface counts as unmeasured, ~0.01 m².</summary>
    private const double AreaTolerance = 0.1;

    public sealed class Result
    {
        public required IReadOnlyList<MissingHatchFinding> Findings { get; init; }
        public int Flagged { get; init; }
        public required string Message { get; init; }
    }

    /// <summary>
    /// Compares the room's enclosure against what was actually measured, and overrides the
    /// shortfall in red. Opens its own transaction.
    /// </summary>
    public static Result Run(UIDocument uiDoc, RoomFinishSet set, PaintExtractResult paint)
    {
        var doc = uiDoc.Document;
        var view = uiDoc.ActiveGraphicalView;

        var findings = Detect(doc, set, paint);

        if (findings.Count == 0)
        {
            return new Result
            {
                Findings = findings,
                Flagged = 0,
                Message = "No missing hatches: every enclosing surface contributed a measured paint area.",
            };
        }

        var flagged = 0;

        if (view is not null && !view.IsTemplate)
        {
            try
            {
                Transactions.Run(doc, "QA - flag missing hatches", () =>
                {
                    var patternId = RoomHatch.EnsureMissingPattern(doc);
                    if (patternId == ElementId.InvalidElementId) return;

                    var overrides = RoomHatch.Build(patternId, RoomHatch.MissingColour);

                    // Opaque, unlike the measured hatch. A warning you can see through reads
                    // as decoration; this one has to interrupt.
                    overrides.SetSurfaceTransparency(0);

                    foreach (var finding in findings)
                    {
                        try
                        {
                            view.SetElementOverrides(finding.Id, overrides);
                            flagged++;
                        }
                        catch
                        {
                            // View template controls graphics for this element; it stays in
                            // the report even though it cannot be coloured.
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"QA missing-hatch flagging failed: {ex.Message}");
            }
        }

        var byReason = findings
            .GroupBy(f => f.Reason)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {Describe(g.Key)}");

        Log.Info($"QA missing hatch: {findings.Count} finding(s), {flagged} flagged in the view.");

        return new Result
        {
            Findings = findings,
            Flagged = flagged,
            Message =
                $"{findings.Count} enclosing surface(s) have NO measured paint area and are " +
                $"flagged in red: {string.Join(", ", byReason)}. " +
                Advice(findings) +
                (flagged < findings.Count
                    ? $" {findings.Count - flagged} could not be coloured - a view template " +
                      "controls their graphics - but they are listed above."
                    : string.Empty),
        };
    }

    // ------------------------------------------------------------------- detection

    /// <summary>
    /// Every element in the enclosure whose measured paint area is effectively zero, with
    /// the reason attached.
    ///
    /// The order of the tests is the order of severity, and it matters: an element that is
    /// not room-bounding is ALSO not painted as far as the room is concerned, so testing for
    /// paint first would label the serious case with the mild reason.
    /// </summary>
    private static List<MissingHatchFinding> Detect(
        Document doc, RoomFinishSet set, PaintExtractResult paint)
    {
        var findings = new List<MissingHatchFinding>();

        foreach (var (ids, kind) in new[]
                 {
                     ((IReadOnlyList<ElementId>)set.Walls, "Wall"),
                     ((IReadOnlyList<ElementId>)set.Floors, "Floor"),
                     ((IReadOnlyList<ElementId>)set.Ceilings, "Ceiling/soffit"),
                 })
        {
            foreach (var id in ids.Distinct())
            {
                if (paint.AreaOfHost(id) > AreaTolerance) continue;

                var reason =
                    !paint.BoundingHosts.Contains(id.Value) ? MissingReason.NotRoomBounding
                    : paint.ClipFailedHosts.Contains(id.Value) ? MissingReason.ClipFailed
                    : MissingReason.NotPainted;

                findings.Add(new MissingHatchFinding
                {
                    Id = id,
                    Reason = reason,
                    Description = $"{kind} · {TypeName(doc, id)} [{id.Value}]",
                });
            }
        }

        return findings;
    }

    // ---------------------------------------------------------------------- wording

    private static string Describe(MissingReason reason) => reason switch
    {
        MissingReason.NotRoomBounding => "not room-bounding",
        MissingReason.ClipFailed => "measured arithmetically (no geometry to draw)",
        _ => "carrying no paint",
    };

    /// <summary>
    /// What to do about it, led by the most serious reason present. One sentence, because a
    /// paragraph in a notes panel does not get read.
    /// </summary>
    private static string Advice(IReadOnlyList<MissingHatchFinding> findings)
    {
        if (findings.Any(f => f.Reason == MissingReason.NotRoomBounding))
        {
            return "RED ON A NON-BOUNDING SURFACE IS THE ONE TO LOOK AT: if that face really " +
                   "does front this room, the room's finish and paint areas are both " +
                   "under-reporting it. Select it and check Room Bounding in Properties.";
        }

        if (findings.Any(f => f.Reason == MissingReason.NotPainted))
        {
            return "These bound the room but carry no painted material, so they are correctly " +
                   "absent from Paint Area - and correctly present in FINISH Area, which is a " +
                   "different number. Red here means 'confirm the finish is meant to be " +
                   "unpainted', not 'this is broken'.";
        }

        return "The area IS in the parameter; only its geometry could not be rebuilt, so " +
               "there is nothing to draw. No model change is needed.";
    }

    private static string TypeName(Document doc, ElementId id)
    {
        try
        {
            var element = doc.GetElement(id);
            if (element is null) return "(deleted)";

            return doc.GetElement(element.GetTypeId())?.Name ?? element.Name;
        }
        catch
        {
            return "(unreadable)";
        }
    }
}
