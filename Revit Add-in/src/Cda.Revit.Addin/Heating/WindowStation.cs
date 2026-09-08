using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Linings;

namespace Cda.Revit.Addin.Heating;

/// <summary>
/// One window, reduced to everything a radiator under it needs to know: which wall, how far
/// along it, how high the sill is, and which room it faces.
///
/// SAME SHAPE AS <see cref="Opening"/>, DELIBERATELY NOT THE SAME CLASS. Opening exists to
/// answer a question about lining rectangles and carries a dozen lining parameters to do it.
/// This answers a question about free wall under a sill. They agree on the coordinate system
/// - u along the wall, z absolute - and share <see cref="WallAxis"/> and <see cref="Span"/>,
/// which is the part worth sharing.
/// </summary>
public sealed class WindowStation
{
    private WindowStation(
        FamilyInstance window, Wall wall, WallAxis axis, XYZ tangent, XYZ normal,
        Span u, double sillZ, double headZ, Room room, double roomSide, double floorZ)
    {
        Window = window;
        Wall = wall;
        Axis = axis;
        Tangent = tangent;
        Normal = normal;
        U = u;
        SillZ = sillZ;
        HeadZ = headZ;
        Room = room;
        RoomSide = roomSide;
        FloorZ = floorZ;
    }

    public FamilyInstance Window { get; }
    public Wall Wall { get; }
    public WallAxis Axis { get; }

    /// <summary>Unit vector along the wall. The u axis.</summary>
    public XYZ Tangent { get; }

    /// <summary>Unit vector out of the wall, in plan. Positive v is towards +normal.</summary>
    public XYZ Normal { get; }

    /// <summary>The window's own extent along the wall, measured from its geometry.</summary>
    public Span U { get; }

    public double UCentre => (U.Lo + U.Hi) / 2.0;
    public double Width => U.Length;

    /// <summary>Underside of the window, absolute. Measured, not read off Sill Height.</summary>
    public double SillZ { get; }

    public double HeadZ { get; }

    /// <summary>The room the window faces into - the room the radiator belongs to.</summary>
    public Room Room { get; }

    /// <summary>
    /// +1 when the room is on the +<see cref="Normal"/> side of the wall, -1 when it is on
    /// the other. Everything that has to face into the room rather than out of the building
    /// multiplies by this.
    /// </summary>
    public double RoomSide { get; }

    /// <summary>Finished floor level of that room, absolute.</summary>
    public double FloorZ { get; }

    public List<string> Warnings { get; } = [];

    public string Label()
    {
        var mark = Window.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
        return $"{Window.Id.Value} [{(string.IsNullOrEmpty(mark) ? "-" : mark)}] " +
               $"{SafeName(Window.Symbol)}";
    }

    /// <summary>
    /// Builds a station, or explains in <paramref name="reason"/> why this window cannot have
    /// one. A null return is an ordinary outcome for plenty of windows in a real model:
    /// rooflights have no wall, curtain panels have no host, and a window into a terrace
    /// placeholder faces nothing that needs heating.
    /// </summary>
    public static WindowStation? Of(
        Document doc, FamilyInstance window, RadiatorSettings settings, out string reason)
    {
        reason = string.Empty;

        if (window.Host is not Wall wall)
        {
            reason = "not hosted in a wall";
            return null;
        }

        var axis = WallAxis.Of(wall);
        if (axis?.Direction is null)
        {
            // A curved wall has no single tangent, so 'centred under the window' and 'this
            // much free wall' stop being one-dimensional questions. Reported rather than
            // approximated: a panel planned on a chord of an arc is wrong by an amount that
            // depends on the radius, and nothing downstream would notice.
            reason = "host wall is curved or has no location curve";
            return null;
        }

        var tangent = axis.Direction;
        var normal = Geometry.NormalOf(tangent);

        var extents = Geometry.Extents(window, tangent, normal, axis.Origin);
        if (extents is null)
        {
            reason = "window has no solid geometry to measure";
            return null;
        }

        var (u, _, z) = extents.Value;

        var level = doc.GetElement(window.LevelId) as Level
                    ?? doc.GetElement(wall.LevelId) as Level;

        var room = FacingRoom(doc, window, wall, normal, z, out var side);
        if (room is null)
        {
            // Covers both "nothing on either side" and "a room was found but would not
            // confirm the point back" - the second is rarer and means an unenclosed or
            // badly-bounded room, which is worth the same look as the first.
            reason = "no room could be confirmed on either side of the window";
            return null;
        }

        if (IsExterior(room, settings))
        {
            reason = $"faces '{RoomText(room, BuiltInParameter.ROOM_NAME)}', an exterior placeholder";
            return null;
        }

        if (IsExcluded(room, settings))
        {
            reason = $"room '{RoomText(room, BuiltInParameter.ROOM_NAME)}' is excluded by name";
            return null;
        }

        var floorZ = FloorElevation(room, level);

        var station = new WindowStation(
            window, wall, axis, tangent, normal, u, z.Lo, z.Hi, room, side, floorZ);

        station.CrossCheckSill(level);
        station.CrossCheckWidth();

        return station;
    }

