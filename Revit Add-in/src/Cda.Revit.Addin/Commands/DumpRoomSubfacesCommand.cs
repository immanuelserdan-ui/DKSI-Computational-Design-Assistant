using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Dumps every room boundary subface, and says for each room whether the wall band a slab
/// opening exposes is reachable by the measuring engine at all - and, separately, whether any
/// nearby stair carries a painted face that is invisible to every pass regardless.
///
/// WHY THIS IS A COMMAND AND NOT A LOG LINE IN THE ENGINE
///   Because the engine cannot see what it never received. The whole question is what
///   SpatialElementGeometryCalculator HANDS BACK in the band between a bounding floor's
///   soffit and its top - and by the time RoomFinishCalculator has routed a subface into a
///   bucket, the distinction between "arrived and was declined" and "never arrived" is gone.
///   This reads the calculator's output before anything is done with it.
///
/// THE STAIR-SIDE FINDING IS A DIFFERENT QUESTION WEARING THE SAME SYMPTOM. A raking closure
/// panel beside a flight - real, painted wall area the takeoff reports as zero - was first
/// told apart from the slab-band case by a screenshot; the two look identical from a photo and
/// are opposite kinds of defect. OST_Stairs is outside the room-bounding categories entirely
/// (see the remarks on SubfaceDump.StairSideHit), so this walks every nearby stair's own
/// geometry directly rather than waiting on a subface Revit will never produce.
///
/// WHOLE MODEL BY DEFAULT, like every other automation here, because a band loss is a
/// per-opening defect and the opening you did not think to select is the one worth finding.
/// Selecting rooms first narrows it, for when you are iterating on one stair.
///
/// TransactionMode.ReadOnly, and that is the safety argument. It cannot write to the model
/// even if its geometry is wrong - the right property for an instrument whose whole job is
/// to be believed about which of two fixes to build.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class DumpRoomSubfacesCommand : CommandBase
{
    protected override string CommandName => "Room Subface Dump";

    protected override Result Run(CommandContext ctx)
    {
        var doc = ctx.Document;

        var scope = SelectedRooms(ctx);

        var result = new SubfaceDump(doc).Run(scope);

        if (result.Rooms.Count == 0)
        {
            TaskDialog.Show(CommandName,
                "No placed rooms to examine" +
                (scope is { Count: > 0 } ? " in the selection." : " in this model.") +
                (result.SkippedUnplaced > 0
                    ? $"\n\n{result.SkippedUnplaced} unplaced room(s) were ignored."
                    : string.Empty));

            return Result.Cancelled;
        }

        var csv = WriteReport(doc, result, out var reportError);

        var lines = BuildSummary(result, scope);

        lines.Add(string.Empty);
        lines.Add(csv is not null
            ? $"Full dump: {csv}"
            : $"The report could not be written ({reportError}). The verdicts above are complete; " +
              "the per-subface rows are not.");

        lines.Add(string.Empty);
        lines.Add("Read-only: nothing in the model was changed.");

        var headline = Headline(result);

        var dialog = new TaskDialog(CommandName)
        {
            MainInstruction = headline,
            MainContent = string.Join("\n", lines),
            FooterText = $"{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        // Selecting the elements at risk is worth more than quoting their ids: the point of
        // the dump is to go and look at the surface it is talking about. Walls and slabs from
        // the band, stairs from the second finding - one list, because the dialog only offers
        // one command link and a user does not care which finding put an element on it.
        var atRisk = result.WithOpening
            .SelectMany(r => r.PaintedBand)
            .Where(s => s.HostId is not null)
            .Select(s => s.HostId!.Value)
            .Concat(result.WithPaintedStairSide
                .SelectMany(r => r.PaintedStairSides)
                .Select(h => h.StairId))
            .Distinct()
            .ToList();

        if (atRisk.Count > 0)
        {
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                $"Select the {atRisk.Count} element(s) at risk",
                "Selects the walls, slabs and stairs whose painted surface falls in the band " +
                "or is invisible to every pass.");
        }

        if (dialog.Show() == TaskDialogResult.CommandLink1)
        {
            try
            {
                ctx.UiDocument.Selection.SetElementIds(
                    [.. atRisk.Select(id => new ElementId(id))]);
            }
            catch (Exception ex)
            {
                Log.Warn($"{CommandName}: could not select hosts: {ex.Message}");
            }
        }

        return Result.Succeeded;
    }

    /// <summary>
    /// The headline is the verdict tally, because the entire purpose of running this is to
    /// learn which fix to build - and that is one word per room. Two independent questions
    /// live in one dialog now, so each contributes its own fragment; either can be silent
    /// while the other is not, and a genuinely clean run says so plainly rather than picking
    /// one finding to lead with.
    /// </summary>
    private static string Headline(SubfaceDumpResult result)
    {
        var parts = new List<string>();

        var empty = result.Count(BandVerdict.BandEmpty);
        var routed = result.Count(BandVerdict.BandFallbackRouted);
        var wall = result.Count(BandVerdict.BandWallResolved);

        if (empty + routed + wall > 0)
        {
            // Reported in the order that decides the work, most structural first: an empty
            // band needs a new pass, a routed one needs a routing change, a wall-resolved one
            // means neither and the loss is further down in the clip.
            parts.Add(empty > 0 && routed == 0 && wall == 0
                ? $"BAND EMPTY in all {empty} room(s) with an opening"
                : routed > 0 && empty == 0 && wall == 0
                    ? $"BAND FALLBACK-ROUTED in all {routed} room(s) with an opening"
                    : $"band: {empty} empty, {routed} fallback-routed, {wall} wall-resolved");
        }

        var stairRooms = result.WithPaintedStairSide.Count();
        if (stairRooms > 0)
        {
            var faces = result.StairSideHits.Count(h => h.Vertical && h.Painted);
            parts.Add($"{faces} painted stair-owned face(s) unreachable beside {stairRooms} room(s)");
        }

        return parts.Count > 0
            ? string.Join("  ·  ", parts)
            : "No slab opening and no painted stair-owned face found near any room.";
    }

    private static List<string> BuildSummary(SubfaceDumpResult result, IReadOnlyCollection<ElementId>? scope)
    {
        var lines = new List<string>
        {
            scope is { Count: > 0 }
                ? $"{result.Rooms.Count} selected room(s) examined."
                : $"{result.Rooms.Count} placed room(s) examined (whole model).",
            $"{result.Subfaces.Count} boundary subface(s) read.",
            string.Empty,
            "VERDICTS:",
            $"  band empty ................ {result.Count(BandVerdict.BandEmpty)}",
            $"  band fallback-routed ...... {result.Count(BandVerdict.BandFallbackRouted)}",
            $"  band wall-resolved ........ {result.Count(BandVerdict.BandWallResolved)}",
            $"  no opening above .......... {result.Count(BandVerdict.NoOpening)}",
            $"  no slab above ............. {result.Count(BandVerdict.NoSlabAbove)}",
            $"  volumes unavailable ....... {result.Count(BandVerdict.VolumesUnavailable)}",
        };

        var stairRoomsChecked = result.Rooms.Count(r => r.StairSides.Count > 0);
        if (stairRoomsChecked > 0)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"{result.StairSideHits.Count} stair face(s) checked beyond CeilingFallback's " +
                $"own underside tier, across {stairRoomsChecked} room(s) with a stair nearby.");
        }

        var interesting = result.WithOpening.OrderByDescending(r => r.OpenArea).ToList();

        if (interesting.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("ROOMS WITH AN OPENING, LARGEST FIRST:");

            foreach (var room in interesting.Take(8))
            {
                var painted = room.PaintedBand.Count();

                lines.Add(
                    $"  [{Describe(room.Verdict)}] {room.Label} " +
                    $"- opening {Measure.ToSquareMetres(room.OpenArea):0.00} m2, " +
                    $"{room.HoleLoops} hole loop(s), " +
                    $"band {Measure.ToMillimetres(room.BandHeight):0} mm " +
                    $"[{room.SoffitZ:0.000} to {room.SlabTopZ:0.000} ft]");

                lines.Add(
                    $"      slab {room.SlabId} {room.SlabName} · " +
                    $"{room.Band.Count} band subface(s), " +
                    $"{room.Band.Count(s => s.Vertical)} vertical, " +
                    $"{painted} painted");

                foreach (var sub in room.Band.Where(s => s.Vertical).Take(4))
                {
                    lines.Add(
                        $"        {sub.Type} host {sub.HostId?.ToString() ?? "-"} " +
                        $"{sub.HostClass}/{sub.HostCategory} " +
                        $"wall={sub.IsWall} painted={sub.HostPainted} " +
                        $"z {sub.ZMin:0.000}..{sub.ZMax:0.000}");
                }
            }

            if (interesting.Count > 8)
                lines.Add($"  ... and {interesting.Count - 8} more in the report.");
        }

        var stairFindings = result.WithPaintedStairSide
            .OrderByDescending(r => r.PaintedStairSides.Sum(h => h.AreaSqFt))
            .ToList();

        if (stairFindings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("STAIR-OWNED FACES INVISIBLE TO EVERY PASS, LARGEST FIRST:");
            lines.Add(
                "  (OST_Stairs bounds no room in Revit, so no subface is ever produced for " +
                "these - see the SubfaceDump.StairSideHit remarks. Only CeilingFallback's own " +
                "downward-facing tier reaches any part of a stair; these do not.)");

            foreach (var room in stairFindings.Take(8))
            {
                var hits = room.PaintedStairSides.ToList();
                var totalArea = hits.Sum(h => h.AreaSqFt);

                lines.Add(
                    $"  {room.Label} - {hits.Count} painted face(s), " +
                    $"{Measure.ToSquareMetres(totalArea):0.00} m2 total, " +
                    $"{room.StairSides.Count(s => !s.Painted)} unpainted stair face(s) also checked");

                foreach (var hit in hits.Take(4))
                {
                    lines.Add(
                        $"      stair {hit.StairId} \"{hit.StairName}\" · " +
                        $"{Measure.ToSquareMetres(hit.AreaSqFt):0.00} m2 · normal Z {hit.NormalZ:0.00}");
                }

                if (hits.Count > 4) lines.Add($"      ... and {hits.Count - 4} more in the report.");
            }

            if (stairFindings.Count > 8)
                lines.Add($"  ... and {stairFindings.Count - 8} more in the report.");
        }

        if (result.Count(BandVerdict.VolumesUnavailable) > 0)
        {
            lines.Add(string.Empty);
            lines.Add(
                "VOLUMES OFF: some rooms report zero Volume, so the opening above them could " +
                "not be derived from the volume excess and only the slab's own inner loops " +
                "were available. Switch on Area and Volume Computations (Volumes) and run " +
                "again for a complete answer - this command will not switch it on itself, " +
                "because that is a model edit.");
        }

        if (result.SkippedUnplaced > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"{result.SkippedUnplaced} unplaced/unenclosed room(s) ignored.");
        }

        if (result.Notes.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(result.Notes.Take(5));
            if (result.Notes.Count > 5) lines.Add($"  ... and {result.Notes.Count - 5} more notes in the report.");
        }

        return lines;
    }

    private static string Describe(BandVerdict verdict) => verdict switch
    {
        BandVerdict.BandEmpty => "BAND EMPTY",
        BandVerdict.BandFallbackRouted => "FALLBACK-ROUTED",
        BandVerdict.BandWallResolved => "WALL-RESOLVED",
        BandVerdict.NoOpening => "no opening",
        BandVerdict.NoSlabAbove => "no slab above",
        BandVerdict.VolumesUnavailable => "volumes off",
        _ => verdict.ToString(),
    };

    private static IReadOnlyCollection<ElementId>? SelectedRooms(CommandContext ctx)
    {
        try
        {
            var ids = ctx.UiDocument.Selection.GetElementIds()
                .Where(id => ctx.Document.GetElement(id) is Autodesk.Revit.DB.Architecture.Room)
                .ToList();

            return ids.Count > 0 ? ids : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The CSV is the artefact. A dialog holds a verdict per room; deciding a fix needs every
    /// subface, with its host and its Z range, so the band can be reconstructed by hand.
    /// </summary>
    private static string? WriteReport(Document doc, SubfaceDumpResult result, out string error)
    {
        error = string.Empty;

        try
        {
            var path = ReportWriter.DefaultPath(doc, "room-subface-dump");

            var rows = new List<IReadOnlyList<string>>
            {
                new[]
                {
                    "Room id", "Room", "Verdict", "Open area m2", "Hole loops",
                    "Soffit ft", "Slab top ft", "Band mm",
                    "Face", "Subface", "Type", "Area m2",
                    "Z min ft", "Z max ft", "Normal Z", "Vertical",
                    "Host id", "Host class", "Host category", "Is wall",
                    "In band", "Band overlap mm", "Host painted",
                },
            };

            var byRoom = result.Rooms.ToDictionary(r => r.RoomId);

            foreach (var sub in result.Subfaces)
            {
                byRoom.TryGetValue(sub.RoomId, out var room);

                rows.Add(new[]
                {
                    sub.RoomId.ToString(),
                    sub.RoomLabel,
                    room is null ? string.Empty : Describe(room.Verdict),
                    room is null ? string.Empty : Measure.ToSquareMetres(room.OpenArea).ToString("0.000"),
                    room?.HoleLoops.ToString() ?? string.Empty,
                    room?.SoffitZ.ToString("0.0000") ?? string.Empty,
                    room?.SlabTopZ.ToString("0.0000") ?? string.Empty,
                    room is null ? string.Empty : Measure.ToMillimetres(room.BandHeight).ToString("0.0"),
                    sub.FaceIndex.ToString(),
                    sub.SubfaceIndex.ToString(),
                    sub.Type,
                    Measure.ToSquareMetres(sub.AreaSqFt).ToString("0.0000"),
                    sub.ZMin.ToString("0.0000"),
                    sub.ZMax.ToString("0.0000"),
                    sub.NormalZ.ToString("0.000"),
                    sub.Vertical ? "yes" : "no",
                    sub.HostId?.ToString() ?? string.Empty,
                    sub.HostClass,
                    sub.HostCategory,
                    sub.IsWall ? "yes" : "no",
                    sub.InBand ? "yes" : "no",
                    Measure.ToMillimetres(sub.BandOverlapFt).ToString("0.0"),
                    sub.HostPainted ? "yes" : "no",
                });
            }

            // A SECOND TABLE IN THE SAME FILE, not a second file. ReportWriter.WriteCsv joins
            // rows verbatim with no header enforcement, so a blank divider row plus a new
            // header row reads cleanly by eye and by spreadsheet without inventing a second
            // report path for a question this command answers in one run anyway.
            if (result.StairSideHits.Count > 0)
            {
                rows.Add([""]);
                rows.Add(new[]
                {
                    "Room id", "Room", "Stair id", "Stair name",
                    "Area m2", "Normal Z", "Vertical", "Painted",
                });

                foreach (var room in result.Rooms.OrderBy(r => r.Label))
                {
                    foreach (var hit in room.StairSides)
                    {
                        rows.Add(new[]
                        {
                            room.RoomId.ToString(),
                            room.Label,
                            hit.StairId.ToString(),
                            hit.StairName,
                            Measure.ToSquareMetres(hit.AreaSqFt).ToString("0.0000"),
                            hit.NormalZ.ToString("0.000"),
                            hit.Vertical ? "yes" : "no",
                            hit.Painted ? "yes" : "no",
                        });
                    }
                }
            }

            ReportWriter.WriteCsv(path, rows);

            var log = new List<string>
            {
                $"ROOM SUBFACE DUMP - {result.Rooms.Count} room(s), {result.Subfaces.Count} subface(s), " +
                $"{result.StairSideHits.Count} stair face(s) checked.",
                "Boundary location: Finish (identical to RoomFinishCalculator).",
                "Read-only: room limits and volumes were read AS THEY STAND, not adjusted first.",
                string.Empty,
            };

            foreach (var room in result.Rooms.OrderBy(r => r.Label))
            {
                var stairNote = room.StairSides.Count == 0
                    ? string.Empty
                    : $" | stair faces {room.StairSides.Count} " +
                      $"({room.PaintedStairSides.Count()} painted, vertical/raking)";

                log.Add(
                    $"{room.Label} (id {room.RoomId}) [{Describe(room.Verdict)}] " +
                    $"base {room.BaseZ:0.000} top {room.RoomTopZ:0.000} " +
                    $"soffit {room.SoffitZ:0.000} slab top {room.SlabTopZ:0.000} " +
                    $"band {Measure.ToMillimetres(room.BandHeight):0} mm " +
                    $"opening {Measure.ToSquareMetres(room.OpenArea):0.00} m2 " +
                    $"loops {room.HoleLoops} " +
                    $"band subfaces {room.Band.Count} ({room.Band.Count(s => s.Vertical)} vertical, " +
                    $"{room.PaintedBand.Count()} painted)" + stairNote);
            }

            if (result.Notes.Count > 0)
            {
                log.Add(string.Empty);
                log.AddRange(result.Notes);
            }

            ReportWriter.WriteSidecarLog(path, log);

            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"Room Subface Dump: report not written: {ex.Message}");
            return null;
        }
    }
}
