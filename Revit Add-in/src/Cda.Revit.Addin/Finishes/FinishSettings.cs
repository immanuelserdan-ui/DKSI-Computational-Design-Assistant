using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Port of the input block and constants of "RoomFinishAreas 7-23-26.dyn".
///
/// OFFICE STANDARD (BIM manual): one finish material per wall/floor/ceiling element
/// (separate 20 mm finish walls joined to base walls). Split Face + Paint is not the
/// office standard; walls modelled that way report their room-side layer/paint material
/// only.
/// </summary>
public sealed class FinishSettings
{
    // ---- parameter names (IN[1], IN[2], IN[3], IN[7], IN[8]) ------------------

    public string WallParameter { get; init; } = "Wall Finish Area";
    public string FloorParameter { get; init; } = "Floor Finish Area";
    public string CeilingParameter { get; init; } = "Ceiling Finish Area";

    /// <summary>
    /// Room-clipped PAINTED-only wall area, written to Rooms and Walls.
    ///
    /// DO NOT SCHEDULE THIS IN A WALL MATERIAL TAKEOFF. This comment used to say the
    /// opposite - "schedule this, filtered to 'Material: As Paint = Yes'" - and that advice
    /// produces quantities that are wrong twice over.
    ///
    ///   ONE. It is an INSTANCE parameter on the wall, and a material takeoff has one row per
    ///   (wall, material). Revit has nothing to split an element parameter by, so it prints
    ///   the identical figure on every material row of the same wall. A wall painted VBP on
    ///   one face and VBJ on the other shows the same number twice; summing the column
    ///   double-counts it, and a third paint material would triple it. Two byte-identical
    ///   areas against two different materials is the signature, and it reads as a plausible
    ///   duplicate rather than as an error.
    ///
    ///   TWO. With <see cref="RoomConsistentPaint"/> on - the default - the repeated figure is
    ///   not even the wall's total. <c>PaintShare</c> writes only the OWNING room's share, so
    ///   the neighbouring room's face is not in that number at all. The row labelled with the
    ///   neighbour's material is the owner's area wearing the neighbour's name.
    ///
    ///   The two errors partly cancel: on a symmetric partition, one room's share printed
    ///   twice sums to roughly the wall's true total. The grand total can therefore look right
    ///   while every row is misattributed, which is why this survived so long.
    ///
    /// FOR PAINT QUANTITIES, RUN 'Paint Takeoff' and read 'DKSI Paint Takeoff by Room'
    /// (<see cref="Schedules.PaintTakeoffBuilder"/>). It places one row per (room, surface,
    /// host, material), so both faces of a shared wall appear against the rooms they face and
    /// the row count matches the painted face count. It is built from the engine's own
    /// per-room results, so it cannot drift from the CSV.
    ///
    /// WHAT THIS PARAMETER IS STILL FOR: the value on the ROOM, which carries that room's own
    /// full painted wall area and is unaffected by all of the above. Schedule Rooms, not
    /// walls. The value on the WALL is the owner's share, useful for tinting or filtering a
    /// view and not for summing.
    ///
    /// <see cref="WallParameter"/> has no such problem and belongs in a Wall Material Takeoff:
    /// it is the element's own whole finish face, never apportioned.
    /// </summary>
    public string PaintParameter { get; init; } = "Wall Paint Area";

    /// <summary>
    /// The ceiling equivalent of <see cref="PaintParameter"/>: the part of the ceiling
    /// surface carrying PAINT, excluding the substrate the paint sits on.
    ///
    /// This is the number a painting contractor is priced against, and it is NOT
    /// <see cref="CeilingParameter"/>. When the fallback chain measures a bare slab soffit,
    /// most of that area is unpainted concrete - real ceiling surface, but nothing to paint.
    /// Summing the full area against a paint rate over-reports by the unpainted remainder.
    /// </summary>
    public string CeilingPaintParameter { get; init; } = "Ceiling Paint Area";