    // ---------------------------------------------------------------- cross-checks

    /// <summary>
    /// Compares the measured sill against the window's Sill Height parameter.
    ///
    /// They legitimately differ: Sill Height is to the rough opening, and the measured value
    /// is to the bottom of the frame, which is what a radiator actually has to clear. The
    /// measured value therefore wins. A LARGE difference is worth reporting anyway, because
    /// it usually means the family's insertion plane is not where the schedule thinks it is.
    /// </summary>
    private void CrossCheckSill(Level? level)
    {
        if (level is null) return;

        var sill = Window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
        if (sill is null || !sill.HasValue) return;

        var declared = level.Elevation + sill.AsDouble();

        if (Math.Abs(declared - SillZ) > Measure.FromMillimetres(50.0))
        {
            Warnings.Add(
                $"Sill Height puts the sill at {Measure.ToMillimetres(declared):0} mm, " +
                $"the geometry at {Measure.ToMillimetres(SillZ):0} mm. Clearance was taken " +
                "from the geometry.");
        }
    }

    private void CrossCheckWidth()
    {
        var declared = Linings.Opening.NumberOfAny(Window, ["Width", "Bredde", "Rough Width"]);

        if (declared.Value is null or <= 0) return;

        if (Math.Abs(declared.Value.Value - Width) > Measure.FromMillimetres(100.0))
        {
            Warnings.Add(
                $"'{declared.Source}' says {Measure.ToMillimetres(declared.Value.Value):0} mm wide, " +
                $"the geometry measures {Measure.ToMillimetres(Width):0} mm. The panel was sized " +
                "and centred on the geometry.");
        }
    }

    // ---------------------------------------------------------------- room lookup

