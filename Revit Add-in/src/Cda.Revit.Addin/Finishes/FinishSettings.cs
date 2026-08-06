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
    /// Room-clipped PAINTED-only wall area, written to Rooms and Walls. Schedule this
    /// (not "Wall Finish Area") in a Wall Material Takeoff filtered to
    /// 'Material: As Paint = Yes' to match the painted CSV rows per material.
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
