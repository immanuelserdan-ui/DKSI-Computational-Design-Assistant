using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Sweeps;

namespace Cda.Revit.Addin.Qa;

/// <summary>One room as it appears in the picker.</summary>
public sealed class RoomEntry
{
    public required ElementId Id { get; init; }
    public required string Display { get; init; }
    public required string SortKey { get; init; }
    public override string ToString() => Display;
}

/// <summary>Everything that bounds one room, resolved once.</summary>
public sealed class RoomFinishSet
{
    public required ElementId RoomId { get; init; }
    public required string RoomLabel { get; init; }

    public List<ElementId> Walls { get; } = [];
    public List<ElementId> Floors { get; } = [];
    public List<ElementId> Ceilings { get; } = [];

    /// <summary>Skirting found inside this room. The optional extra, not a boundary.</summary>
    public List<ElementId> Sweeps { get; } = [];

    /// <summary>Doors, windows and openings cut into the bounding walls.</summary>
    public List<ElementId> Openings { get; } = [];

    /// <summary>Casework and MEP standing in the room.</summary>
    public List<ElementId> Contents { get; } = [];

    /// <summary>
    /// Bounding elements that live in a LINKED model.
    ///
    /// These are reported and never selected. <see cref="Selection.SetElementIds"/> only
    /// accepts ids from the host document; passing a linked element's id either selects the
    /// wrong element - ids collide across documents - or throws. A room bounded by a linked
    /// core is a real and common arrangement, so saying "3 bounding elements are in a link"
    /// is the honest answer rather than silently returning fewer walls.
    /// </summary>
    public List<string> LinkedNotes { get; } = [];

    public List<string> Notes { get; } = [];

    /// <summary>Finish parameter values already written onto the room, for display.</summary>
    public List<(string Name, string Value)> Finishes { get; } = [];

    public IReadOnlyList<ElementId> Boundary =>
        [.. Walls, .. Floors, .. Ceilings];

    /// <summary>
    /// What to select and isolate, assembled from what the caller asked for.
    ///
    /// The ROOM ITSELF is always included. Without it the isolated view has no colour fill
    /// and no tag, so the one thing the whole exercise is about - this room - is the only
    /// thing not visible in it.
    /// </summary>
    public IReadOnlyList<ElementId> Isolation(bool withSweeps, bool withContents)
    {
        var ids = new List<ElementId> { RoomId };

        ids.AddRange(Walls);
        ids.AddRange(Floors);
        ids.AddRange(Ceilings);

        if (withSweeps) ids.AddRange(Sweeps);

        if (withContents)
        {
            ids.AddRange(Openings);
            ids.AddRange(Contents);
        }

        return [.. ids.Distinct()];
    }
}

/// <summary>
/// Answers "what does this room touch?" for the finish QA check.
///
/// WHY THE SOLID AND NOT JUST THE BOUNDARY SEGMENTS
///   <c>Room.GetBoundarySegments</c> is the obvious call and it only ever returns the walls
///   and separation lines around the room in plan. It says nothing about what is overhead or
///   underfoot, which is two thirds of what a finish check is about.
///   <see cref="SpatialElementGeometryCalculator"/> returns the room as a closed solid and,
///   for each face of it, the element that bounds it - so the floor and the ceiling arrive by
///   the same route as the walls, already matched to the faces the finish is measured on.
///
///   The same class is what <see cref="Finishes.RoomFinishCalculator"/> measures from. That
///   is deliberate: a QA highlight that showed a different set of elements than the engine
///   measured would send people looking at the wrong wall.
/// </summary>
public sealed class RoomFinishInspector
{
    private readonly Document _doc;
    private readonly QaSettings _settings;