    /// <summary>
    /// The floor equivalent of <see cref="PaintParameter"/>: the part of the floor surface
    /// carrying an identified finish material, excluding bare substrate.
    ///
    /// NOT a duplicate of <see cref="FloorParameter"/>, and the run reports prove it - across
    /// every recorded run the Floor bucket splits 34 painted rows to 19 unpainted, and in the
    /// 2026-08-03 test model 45.000 m2 of "EM Floor" substrate sits against 27.876 m2 of
    /// identified finish (VBF, VFK). Summing 'Floor Finish Area' against a finish rate
    /// over-reports by that substrate.
    ///
    /// An unpainted floor row is also the modelling flag described on <see cref="MaterialKey"/>:
    /// either genuinely bare substrate, or a floor finish nobody has modelled yet. Both are
    /// worth seeing, and neither is visible while the only floor number is the total.
    /// </summary>
    public string FloorPaintParameter { get; init; } = "Floor Paint Area";

    /// <summary>
    /// The building-code walkable area: room footprint counted ONLY where a real floor or
    /// foundation slab sits beneath it. Revit's read-only Room.Area never deducts floor
    /// openings; this does.
    ///
    /// NOTE ON THE APPARENT DUPLICATION WITH <see cref="FloorParameter"/>: the two are equal
    /// on any room with no mezzanine, because every site that adds to this also adds the same
    /// amount to the Floor material ledger. The one thing this has that the other does not is
    /// the mezzanine exclusion - and the comparison that matters is against Revit's built-in
    /// Room.Area, which never deducts open-to-below at all. Keep it bound; drop it from
    /// schedules while no mezzanines are modelled.
    /// </summary>
    public string NetFloorParameter { get; init; } = "Net Floor Area";

    // ---- room identity carried onto the finish elements ----------------------

    /// <summary>
    /// Apartment, from the room's <b>Department</b>. Written onto every wall, floor,
    /// ceiling and roof this room's finishes were measured on.
    ///
    /// THE POINT OF ALL THREE OF THESE: a material takeoff schedules ELEMENTS, and an
    /// element has no idea which room it faces. Without these three fields a finish
    /// schedule can say "12.4 m² of gips" but not which room it belongs to, so it cannot
    /// be grouped, sorted or issued per apartment — which is the whole of what a Roombook
    /// schedule does. The room already knows; this copies the answer to where the schedule
    /// can see it.
    /// </summary>
    public string ApartmentParameter { get; init; } = "Lejlighed";

    /// <summary>Room Number, carried onto the finish elements. See <see cref="ApartmentParameter"/>.</summary>
    public string RoomNumberParameter { get; init; } = "Rum nr";

    /// <summary>Room Name, carried onto the finish elements. See <see cref="ApartmentParameter"/>.</summary>
    public string RoomNameParameter { get; init; } = "Rum";

    // ---- the per-room paint takeoff -------------------------------------------------
    //
    // Carried by the generated takeoff elements, not by walls. A wall material takeoff has
    // one row per (wall, material) and no room dimension, so a wall touching two rooms has
    // one 'Rum' slot for two correct answers - which is why a room shows only the walls it
    // OWNS rather than every face that touches it. These name the columns of the takeoff
    // that does have a room dimension.

    public string PaintAreaParameter { get; init; } = "Paint Area";

    public string PaintSurfaceParameter { get; init; } = "Paint Surface";

    public string PaintMaterialParameter { get; init; } = "Paint Material";

    /// <summary>The TYPE of the element the row was measured on - "IV-Gips-100mm".</summary>
    public string PaintTypeParameter { get; init; } = "Paint Type";

