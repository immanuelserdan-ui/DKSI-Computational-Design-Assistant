using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Dimensions;

/// <summary>
/// Places interior corner-to-corner dimensions in every room of every plan view it is given.
///
/// WHAT IT PRODUCES, PER ROOM
///   ONE DIMENSION PER WALL FACE, measured from the interior corner where it starts to the
///   corner where it ends. Every axis the room has is measured - a splayed corner or angled
///   partition included - and every face that runs along an axis gets its own string, bracketed
///   by whichever two faces square to that axis terminate it.
///
///   NOT WALL-TO-WALL. Measuring wall face to wall face reports the gap between consecutive
///   faces, which in an L-shaped room can run from a wall bounding one leg to a wall bounding
///   the other - through a partition and across the room beyond it. Vaer. 1 read 1800 / 1900
///   that way and the 1900 was not a distance anything in the building has. Corner-to-corner
///   gives 1800 and 3700, and both can be checked with a tape.
///
/// WHY IT WORKS PER VIEW AND NOT PER LEVEL
///   A dimension belongs to a view, not to the model. There is no such thing as dimensioning
///   a room once and having it appear everywhere, and there is no correct answer to "which
///   view" that this engine can pick on its own - a level with a 1:50 unit plan and a 1:200
///   overall plan wants dimensions in the first and not the second. So the caller names the
///   views and this sweeps every room on each of their levels. That is still whole-model
///   work: nothing here looks at the active view or at one storey unless it was handed one.
///
/// RE-RUN SAFETY
///   Every dimension it creates is stamped with the room it measures. A second run deletes
///   the stamped dimensions for the rooms it is about to redo and places them again, so a
///   changed wall gets a corrected dimension instead of a second one on top of the first.
///   Dimensions drawn by a person carry no stamp and are never touched.
/// </summary>
public sealed class RoomDimensionGenerator
{
    /// <summary>Stamp identity. Changing it orphans everything placed by earlier runs.</summary>
    private const string Tool = "roomdimension";

    private const string LegacyCommentsPrefix = "DKSI room dimension:";

    private readonly Document _doc;
    private readonly RoomDimensionSettings _settings;

    public RoomDimensionGenerator(Document doc, RoomDimensionSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    public sealed class Outcome
    {
        public int ViewsProcessed { get; set; }
        public int RoomsDimensioned { get; set; }
        public int RoomsSkipped { get; set; }
        public int DimensionsCreated { get; set; }
        public int DimensionsReplaced { get; set; }
        public int TextsMoved { get; set; }
        public List<string> Notes { get; } = [];
        public List<IReadOnlyList<string>> Rows { get; } = [];

        /// <summary>
        /// The dimensions this run created, for the text arranger to tidy afterwards.
        /// Internal because the placement it carries is: nothing outside this assembly has
        /// any use for a room's inward vector.
        /// </summary>
        internal List<PlacedDimension> Created { get; } = [];
    }

    /// <summary>
    /// A created dimension together with the placement that produced it.
    ///
    /// The placement cannot be recovered from the Dimension afterwards. Revit will tell you
    /// where the line is, but not which side of it the ROOM is on - and that is the one fact
    /// the text pass needs, because a dimension hugging a wall has open room on exactly one
    /// side and brickwork on the other.
    /// </summary>
    internal readonly record struct PlacedDimension(Dimension Dimension, DimensionMath.Placement Placement);

    /// <summary>
    /// Places dimensions in every view given. CALLER OWNS THE TRANSACTION - run it inside
    /// <see cref="Transactions.Run"/> to keep the work, or <see cref="Transactions.Probe"/>
    /// for a dry run whose counts are exact because Revit really did the work before it was
    /// rolled back.
    /// </summary>
    public Outcome Run(IReadOnlyList<ViewPlan> views)
    {
        var outcome = new Outcome();

        var type = _settings.ResolveType(_doc)
            ?? throw new InvalidOperationException(
                $"No linear dimension type named '{_settings.DimensionTypeName}' in this project.");

        outcome.Rows.Add(new[] { "View", "Level", "Room", "Number", "Run", "Witness lines", "Measured to", "Result" });

        foreach (var view in views)
        {
            if (view.IsTemplate) continue;

            var level = view.GenLevel;
            if (level is null)
            {
                outcome.Notes.Add($"View '{view.Name}' has no associated level and was skipped.");
                continue;
            }

            if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_Dimensions)))
                outcome.Notes.Add(
                    $"View '{view.Name}': the Dimensions category is HIDDEN in this view. The " +
                    "dimensions below were created and will not be visible until it is turned on.");

            outcome.ViewsProcessed++;