    /// <summary>
    /// Which room the window looks into.
    ///
    /// PROBED, NOT REASONED ABOUT. <see cref="Wall.Orientation"/> is a property of how the
    /// wall was drawn, not of which side is indoors - a wall drawn the other way round has
    /// its "exterior" face inside the building, and nothing in the model objects. So both
    /// sides are asked, and the answer is whichever side has a real room on it.
    ///
    /// The probe sits at mid-window height rather than at the floor, because a room's lower
    /// boundary can be raised above the slab and a floor-level probe then lands outside it.
    /// </summary>
    private static Room? FacingRoom(
        Document doc, FamilyInstance window, Wall wall, XYZ normal, Span z,
        out double side)
    {
        side = -1.0;

        var centre = Linings.Opening.LocationPoint(window);
        var height = (z.Lo + z.Hi) / 2.0;
        var reach = SafeWidth(wall) / 2.0 + Measure.FromMillimetres(300.0);

        var phase = PhaseOf(window, doc);

        Room? best = null;
        var bestScore = 0;

        foreach (var sign in new[] { -1.0, 1.0 })
        {
            var probe = new XYZ(
                centre.X + normal.X * reach * sign,
                centre.Y + normal.Y * reach * sign,
                height);

            var room = RoomAt(doc, probe, phase);
            if (room is null) continue;

            // A real interior room beats an exterior placeholder, which beats nothing. The
            // placeholder is still recorded, so a window that only faces a terrace reports
            // "faces a placeholder" rather than "no room found" - a different problem with a
            // different fix.
            var score = IsPlaceholder(room) ? 1 : 2;

            if (score <= bestScore) continue;

            bestScore = score;
            best = room;
            side = sign;
        }

        // ONE AUTHORITY, ASKED ONCE.
        //
        // Two things have been tried here and both were removed. A tie-break on
        // Wall.Orientation, which describes the direction the wall was drawn in and says
        // nothing about which side is indoors. Then a confirmation pass calling
        // IsPointInRoom on the winning probe point - the exact point GetRoomAtPoint had just
        // resolved to this room - which rejected twelve windows outright in a model where
        // every one of them was fine. Same point, two APIs, opposite answers.
        //
        // A second opinion is only worth having when it is better informed than the first.
        // GetRoomAtPoint answered the question that was asked, at the point it was asked
        // about; re-asking a different API bought nothing but a new way to fail.
        return best;
    }

    private static bool IsPlaceholder(Room room) =>
        RoomText(room, BuiltInParameter.ROOM_NAME)
            .StartsWith("Udvendig", StringComparison.OrdinalIgnoreCase);

    private static bool TryPointInRoom(Room room, XYZ point)
    {
        try { return room.IsPointInRoom(point); }
        catch { return false; }
    }

    private static Room? RoomAt(Document doc, XYZ point, Phase? phase)
    {
        try
        {
            var room = phase is not null
                ? doc.GetRoomAtPoint(point, phase)
                : doc.GetRoomAtPoint(point);

            return room is { Area: >= 0 } && room.Location is not null ? room : null;
        }
        catch
        {
            return null;
        }
    }

    private static Phase? PhaseOf(Element element, Document doc)
    {
        try
        {
            var parameter = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
            return parameter is null ? null : doc.GetElement(parameter.AsElementId()) as Phase;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- helpers

    public static bool IsExterior(Room room, RadiatorSettings settings) =>
        RoomText(room, BuiltInParameter.ROOM_NAME)
            .StartsWith(settings.ExteriorRoomPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsExcluded(Room room, RadiatorSettings settings)
    {
        if (settings.ExcludedRoomKeywords.Length == 0) return false;

        var name = RoomText(room, BuiltInParameter.ROOM_NAME);
        var department = RoomText(room, BuiltInParameter.ROOM_DEPARTMENT);

        return settings.ExcludedRoomKeywords.Any(keyword =>
            name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            department.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public static double FloorElevation(Room room, Level? level)
    {
        try
        {
            var baseOffset = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET)?.AsDouble() ?? 0.0;
            if (level is not null) return level.Elevation + baseOffset;

            if (room.Level is not null) return room.Level.Elevation + baseOffset;
        }
        catch
        {
            // Fall through to the bounding box.
        }

        try { return room.get_BoundingBox(null)?.Min.Z ?? 0.0; }
        catch { return 0.0; }
    }

    public static string RoomText(Room room, BuiltInParameter parameter)
    {
        try { return room.get_Parameter(parameter)?.AsString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public static string RoomLabel(Room room)
    {
        var label = $"{RoomText(room, BuiltInParameter.ROOM_NUMBER)} " +
                    $"{RoomText(room, BuiltInParameter.ROOM_NAME)}".Trim();

        return label.Length > 0 ? label : room.Id.Value.ToString();
    }

    private static double SafeWidth(Wall wall)
    {
        try { return wall.Width; }
        catch { return Measure.FromMillimetres(200.0); }
    }

    private static string SafeName(Element? element)
    {
        try { return element?.Name ?? "?"; }
        catch { return "?"; }
    }
}