    /// <summary>
    /// The ELEMENT ID of the wall, floor or ceiling a takeoff row was measured on.
    ///
    /// WHY AN ID AND NOT JUST THE TYPE NAME
    ///   <see cref="PaintTypeParameter"/> tells you the row was measured on an
    ///   "EM_Int - 100mm". It does not tell you WHICH one, and a flat has a dozen. That gap is
    ///   what makes the takeoff untraceable: the row is a number nobody can walk back to a
    ///   surface, which is exactly what an audit or a quantity dispute needs to do.
    ///
    ///   It also cannot be recovered by clicking. The rows are geometry-less DirectShapes on
    ///   purpose, so selecting one highlights nothing in the model - correct behaviour for an
    ///   invisible data carrier, and useless for finding the wall. This is the answer to that:
    ///   Select by ID reaches the element, and <see cref="Overlay.PaintHighlight"/> uses it to
    ///   draw the row's painted face on request.
    ///
    /// TEXT, NOT INTEGER, and deliberately. Revit element ids are 64-bit and a Revit Integer
    /// parameter is 32-bit, so a large model would silently overflow. It is also read far more
    /// often than it is arithmetic on.
    ///
    /// Blank on the arithmetic fallback bucket, which has no single host - the same rule and
    /// the same reason as <see cref="PaintTypeParameter"/>.
    /// </summary>
    public string PaintHostParameter { get; init; } = "Paint Host Id";

    // THE OWNING ROOM'S TOTALS ON A TAKEOFF ROW use the SIX EXISTING parameters above -
    // WallParameter / PaintParameter and their floor and ceiling equivalents - which are now
    // bound to Generic Models as well as to Rooms and the element categories.
    //
    // An earlier revision invented 'Room Paint Total' and 'Room Finish Total' for this and was
    // wrong to: those six already exist and are already named for exactly this quantity, so a
    // synonym only creates two fields that can disagree. Someone who knows 'Wall Paint Area'
    // from a room's Properties should meet the same name on the takeoff row.
    //
    // Revit's own 'Room: Wall Paint Area' field cannot supply this. That is a room-RELATIONSHIP
    // lookup resolved from an element's location, and the takeoff rows are geometry-less by
    // design, so they sit in no room and the column is blank in every model forever. Writing
    // the value onto the row is the only mechanism available.
    //
    // NEVER SUM THOSE COLUMNS ON THE TAKEOFF. The value is the room's total repeated on every
    // row of that room, so totalling it multiplies the room by its row count - Bad's six rows
    // would report 6 x 18.45 m2. It is a REFERENCE figure for comparing a row against its room;
    // grouping by room with a footer is the safe way to get per-room subtotals, because those
    // are computed from the rows themselves.

    /// <summary>
    /// Text parameter on Rooms recording WHICH element the ceiling area came from -
    /// "ceiling", "slab above", "roof", or "none", suffixed "(fallback)" when nothing
    /// bounded the room and the priority chain had to look overhead itself.
    ///
    /// Schedule it beside "Ceiling Finish Area". A ceiling quantity whose provenance is
    /// invisible is a quantity nobody can check, and once the fallback exists an area of
    /// 0 and an area measured off a roof look identical in the schedule without it.
    /// </summary>
    public string CeilingSourceParameter { get; init; } = "Ceiling Area Source";

    // ---- behaviour flags (IN[4], IN[5]) --------------------------------------

    /// <summary>Applies only to walls measured via the arithmetic FALLBACK.</summary>
    public bool SubtractOpenings { get; init; } = true;

    /// <summary>Applies only to walls measured via the arithmetic FALLBACK.</summary>
    public bool SubtractCasework { get; init; } = true;

    /// <summary>Geometric measurement is standard; the graph hard-wires this on.</summary>
    public bool UseGeometric { get; init; } = true;

    /// <summary>Sloped-ceiling upper-limit fix is standard; the graph hard-wires this on.</summary>
    public bool AutoAdjustLimits { get; init; } = true;