            foreach (var room in RoomsOn(level))
            {
                if (!IsDimensionable(room, out var reason))
                {
                    outcome.RoomsSkipped++;
                    if (reason is not null) outcome.Notes.Add(reason);
                    continue;
                }

                outcome.DimensionsReplaced += RemovePrevious(view, room, outcome);

                var placed = Place(view, room, type, outcome);

                if (placed > 0) outcome.RoomsDimensioned++;
                else outcome.RoomsSkipped++;
            }
        }

        return outcome;
    }

    // ---- room selection ----------------------------------------------------------

    private IEnumerable<Room> RoomsOn(Level level) =>
        new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .Where(r => r.LevelId == level.Id)
            .OrderBy(r => r.Number, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// "A valid, designated Room assignment" made concrete.
    ///
    /// Area is the test that matters and it is not the obvious one. An UNPLACED room - one
    /// that exists in the schedule with a name and number but sits in no model space - is a
    /// perfectly ordinary Room element with a Location of null and an Area of zero. So is a
    /// room whose enclosing walls were deleted, which Revit calls 'not enclosed' and keeps.
    /// Neither has a boundary to dimension, and both would otherwise reach the geometry code
    /// and throw there instead of being reported here.
    /// </summary>
    private bool IsDimensionable(Room room, out string? reason)
    {
        reason = null;

        if (room.Location is null || room.Area <= 0)
        {
            // Silent: an unplaced room is a normal thing for a project to contain and
            // listing every one of them would bury the notes that matter.
            return false;
        }

        var name = RoomNaming.NameOf(room);

        if (_settings.ExcludedRoomNames.Any(
                x => name.Contains(x, StringComparison.CurrentCultureIgnoreCase)))
            return false;

        if (Measure.ToSquareMetres(room.Area) < _settings.MinimumRoomAreaSquareMetres)
        {
            reason = $"{RoomNaming.Describe(room)} is under " +
                     $"{_settings.MinimumRoomAreaSquareMetres:0.##} m2 and was skipped.";
            return false;
        }

        return true;
    }

    // ---- placement ---------------------------------------------------------------

    private int Place(ViewPlan view, Room room, DimensionType type, Outcome outcome)
    {
        var faces = RoomFaceReferences.Collect(_doc, room, outcome.Notes);

        if (faces.Count < 2)
        {
            outcome.Notes.Add($"{RoomNaming.Describe(room)}: fewer than two usable wall " +
                              "faces, nothing to dimension between.");
            return 0;
        }

        var elevation = faces[0].Point.Z;

        // ONE RUN PER WALL AXIS, not two runs and a hope. A room's walls do not all lie on
        // two perpendicular axes: a splayed corner, an angled partition or a bay puts faces
        // on a third and a fourth, and the previous two-run version measured none of them and
        // said nothing about it. Every axis the room actually has now gets its own string.
        var axes = DimensionMath.WallAxes(
            faces.Select(f => (f.Inward.ToPlan(), f.Length)).ToList(),
            _settings.SquarenessCosine);

        var placed = 0;

        foreach (var axis in axes)
            placed += PlaceAxis(view, room, type, outcome, faces, axis, elevation);

        if (placed == 0)
            outcome.Notes.Add($"{RoomNaming.Describe(room)}: {axes.Count} wall axis/axes found " +
                              "but no run could be placed on any of them.");

        return placed;
    }

    /// <summary>
    /// Every corner-to-corner dimension on one axis: one per wall face running along it,
    /// measured between the two faces that terminate it.
    ///
    /// The faces split in two here and the split is the whole method. A face whose normal is
    /// PARALLEL to the axis is a corner - something a dimension measures TO. A face whose
    /// normal is perpendicular RUNS ALONG the axis - it is what a dimension is measured FOR,
    /// and what it is drawn against. Every run is then bracketed by the two ends at its span.
    /// </summary>
    private int PlaceAxis(
        ViewPlan view,
        Room room,
        DimensionType type,
        Outcome outcome,
        IReadOnlyList<RoomFace> faces,
        DimensionMath.Vec2 axis,
        double elevation)
    {
        var perpendicular = DimensionMath.LeftOf(axis);
        var axisName = $"{DimensionMath.BearingOf(axis):0}°";

        var ends = new List<DimensionMath.FaceEnd>();
        var runs = new List<DimensionMath.FaceRun>();

        for (var i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            var inward = face.Inward.ToPlan();
            var point = face.Point.ToPlan();

            if (DimensionMath.IsSquareTo(inward, axis, _settings.SquarenessCosine))
            {
                ends.Add(new DimensionMath.FaceEnd(i, point.Dot(axis)));
                continue;
            }

            if (!DimensionMath.IsSquareTo(inward, perpendicular, _settings.SquarenessCosine))
                continue;                                   // neither along nor across this axis

            var a = face.Start.ToPlan().Dot(axis);
            var b = face.End.ToPlan().Dot(axis);

            runs.Add(new DimensionMath.FaceRun(
                Index: i,
                SpanMin: Math.Min(a, b),
                SpanMax: Math.Max(a, b),
                Offset: point.Dot(perpendicular),
                InwardSign: inward.Dot(perpendicular) > 0 ? 1 : -1));
        }

        if (runs.Count == 0 || ends.Count < 2)
        {
            outcome.Rows.Add(Row(view, room, axisName, 0, [],
                "no wall face runs along this axis with corners at both ends"));
            return 0;
        }

        var corners = DimensionMath.CornerToCorner(runs, ends, _settings.CornerMatchTolerance);

        // Faces that lost an end are the ones the core rule cares about, so they are named
        // rather than counted: a face with no dimension is exactly what this must not do
        // silently.
        var uncovered = runs.Count - corners.Count;
        if (uncovered > 0)
            outcome.Notes.Add($"{RoomNaming.Describe(room)} {axisName}: {uncovered} wall face(s) " +
                              "share a dimension with another face or have no corner at one end.");

        var placed = 0;

        foreach (var corner in corners)
        {
            var start = faces[corner.StartFace];
            var end = faces[corner.EndFace];
            var along = faces[corner.AlongFace];

            // The line sits offset from the face it measures, INTO the room.
            var across = corner.FaceOffset + (corner.InwardSign * _settings.Offset);

            var placement = new DimensionMath.Placement(
                AlongMin: corner.StartAt,
                AlongMax: corner.EndAt,
                Across: across,
                Inward: perpendicular.Scaled(corner.InwardSign),
                WallAlongInward: corner.FaceOffset * corner.InwardSign);

            var line = Line.CreateBound(
                Rebuild(axis, placement.AlongMin, perpendicular, across, elevation),
                Rebuild(axis, placement.AlongMax, perpendicular, across, elevation));

            var dimension = Create(view, line, [start.Reference, end.Reference], type);

            // Named by the face it is drawn against, so a room's several dimensions on one
            // bearing each keep their own stamp and a re-run replaces them individually.
            var name = $"{axisName}@{Measure.ToMillimetres(corner.FaceOffset):0}";
            var measured = new[] { (start, 0.0), (end, 0.0), (along, 0.0) };

            if (dimension is null)
            {
                outcome.Rows.Add(Row(view, room, name, 2, measured, "Revit rejected the dimension"));

                outcome.Notes.Add($"{RoomNaming.Describe(room)} {name}: Revit would not create " +
                                  "this dimension. See the log for the exception.");
                continue;
            }

            // WriteOrFallback, and the result is CHECKED. This used to call Write and discard
            // what it returned, so on a model where Extensible Storage is unavailable every
            // dimension was placed unstamped - invisible to RemovePrevious, which then left
            // them in place and drew a second string over each one on the next run. The legacy
            // Comments prefix this class declares was only ever read, never written.
            if (!ElementStamp.WriteOrFallback(dimension, Tool, Tag(room, name), LegacyCommentsPrefix))
            {
                outcome.Notes.Add(
                    $"{RoomNaming.Describe(room)} {name}: the dimension was placed but could not " +
                    "be stamped, so a later run will not recognise it and will draw a second " +
                    "string over it. Delete it by hand.");
            }

            outcome.Created.Add(new PlacedDimension(dimension, placement));
            outcome.DimensionsCreated++;

            outcome.Rows.Add(Row(view, room, name, 2, measured,
                $"placed - {Measure.ToMillimetres(corner.Clear):0} mm corner to corner"));

            placed++;
        }

        return placed;
    }


    private Dimension? Create(View view, Line line, IEnumerable<Reference> references, DimensionType type)
    {
        var array = new ReferenceArray();
        foreach (var reference in references) array.Append(reference);

        if (array.Size < 2) return null;

        try
        {
            return _doc.Create.NewDimension(view, line, array, type);
        }
        catch (Exception ex)
        {
            // Expected often enough to be ordinary control flow: Revit refuses a dimension
            // whose references it cannot resolve in this view, and there is no way to ask it
            // in advance whether it will. Caller retries with fewer references.
            Log.Debug($"NewDimension refused ({array.Size} refs): {ex.Message}");
            return null;
        }
    }

    // ---- geometry frame ----------------------------------------------------------

    /// <summary>
    /// A point back out of the room's own (u, v) frame into world coordinates.
    ///
    /// u, v and Z are orthonormal, so this is exact and needs no origin of its own - the
    /// coordinates ARE the dot products, and adding the scaled axes inverts them.
    ///
    /// The only piece of the frame left on the Revit side of the boundary. Choosing the axes,
    /// measuring the extent and deciding which wall each string hugs all live in
    /// DimensionMath, where they can be asserted on without opening Revit.
    /// </summary>
    private static XYZ Rebuild(
        DimensionMath.Vec2 along, double alongCoordinate,
        DimensionMath.Vec2 across, double acrossCoordinate,
        double z) =>
        along.Scaled(alongCoordinate).Plus(across.Scaled(acrossCoordinate)).ToWorld(z);

    // ---- re-run bookkeeping ------------------------------------------------------

    private static string Tag(Room room, string runName) => $"{room.UniqueId}|{runName}";

    /// <summary>
    /// Removes this tool's own dimensions for one room in one view. Hand-drawn dimensions
    /// carry no stamp and are invisible to this.
    ///
    /// OWNERSHIP FIRST, AND THE DELETE CANNOT THROW OUT OF HERE. Both matter, and neither was
    /// true before. This is called per room per view from inside the ONE transaction the whole
    /// command runs in, so an exception here does not lose one room's dimensions - it unwinds
    /// the entire run, discarding every dimension already placed for every other room in every
    /// other view. On a central model that needed nothing more than one previously generated
    /// dimension being checked out by a colleague, and the dry run immediately beforehand could
    /// not warn about it: Transactions.Probe rolls back either way, so the preview reported a
    /// confident count for a run that was about to be thrown away in full.
    /// </summary>
    private int RemovePrevious(View view, Room room, Outcome outcome)
    {
        var collector = new FilteredElementCollector(_doc, view.Id)
            .OfCategory(BuiltInCategory.OST_Dimensions)
            .WhereElementIsNotElementType();

        var stampFilter = ElementStamp.Filter();
        if (stampFilter is not null) collector = collector.WherePasses(stampFilter);

        var prefix = room.UniqueId + "|";

        var stale = collector
            .Where(e => ElementStamp.Read(e, Tool, LegacyCommentsPrefix)?
                            .StartsWith(prefix, StringComparison.Ordinal) == true)
            .Select(e => e.Id)
            .ToList();

        if (stale.Count == 0) return 0;

        var ownership = Worksharing.Claim(_doc, stale);

        if (ownership.OwnedByOthers.Count > 0)
        {
            outcome.Notes.Add(
                $"{RoomNaming.Describe(room)} in '{view.Name}': {ownership.OwnedByOthers.Count} " +
                "dimension(s) from a previous run are owned by other users and were left in " +
                "place. This room's new dimensions are drawn over them until those users " +
                "synchronise.");

            stale = [.. ownership.Writable];
            if (stale.Count == 0) return 0;
        }

        try
        {
            _doc.Delete(stale);
            return stale.Count;
        }
        catch (Exception ex)
        {
            // Contained here on purpose - see the note on this method. One room's stale
            // dimensions surviving is a duplicate to tidy; the whole run unwinding is a
            // command that reports success on a dry run and then does nothing.
            outcome.Notes.Add(
                $"{RoomNaming.Describe(room)} in '{view.Name}': {stale.Count} previous " +
                $"dimension(s) could not be removed ({ex.Message}). The new ones are drawn " +
                "over them.");

            Log.Warn($"Room dimensions: could not remove {stale.Count} previous dimension(s) " +
                     $"in '{view.Name}': {ex.Message}");

            return 0;
        }
    }

    /// <summary>
    /// One report row.
    ///
    /// THE 'MEASURED TO' COLUMN IS THE POINT OF THIS REPORT. Whether a dimension really runs
    /// finish face to finish face is not a claim this tool can settle on its own - it depends
    /// on whether the 20 mm finish walls carry Room Bounding, which is a modelling decision
    /// this code cannot see and must not guess at. What it CAN do is name the wall type every
    /// witness line landed on, so the question is answered by reading one column instead of
    /// by trusting the tool.
    /// </summary>
    private IReadOnlyList<string> Row(
        View view, Room room, string runName, int witnessLines,
        IEnumerable<(RoomFace Face, double At)> measuredTo, string result) =>
        [
            view.Name,
            (view as ViewPlan)?.GenLevel?.Name ?? string.Empty,
            RoomNaming.NameOf(room),
            RoomNaming.NumberOf(room),
            runName,
            witnessLines.ToString(),
            HostTypes(measuredTo),
            result,
        ];

    /// <summary>The distinct wall types a run's witness lines landed on.</summary>
    private string HostTypes(IEnumerable<(RoomFace Face, double At)> measuredTo)
    {
        var names = new List<string>();

        foreach (var (face, _) in measuredTo)
        {
            // Wall.Name is the TYPE name, which is what tells a reader whether this is the
            // finish wall or the base wall behind it.
            var name = _doc.GetElement(face.HostId)?.Name;
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
        }

        return string.Join(" | ", names);
    }
}