    public RoomFinishInspector(Document doc, QaSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    /// <summary>
    /// Every placed room in the model, ordered the way a room schedule is.
    ///
    /// Unplaced and unenclosed rooms are excluded. Both are real Room elements with a name
    /// and a number, and neither has any geometry to highlight - offering them in the picker
    /// produces a Highlight that selects nothing and looks like a broken tool.
    /// </summary>
    public IReadOnlyList<RoomEntry> ListRooms()
    {
        var entries = new List<RoomEntry>();

        foreach (var room in new FilteredElementCollector(_doc)
                     .OfCategory(BuiltInCategory.OST_Rooms)
                     .WhereElementIsNotElementType()
                     .OfType<Room>())
        {
            try
            {
                // Area is zero for both unplaced rooms (no Location) and unenclosed ones
                // (Location but no closed boundary). Neither can be highlighted.
                if (room.Area <= 0) continue;
                if (room.Location is null) continue;
            }
            catch
            {
                continue;
            }

            var number = Text(room, BuiltInParameter.ROOM_NUMBER);
            var name = Text(room, BuiltInParameter.ROOM_NAME);
            var level = LevelName(room);

            var display = string.IsNullOrWhiteSpace(number)
                ? $"{name}  ·  {level}  [{room.Id.Value}]"
                : $"{number} — {name}  ·  {level}";

            entries.Add(new RoomEntry
            {
                Id = room.Id,
                Display = display,
                // Numbers are text in Revit, so "10" sorts before "9". Padding the numeric
                // run makes the picker read in the order the drawings are numbered.
                SortKey = PadNumbers(number) + "|" + name,
            });
        }

        return [.. entries.OrderBy(e => e.SortKey, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Resolves the boundary of one room. Never throws: a room that cannot be measured
    /// returns an empty set carrying the reason in <see cref="RoomFinishSet.Notes"/>.
    /// </summary>
    public RoomFinishSet Resolve(ElementId roomId, bool includeSweeps) =>
        Resolve(roomId, includeSweeps, false);

    /// <summary>
    /// Resolves the boundary of one room. Never throws: a room that cannot be measured
    /// returns an empty set carrying the reason in <see cref="RoomFinishSet.Notes"/>.
    /// </summary>
    /// <param name="includeContents">
    /// Also gather the doors, windows, casework and MEP that belong to the room. Off for the
    /// finish check proper - none of them contribute to a finish area - and on for the
    /// isolated view, where they are what makes the space navigable.
    /// </param>
    public RoomFinishSet Resolve(ElementId roomId, bool includeSweeps, bool includeContents)
    {
        var room = _doc.GetElement(roomId) as Room;

        var set = new RoomFinishSet
        {
            RoomId = roomId,
            RoomLabel = room is null ? $"[{roomId.Value}]" : Label(room),
        };

        if (room is null)
        {
            set.Notes.Add("That room no longer exists in the model. Refresh the list.");
            return set;
        }

        ReadFinishParameters(room, set);

        if (!SpatialElementGeometryCalculator.CanCalculateGeometry(room))
        {
            set.Notes.Add(
                "Revit cannot compute this room's geometry - it is unenclosed, or its upper " +
                "limit puts the top below the base. Nothing to highlight.");
            return set;
        }

        var options = new SpatialElementBoundaryOptions
        {
            // Finish, not Center. The finish face is the surface being painted, and it is
            // what the finish engine measures - a highlight taken from the centreline would
            // select the same walls but describe a different quantity.
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };

        // Held beyond the try so the geometric pass below can reuse it. Recomputing the room
        // volume for that pass would double the most expensive call in this method.
        Solid? roomSolid = null;

        try
        {
            var calculator = new SpatialElementGeometryCalculator(_doc, options);
            var results = calculator.CalculateSpatialElementGeometry(room);
            var solid = results.GetGeometry();

            roomSolid = solid;

            var seen = new HashSet<long>();
            var linkedCount = 0;

            foreach (Face face in solid.Faces)
            {
                IList<SpatialElementBoundarySubface> subfaces;
                try { subfaces = results.GetBoundaryFaceInfo(face); }
                catch { continue; }

                if (subfaces is null || subfaces.Count == 0) continue;

                var orientation = Classify(face);

                foreach (var subface in subfaces)
                {
                    ElementId hostId;
                    var linked = false;

                    try
                    {
                        var boundary = subface.SpatialBoundaryElement;
                        hostId = boundary.HostElementId;
                        linked = boundary.LinkInstanceId != ElementId.InvalidElementId;
                    }
                    catch
                    {
                        continue;
                    }

                    if (linked)
                    {
                        linkedCount++;
                        continue;
                    }

                    if (hostId == ElementId.InvalidElementId) continue;
                    if (!seen.Add(hostId.Value)) continue;

                    Sort(hostId, orientation, set);
                }
            }

            if (linkedCount > 0)
            {
                set.LinkedNotes.Add(
                    $"{linkedCount} bounding face(s) belong to a linked model and cannot be " +
                    "selected from here. Open the link to inspect them.");
            }
        }
        catch (Exception ex)
        {
            set.Notes.Add($"Boundary calculation failed: {ex.Message}");
            Log.Warn($"QA room boundary failed for {roomId.Value}: {ex.Message}");
        }

        // SECOND SOURCE for the walls. See the method for why one is not enough.
        AddBoundarySegmentWalls(room, options, set);

        // THIRD SOURCE, and the only one that asks the geometry rather than the room model.
        // Catches the wall that encloses the room without Revit calling it bounding - room
        // bounding switched off, a separation line doing the bounding, or a neighbour's wall
        // standing against the face. Those are the holes in the isolated view.
        AddEnclosingWalls(room, roomSolid, set);

        // Separation-line-only boundaries produce no wall at all, which is legitimate and
        // very confusing to look at. Say it rather than showing an empty selection.
        if (set.Walls.Count == 0)
            set.Notes.Add("No bounding WALLS - this room may be enclosed by room separation lines only.");

        if (set.Ceilings.Count == 0)
        {
            set.Notes.Add(
                "Nothing bounds this room from above. The finish engine falls back to the slab " +
                $"or roof above and records that in '{_settings.Finishes.CeilingSourceParameter}'; " +
                "there is simply no ceiling element to highlight.");
        }

        if (set.Floors.Count == 0)
            set.Notes.Add("Nothing bounds this room from below - no floor element to highlight.");

        if (includeSweeps) CollectSweeps(room, set);

        if (includeContents)
        {
            CollectOpenings(set);
            CollectContents(room, set);
        }

        return set;
    }

    /// <summary>
    /// Adds bounding walls the solid pass missed, from the room's own boundary segments.
    ///
    /// WHY TWO SOURCES ARE NEEDED
    ///   <see cref="SpatialElementGeometryCalculator"/> gives the authoritative answer for
    ///   FINISH AREAS, because it returns the actual faces the area is measured on. It is not
    ///   the authoritative answer for "which walls surround this room", and the difference
    ///   shows up as walls simply absent from the isolated view - the room looking open on one
    ///   side when it is not.
    ///
    ///   Three ways that happens in practice, all seen in real models: a wall that bounds the
    ///   room over so short a stretch that its subface is degenerate and carries no host id; a
    ///   wall whose room-side face is entirely consumed by an opening, leaving no face to
    ///   report; and a face whose subface info the calculator declines to return at all,
    ///   which it does without raising anything.
    ///
    ///   <c>GetBoundarySegments</c> is a different question asked of a different mechanism -
    ///   the room's plan boundary loop - so it fails in different places. Taking the UNION is
    ///   what makes the isolated view enclosed. The cost is that a wall touching the room only
    ///   at a corner can now come in, which is visible and harmless; a missing wall is neither.
    /// </summary>
    private void AddBoundarySegmentWalls(
        Room room, SpatialElementBoundaryOptions options, RoomFinishSet set)
    {
        IList<IList<BoundarySegment>> loops;

        try { loops = room.GetBoundarySegments(options); }
        catch { return; }

        if (loops is null) return;

        var known = new HashSet<long>(
            set.Walls.Concat(set.Floors).Concat(set.Ceilings).Select(id => id.Value));

        var added = 0;

        foreach (var loop in loops)
        {
            foreach (var segment in loop)
            {
                ElementId id;
                try { id = segment.ElementId; }
                catch { continue; }

                if (id == ElementId.InvalidElementId) continue;
                if (!known.Add(id.Value)) continue;

                // Room separation lines are the other thing this returns, and they are not
                // walls - isolating one shows an invisible line and no geometry.
                if (_doc.GetElement(id) is not Wall) continue;

                set.Walls.Add(id);
                added++;
            }
        }

        if (added > 0)
        {
            set.Notes.Add(
                $"{added} bounding wall(s) came from the room's boundary loop rather than from " +
                "its measured faces. They enclose the room but contribute no measurable finish " +
                "area on this side - typically a wall met at a corner, or one whose room face is " +
                "entirely taken up by an opening.");
        }
    }

    /// <summary>
    /// Adds walls that physically enclose the room but that Revit does not call bounding.
    ///
    /// THE BUG THIS FIXES
    ///   An isolated view built from the two boundary sources alone had holes in it: a stretch
    ///   of room with floor, ceiling and no wall above the floor edge. The wall was there in
    ///   the model the whole time. It was missing from the isolation because every earlier
    ///   pass asks the ROOM which walls it references, and a wall with Room Bounding switched
    ///   off - or one shadowed by a room separation line, or one belonging to the neighbour
    ///   and merely standing against the face - is referenced by no room at all.
    ///
    ///   <see cref="RoomWallFinder"/> asks the geometry instead. It is deliberately the LAST
    ///   pass: the two boundary sources carry provenance that matters for finish areas, and
    ///   a wall they found should keep their label rather than be relabelled by this one.
    /// </summary>
    private void AddEnclosingWalls(Room room, Solid? roomSolid, RoomFinishSet set)
    {
        if (!_settings.IncludeEnclosingWalls) return;

        try
        {
            var known = set.Walls.Concat(set.Floors).Concat(set.Ceilings).ToList();

            var finder = new RoomWallFinder(_doc, _settings.WallTouchToleranceMm);
            var extra = finder.Find(room, roomSolid, known);

            if (extra.Count == 0) return;

            foreach (var (id, _) in extra) set.Walls.Add(id);

            var inside = extra.Count(e => e.Source == WallSource.IntersectsVolume);
            var touching = extra.Count(e => e.Source == WallSource.TouchesBoundary);

            var parts = new List<string>();
            if (inside > 0) parts.Add($"{inside} standing inside the room volume");
            if (touching > 0) parts.Add($"{touching} touching a boundary face");

            set.Notes.Add(
                $"{extra.Count} enclosing wall(s) added by geometry rather than by the room's own " +
                $"boundary - {string.Join(", ", parts)}. Revit does not report these as bounding " +
                "this room, usually because Room Bounding is switched off on them or a room " +
                "separation line is doing the bounding instead. They are kept visible so the " +
                "isolated view is not left with a hole in the enclosure.");
        }
        catch (Exception ex)
        {
            // Never fatal. The boundary passes have already produced a usable answer, and
            // losing this one costs completeness, not correctness.
            Log.Warn($"QA: enclosing-wall pass failed for room {room.Id.Value}: {ex.Message}");
            set.Notes.Add($"Enclosing-wall check failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------- contents

    /// <summary>
    /// Doors, windows and openings cut into this room's bounding walls.
    ///
    /// TAKEN FROM THE WALLS, NOT FROM THE ROOM
    ///   The tempting alternative is to ask each door which rooms it connects, through
    ///   <c>FromRoom</c>/<c>ToRoom</c>. That is the right call for the door schedule and the
    ///   wrong one here, for two reasons. It is phase-dependent and returns null on the wrong
    ///   phase - the trap that once suppressed every reveal in the skirting engine. And it
    ///   answers a different question: a door in this room's wall belongs in this room's
    ///   isolated view whichever side it is deemed to open into, because otherwise the view
    ///   has an unexplained hole in it.
    /// </summary>
    private void CollectOpenings(RoomFinishSet set)
    {
        var wallCategory = (long)BuiltInCategory.OST_Walls;

        foreach (var wallId in set.Walls)
        {
            if (_doc.GetElement(wallId) is not Wall wall) continue;

            ICollection<ElementId> inserts;
            try { inserts = wall.FindInserts(true, false, true, true); }
            catch { continue; }

            foreach (var id in inserts)
            {
                var insert = _doc.GetElement(id);
                if (insert is null) continue;

                // Embedded walls come back from FindInserts too. They are already handled as
                // boundaries where they bound, and adding one here would isolate a curtain
                // wall someone has to scroll past.
                if ((insert.Category?.Id.Value ?? 0) == wallCategory) continue;

                set.Openings.Add(id);
            }
        }

        if (set.Openings.Count > 0)
            set.Openings.RemoveAll(id => set.Walls.Contains(id));
    }

    /// <summary>
    /// Casework and MEP standing in the room.
    ///
    /// A bounding-box pre-filter narrows the model down cheaply, then containment is decided
    /// by asking the room - never by the box alone. A box test would pull in the neighbour's
    /// kitchen units the moment a room is not rectangular, and the room in front of us right
    /// now is a good deal less than rectangular.
    /// </summary>
    private void CollectContents(Room room, RoomFinishSet set)
    {
        var categories = _settings.RoomContentCategories;
        if (categories.Length == 0) return;

        BoundingBoxXYZ? box;
        try { box = room.get_BoundingBox(null); }
        catch { return; }

        if (box is null) return;

        var floor = FloorElevation(room);
        var probeHeight = floor + _settings.ContentProbeHeight;

        IEnumerable<Element> candidates;

        try
        {
            var outline = new Outline(
                new XYZ(box.Min.X - 1.0, box.Min.Y - 1.0, box.Min.Z - 2.0),
                new XYZ(box.Max.X + 1.0, box.Max.Y + 1.0, box.Max.Z + 2.0));

            candidates = new FilteredElementCollector(_doc)
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .ToElements();
        }
        catch (Exception ex)
        {
            set.Notes.Add($"Room contents lookup failed: {ex.Message}");
            return;
        }

        foreach (var element in candidates)
        {
            BoundingBoxXYZ? elementBox;
            try { elementBox = element.get_BoundingBox(null); }
            catch { continue; }

            if (elementBox is null) continue;

            var centre = (elementBox.Min + elementBox.Max) / 2.0;

            // Plan position from the element, height from the ROOM. See
            // QaSettings.ContentProbeHeight for why the element's own Z is the wrong probe.
            var probe = new XYZ(centre.X, centre.Y, probeHeight);

            try
            {
                if (room.IsPointInRoom(probe)) set.Contents.Add(element.Id);
            }
            catch
            {
                // Undecidable containment: leave it out rather than isolate a unit from the
                // room next door, which would be worse than a slightly sparse view.
            }
        }
    }

    /// <summary>
    /// The room's floor in internal units - level elevation plus its own base offset, which
    /// is not zero in a sunken or raised room.
    /// </summary>
    private static double FloorElevation(Room room)
    {
        var elevation = 0.0;

        try { elevation = room.Level?.Elevation ?? 0.0; }
        catch { /* no level */ }

        try
        {
            var offset = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET);
            if (offset is { HasValue: true }) elevation += offset.AsDouble();
        }
        catch
        {
            // Unreadable offset; the level alone is close enough to probe from.
        }

        return elevation;
    }

    // ------------------------------------------------------------------ boundaries

    private enum Orientation { Side, Bottom, Top }

    /// <summary>
    /// Which way a face of the room solid points.
    ///
    /// The room solid's normals point OUT of the room, so its underside points down and its
    /// top points up. Classifying by the normal rather than by
    /// <c>SpatialElementBoundarySubface.SubfaceType</c> is deliberate: the normal is a fact
    /// about the geometry and cannot be absent, whereas SubfaceType has moved between API
    /// versions and throws on some sloped faces.
    ///
    /// 0.7 is a 45-degree cut. A sloped soffit reads as a ceiling, which is what it is.
    /// </summary>
    private static Orientation Classify(Face face)
    {
        try
        {
            var box = face.GetBoundingBox();
            var mid = new UV(
                (box.Min.U + box.Max.U) / 2.0,
                (box.Min.V + box.Max.V) / 2.0);

            var normal = face.ComputeNormal(mid);

            if (normal.Z > 0.7) return Orientation.Top;
            if (normal.Z < -0.7) return Orientation.Bottom;
            return Orientation.Side;
        }
        catch
        {
            return Orientation.Side;
        }
    }

    /// <summary>
    /// Files a bounding element under walls, floors or ceilings.
    ///
    /// Orientation decides, not category, and that matters: a room bounded from above by a
    /// FLOOR (the slab of the storey above) or by a ROOF belongs in the ceiling bucket,
    /// because that is the surface whose finish is the ceiling finish. Filing by category
    /// would put it under floors and highlight the wrong thing.
    /// </summary>
    private void Sort(ElementId id, Orientation orientation, RoomFinishSet set)
    {
        var element = _doc.GetElement(id);
        if (element is null) return;

        var category = element.Category?.Id.Value ?? 0;
        var isWall = category == (long)BuiltInCategory.OST_Walls;

        switch (orientation)
        {
            case Orientation.Bottom:
                set.Floors.Add(id);
                break;

            case Orientation.Top:
                set.Ceilings.Add(id);
                break;

            default:
                if (isWall) set.Walls.Add(id);
                else set.Walls.Add(id);   // columns, curtain panels: still a vertical face
                break;
        }
    }

    // --------------------------------------------------------------------- sweeps

    /// <summary>
    /// The skirting inside this room - the optional extra on the brief.
    ///
    /// Found by containment rather than by boundary: a board is not a bounding element of
    /// the room, it is an object standing in it. <c>Room.IsPointInRoom</c> is the only
    /// reliable test, and it is asked at the board's own centre lifted clear of the floor
    /// plane, where the answer is not a coin toss.
    /// </summary>
    private void CollectSweeps(Room room, RoomFinishSet set)
    {
        try
        {
            var inventory = SweepInventory.Collect(_doc, _settings);
            if (inventory.Count == 0) return;

            var box = room.get_BoundingBox(null);

            foreach (var board in inventory)
            {
                if (box is not null && !Overlaps(box, board.Box)) continue;

                var probe = new XYZ(board.Centre.X, board.Centre.Y, board.Centre.Z + 0.5);

                try
                {
                    if (room.IsPointInRoom(probe)) set.Sweeps.Add(board.Id);
                }
                catch
                {
                    // Undecidable containment; leave it out rather than highlight a board
                    // from the next room along.
                }
            }
        }
        catch (Exception ex)
        {
            set.Notes.Add($"Skirting lookup failed: {ex.Message}");
        }
    }

    private static bool Overlaps(BoundingBoxXYZ a, BoundingBoxXYZ b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
        a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
        a.Min.Z <= b.Max.Z + 1.0 && a.Max.Z + 1.0 >= b.Min.Z;

    // ----------------------------------------------------------------- parameters

    /// <summary>
    /// Reads back what the finish engine wrote. Read-only, and it is the point of the check:
    /// the highlight shows WHICH elements a number came from, so a wrong number can be
    /// traced to the wall that produced it.
    /// </summary>
    private void ReadFinishParameters(Room room, RoomFinishSet set)
    {
        var f = _settings.Finishes;

        foreach (var name in new[]
                 {
                     f.WallParameter, f.PaintParameter,
                     f.FloorParameter, f.NetFloorParameter,
                     f.CeilingParameter,
                 })
        {
            set.Finishes.Add((name, Area(room, name)));
        }

        var source = ParameterHelper.Find(room, f.CeilingSourceParameter)?.AsString();
        set.Finishes.Add((f.CeilingSourceParameter,
            string.IsNullOrWhiteSpace(source) ? "(not written)" : source));
    }

    /// <summary>
    /// An area parameter as m², or a reason it is not a number.
    ///
    /// "not bound" and "0.00 m²" mean completely different things - the first is an
    /// unconfigured model, the second is a measured zero - and collapsing them into a blank
    /// cell is how people conclude the engine is broken when it has never been set up.
    /// </summary>
    private static string Area(Element element, string name)
    {
        var parameter = ParameterHelper.Find(element, name);

        if (parameter is null) return "(not bound)";
        if (!parameter.HasValue) return "(no value)";
        if (parameter.StorageType != StorageType.Double) return parameter.AsValueString() ?? "-";

        return $"{Measure.ToSquareMetres(parameter.AsDouble()):0.00} m²";
    }

    // -------------------------------------------------------------------- helpers

    private string LevelName(Room room)
    {
        try { return room.Level?.Name ?? "-"; }
        catch { return "-"; }
    }

    private string Label(Room room)
    {
        var number = Text(room, BuiltInParameter.ROOM_NUMBER);
        var name = Text(room, BuiltInParameter.ROOM_NAME);

        return string.IsNullOrWhiteSpace(number) ? name : $"{number} — {name}";
    }

    private static string Text(Element element, BuiltInParameter parameter)
    {
        try { return element.get_Parameter(parameter)?.AsString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Zero-pads digit runs so "9" sorts before "10".</summary>
    private static string PadNumbers(string value)
    {
        if (string.IsNullOrEmpty(value)) return "￿";   // blanks last

        var result = new System.Text.StringBuilder(value.Length + 8);
        var digits = new System.Text.StringBuilder(8);

        foreach (var c in value)
        {
            if (char.IsDigit(c))
            {
                digits.Append(c);
                continue;
            }

            if (digits.Length > 0)
            {
                result.Append(digits.ToString().PadLeft(8, '0'));
                digits.Clear();
            }

            result.Append(c);
        }

        if (digits.Length > 0) result.Append(digits.ToString().PadLeft(8, '0'));

        return result.ToString();
    }
}