    /// <summary>
    /// For a room with NO top boundary at all, search overhead in the
    /// <see cref="CeilingFallbackTiers"/> order instead of reporting zero. See
    /// <see cref="CeilingFallbackResolver"/>.
    ///
    /// Turn OFF to make an unmodelled ceiling read as 0 - the honest signal that the model
    /// is incomplete, which is what a design-stage QC check wants. Leave ON for quantities
    /// off a shell-and-core model, where the slab soffit IS the ceiling finish.
    /// </summary>
    public bool UseCeilingFallback { get; init; } = true;

    /// <summary>
    /// Rooms whose name STARTS with this are outdoors and get no finish measured.
    ///
    /// WHY THIS HAD TO EXIST
    ///   These are the placeholder rooms drawn with room separation lines to enclose a
    ///   terrace, balcony or entrance so it can be scheduled for AREA. They are real Room
    ///   elements with real bounding walls, and nothing else in this engine could tell them
    ///   apart from an interior room - so it measured them, and what it measured was the
    ///   OUTSIDE FACE of the building's exterior walls, in interior paint materials, into an
    ///   interior paint takeoff.
    ///
    ///   It was worse than a stray row. Because <c>WriteRoomIdentity</c> gives an element to
    ///   whichever room claimed the most area, a wall between a small interior room and a
    ///   large terrace was credited entirely to the terrace - so a genuinely interior wall's
    ///   paint area appeared in the schedule hosted by 'Udvendig'.
    ///
    ///   Defaulted from <see cref="Doors.UdvendigSettings.ExteriorPrefix"/> rather than
    ///   repeating the literal, so this engine, the skirting generator and the door resolver
    ///   cannot drift to different ideas of what 'outside' is named.
    /// </summary>
    public string ExteriorRoomPrefix { get; init; } = new Doors.UdvendigSettings().ExteriorPrefix;

    /// <summary>
    /// Skip exterior placeholder rooms entirely. ON, and it should stay on.
    ///
    /// Turning it off restores measurement of terrace and balcony walls into the interior
    /// finish parameters. The identity tie-break still refuses to let an exterior room win,
    /// so the labelling stays correct either way - but the AREAS would be wrong again, which
    /// is the more expensive half of the problem.
    /// </summary>
    public bool SkipExteriorRooms { get; init; } = true;

    /// <summary>
    /// Write each element's paint area as the OWNING room's share rather than the sum across
    /// every room the element serves. ON.
    ///
    /// A partition between two rooms carries the paint of both faces, and the element gets a
    /// single 'Rum'. Summed as-is, a takeoff grouped by room bills one room for its
    /// neighbour's paint. This makes the number agree with the label it is filed under.
    ///
    /// The label itself is chosen by PAINT, not by wall area, so the room this apportions to is
    /// the room the paint is actually in. Apportioning is done per SURFACE, so a slab that is a
    /// floor for one room and a ceiling for another never mixes the two pools.
    ///
    /// Turn OFF only to reproduce an older takeoff. The trade is deliberate and reported: the
    /// other room's share stops appearing in an ELEMENT schedule, so element schedules no
    /// longer sum to the building's painted area. The ROOM parameters carry every room's own
    /// total and remain the authority for quantities either way.
    /// </summary>
    public bool RoomConsistentPaint { get; init; } = true;

    /// <summary>Measure the painted jamb/head returns inside door openings.</summary>
    public bool CaptureDoorReveals { get; init; } = true;

    /// <summary>
    /// Measure the painted jamb, head AND SILL returns inside window openings.
    ///
    /// These were previously missed entirely: the reveal pass only ever collected doors,
    /// so a window's returns belonged to nobody. The room-boundary sweep cannot see them
    /// either — their normals are perpendicular to the room plane, so they are not one of
    /// the two big room faces — and the opening itself is deducted from the wall. The
    /// paint on the sides of every window was simply absent from the takeoff.
    ///
    /// The sill comes along for free: it is a wall return like the jambs, just facing up.
    /// </summary>
    public bool CaptureWindowReveals { get; init; } = true;

    /// <summary>
    /// Deduct the part of each reveal covered by the door's own lining/frame/leaf, so only
    /// the visible painted return is counted. False reports the whole painted face.
    /// </summary>
    public bool DeductFrameCoverage { get; init; } = true;

    // ---- tolerances (internal feet) ------------------------------------------

    /// <summary>Plane-matching tolerance, ~6 mm.</summary>
    public const double CoplanarTolerance = 0.02;

    /// <summary>
    /// How far off a wall face to step when asking which room that face fronts, ~30 mm.
    ///
    /// Far enough to clear the face itself, where <c>IsPointInRoom</c> is a coin toss, and
    /// well short of anything that could be standing against the wall. The same reasoning and
    /// roughly the same distance as the skirting engine's side probe.
    /// </summary>
    public const double FaceProbe = 0.1;

    /// <summary>Thin-solid thickness used for face intersection.</summary>
    public const double ExtrudeThickness = 0.10;

    /// <summary>Height above a room's base searched for ceilings/roofs.</summary>
    public const double ScanBand = 20.0;

    /// <summary>Margin added above the highest ceiling point when raising a limit.</summary>
    public const double LimitMargin = 0.5;

    /// <summary>
    /// Reveal-coverage slab thickness, deliberately thin (~6 mm) so a lining sitting
    /// against the reveal fully spans it, while a leaf offset into the opening does not
    /// get falsely counted. Larger would under-deduct thin linings.
    /// </summary>
    public const double RevealSlabThickness = 0.02;

    /// <summary>Tolerance around a door's bounding box when collecting reveal faces.</summary>
    public const double RevealPad = 0.05;

    /// <summary>
    /// Below this (internal square feet, ~1 cm2) an overhead tier counts as having found
    /// nothing, so a boolean sliver at a wall face cannot claim to be the ceiling and stop
    /// the chain before the real one is reached.
    /// </summary>
    public const double CeilingFallbackMinimum = 0.01;

    /// <summary>
    /// Internal square feet to square metres.
    ///
    /// RETAINED ONLY AS A COMPILE-TIME CONSTANT. Every runtime conversion now goes through
    /// <see cref="Infrastructure.Measure.ToSquareMetres"/>, which asks UnitUtils rather than
    /// trusting a literal. This stays because a const cannot call a method, and removing it
    /// would break any external graph still referencing it - but nothing in this add-in
    /// should use it.
    /// </summary>
    [Obsolete("Use Measure.ToSquareMetres - it goes through UnitUtils instead of a literal.")]
    public const double SqFtToSqM = 0.09290304;

    // ---- surface bucket names (also the CSV "Surface" column) ----------------

    public const string SurfaceWalls = "Walls";
    public const string SurfaceFloor = "Floor";
    public const string SurfaceCeiling = "Ceiling";
    public const string SurfaceReveals = "Reveals";

    // ---- ceiling provenance --------------------------------------------------

    public const string SourceCeiling = "ceiling";
    public const string SourceSlabAbove = "slab above";
    public const string SourceRoof = "roof";
    public const string SourceOther = "other";
    public const string SourceNone = "none";

    public static readonly string[] CeilingSourceOrder =
        [SourceCeiling, SourceSlabAbove, SourceRoof, SourceOther];

    /// <summary>
    /// The room-boundary priority chain, in order, for rooms nothing bounds from above.
    /// Ceiling first, then the underside of the slab above, then the roof - the first tier
    /// that measures anything wins outright and the rest are not consulted.
    ///
    /// This is the same order Revit's own volume clipping produces for rooms that ARE
    /// bounded (it stops at the nearest bounding element), stated explicitly so the two
    /// paths cannot disagree.
    /// </summary>
    public static readonly (string Source, BuiltInCategory Category)[] CeilingFallbackTiers =
    [
        (SourceCeiling, BuiltInCategory.OST_Ceilings),
        (SourceSlabAbove, BuiltInCategory.OST_Floors),
        (SourceRoof, BuiltInCategory.OST_Roofs),
    ];
}
