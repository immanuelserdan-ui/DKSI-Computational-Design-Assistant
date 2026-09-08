using Autodesk.Revit.DB;
using Cda.Revit.Addin.Doors;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Heating;

/// <summary>
/// Inputs for <see cref="RadiatorGenerator"/>. Separated from the engine for the same reason
/// <see cref="Sweeps.SkirtingSettings"/> is: the numbers a project argues about - how far off
/// the floor, how far below the sill, how close to a door frame is too close - are the ones
/// that change, and they should be changeable without opening geometry code.
///
/// WHERE THE NUMBERS COME FROM
///   DS 469 and the traditional Danish practice it codifies are performance-based about
///   heating output and prescriptive only about air movement: a radiator wants free air
///   below it and free air above it, or the convection loop that makes it work stops. The
///   two clearances below are that requirement, and nothing else here is a code figure.
///
///   The 120 mm floor clearance is not invented: it is what the radiators already standing
///   in this model carry in their own 'Offset From Floor' parameter. Reading it off the
///   model rather than off a standard means the tool agrees with the drawings the office
///   has already issued, which matters more than agreeing with a round number.
/// </summary>
public sealed class RadiatorSettings
{
    /// <summary>
    /// Matched against the family name AND the type name, whitespace-normalised. The
    /// normalisation is load-bearing rather than defensive: this model contains a type
    /// literally named "L 1000  H 600" with two spaces, sitting beside "L 1000 H 600" with
    /// one, and an exact-match lookup finds one of them and silently ignores the other.
    /// </summary>
    public string FamilyName { get; init; } = "Radiator_P No Void V6";

    /// <summary>
    /// Rooms whose name STARTS with this are outdoors and get no radiator, however many
    /// windows look into them.
    ///
    /// Defaulted from <see cref="UdvendigSettings.ExteriorPrefix"/> rather than repeating the
    /// literal, so this tool, the skirting engine and the door resolver cannot drift to
    /// different ideas of what "outside" is called.
    /// </summary>
    public string ExteriorRoomPrefix { get; init; } = new UdvendigSettings().ExteriorPrefix;

    /// <summary>
    /// Rooms whose Name or Department contains any of these get no radiator. Empty by
    /// default: an unheated room is a design decision, not something a name should imply.
    /// The mechanism is here because some project will want stores and shafts left out.
    /// </summary>
    public string[] ExcludedRoomKeywords { get; init; } = [];

    /// <summary>
    /// A room with no Name gets no radiator.
    ///
    /// This model has them: room 28308486 is 4.3 m2, properly enclosed, carries a Department
    /// and a computed volume - and its Name is an empty string. Revit is perfectly happy with
    /// that and will report it as "2" in any schedule.
    ///
    /// An unnamed room has not been designed yet. It is a space somebody enclosed and has not
    /// decided about, and heating it is a decision the model does not support. Placing there
    /// looks like a considered choice on a drawing and is not one.
    /// </summary>
    public bool RequireRoomName { get; init; } = true;

    /// <summary>
    /// A room with no room tag anywhere in the model gets no radiator.
    ///
    /// A stricter test than the name, and a different one. The Name is a property of the room
    /// element; the TAG is evidence that somebody put the room on a drawing and meant it. An
    /// untagged room is routinely a leftover - a room left behind when walls moved, a
    /// duplicate sitting under a real one, a space enclosed by accident when a separation line
    /// was drawn - and those are exactly the rooms whose radiators nobody would ever notice.
    ///
    /// Any view counts. A room tagged on one storey plan and nowhere else is still a room
    /// somebody has drawn deliberately.
    /// </summary>
    public bool RequireRoomTag { get; init; } = true;

    // -- vertical rules --------------------------------------------------------

    /// <summary>
    /// Bottom of the panel above finished floor. Free air has to get in underneath or the
    /// convection loop never starts.
    ///
    /// 120 mm is this model's own figure, read from the 'Offset From Floor' parameter on the
    /// radiators already placed. It sits inside the 100 mm minimum that Danish HVAC practice
    /// asks for, which is the test that matters - see <see cref="MinFloorClearance"/>.
    /// </summary>
    public double FloorClearance { get; init; } = Measure.FromMillimetres(120.0);

    /// <summary>
    /// The floor clearance below which the placement is reported as non-compliant rather
    /// than merely tight. Never used to POSITION anything - only to judge what was placed.
    /// </summary>
    public double MinFloorClearance { get; init; } = Measure.FromMillimetres(100.0);

    /// <summary>
    /// Preferred air gap between the top of the panel and the underside of the sill. The
    /// upper end of the 50-100 mm range practice asks for, because the tool should aim at
    /// the comfortable answer and only fall back towards the minimum when the wall makes it.
    /// </summary>
    public double SillClearance { get; init; } = Measure.FromMillimetres(100.0);

    /// <summary>
    /// The gap below which warm air cannot get past the sill. A window that cannot give the
    /// panel this much is the floor-to-ceiling case in disguise, and goes down the same
    /// relocation path.
    /// </summary>
    public double MinSillClearance { get; init; } = Measure.FromMillimetres(50.0);

    /// <summary>
    /// A sill this close to the floor is reported as floor-to-ceiling glazing rather than as
    /// a clearance failure. Both relocate; they read differently in a report, and a designer
    /// scanning for "why is this radiator not under its window" wants the distinction.
    /// </summary>
    public double FloorToCeilingSill { get; init; } = Measure.FromMillimetres(300.0);

    // -- horizontal rules ------------------------------------------------------

    /// <summary>
    /// The share of the window's width the panel should aim to cover. One, because a
    /// radiator's job under a window is to meet the down-draught across the whole pane; a
    /// panel covering half the glass leaves half the cold air to fall past it.
    ///
    /// It is a TARGET, not a constraint. The catalogue is a ladder of discrete lengths, so
    /// the engine takes the longest rung that fits the free wall and reports what fraction
    /// it actually achieved.
    /// </summary>
    public double WidthFraction { get; init; } = 1.0;

    /// <summary>
    /// How much longer than the window the panel may be. A radiator slightly wider than its
    /// window is ordinary and often unavoidable on a coarse ladder; one twice the width is a
    /// different design decision and should not happen by accident.
    /// </summary>
    public double OverhangAllowance { get; init; } = Measure.FromMillimetres(200.0);

    /// <summary>Clear wall left between the panel and any door, window or opening reveal.</summary>
    public double OpeningClearance { get; init; } = Measure.FromMillimetres(50.0);

    /// <summary>
    /// Clear wall left at an internal corner. Not comfort - buildability: a panel hard into
    /// a corner cannot be lifted off its brackets, and the pipe drop has nowhere to go.
    /// </summary>
    public double CornerClearance { get; init; } = Measure.FromMillimetres(50.0);

    /// <summary>Clear wall left beside a column, casework run or another radiator.</summary>
    public double ObstacleClearance { get; init; } = Measure.FromMillimetres(50.0);

    /// <summary>
    /// How far off the window's centreline the panel may slide to find free wall before the
    /// window is treated as unservable and goes to relocation.
    ///
    /// Sliding is a real answer, not a fudge: a window set close to a return wall has its
    /// free wall on one side only, and a panel nudged 100 mm still meets the glass. Sliding
    /// FAR is not - past this, the panel is no longer under the window in any useful sense
    /// and pretending otherwise hides the problem instead of reporting it.
    /// </summary>
    public double MaxSlide { get; init; } = Measure.FromMillimetres(150.0);

    /// <summary>
    /// Shortest panel worth placing. Below this the catalogue has nothing that helps and a
    /// stub under the window is worse than an honest report saying the wall is full.
    /// </summary>
    public double MinLength { get; init; } = Measure.FromMillimetres(400.0);

    // -- what counts as in the way ---------------------------------------------

    /// <summary>
    /// Categories whose instances block a panel when they stand against the wall inside the
    /// panel's own height band.
    ///
    /// THE HEIGHT BAND IS THE WHOLE POINT. A wall cupboard at 1800 mm and a base unit at
    /// 900 mm are both "casework against this wall" in plan and only one of them is in the
    /// way. Testing in plan alone is how a tool refuses to place a radiator that would have
    /// fitted perfectly underneath a high-level shelf.
    /// </summary>
    public BuiltInCategory[] BlockingCategories { get; init; } =
    [
        BuiltInCategory.OST_Casework,
        BuiltInCategory.OST_Furniture,
        BuiltInCategory.OST_FurnitureSystems,
        BuiltInCategory.OST_Columns,
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_SpecialityEquipment,
        BuiltInCategory.OST_MechanicalEquipment,
    ];

    /// <summary>
    /// How far off the wall face an element may stand and still block. Anything further out
    /// than the panel's own depth plus this is furniture in the room, not an obstruction on
    /// the wall, and a radiator can sit behind it.
    /// </summary>
    public double BlockingReach { get; init; } = Measure.FromMillimetres(100.0);

    // -- relocation ------------------------------------------------------------

    /// <summary>
    /// Move the panel to another wall in the same room when it cannot go under its window,
    /// rather than reporting and placing nothing.
    ///
    /// ON, because the alternative loses the heat. A room with a full-height terrace door
    /// still has to be heated, and the drawing that comes out of this tool is more use with
    /// a flagged radiator on the nearest exterior wall than with a gap and a note.
    /// </summary>
    public bool RelocateWhenBlocked { get; init; } = true;

    /// <summary>
    /// Prefer an exterior wall when relocating. This is the thermal half of the rule: the
    /// cold surface is the envelope, so a panel that has left its window should stay on the
    /// envelope rather than move to a warm partition on the other side of the room.
    /// </summary>
    public bool PreferExteriorOnRelocation { get; init; } = true;

    /// <summary>
    /// How much a relocated panel's distance from its window counts against it, per metre,
    /// when scoring candidate walls. Exterior-wall preference is worth roughly three metres
    /// of walking away, which is what keeps a panel on the envelope across a normal room and
    /// lets it give up and take a partition in a long thin one.
    /// </summary>
    public double DistancePenalty { get; init; } = 1.0;

    /// <summary>Score bonus for a candidate face on an exterior wall.</summary>
    public double ExteriorBonus { get; init; } = 3.0;

    // -- bookkeeping -----------------------------------------------------------

    /// <summary>
    /// Written into Extensible Storage on everything this tool places, which is how a
    /// re-run finds and removes its own work. Invisible in the UI, unschedulable, and not
    /// editable by hand - see <see cref="ElementStamp"/> for why that matters.
    /// </summary>
    public const string Stamp = "DKSI radiator";

    /// <summary>
    /// The Comments prefix earlier tools used, recognised on read only. A model that has
    /// been through an older build still gets its radiators cleaned up on Regenerate.
    /// </summary>
    public const string LegacyStamp = "DKSI radiator:";

    /// <summary>
    /// Instance parameter names carrying the panel's height above the floor, in preference
    /// order. English and Danish, because a family loaded from a supplier is either.
    /// </summary>
    public string[] FloorOffsetNames { get; init; } =
        ["Offset From Floor", "Offset from Floor", "Højde over gulv", "Hoejde over gulv"];

    /// <summary>Type parameter names carrying the panel's length. Cross-check only - see
    /// <see cref="RadiatorCatalogue"/> for why these are not trusted to position anything.</summary>
    public string[] LengthNames { get; init; } =
        ["Radiator Length", "Length", "Længde", "Laengde"];

    /// <summary>Type parameter names carrying the panel's height.</summary>
    public string[] HeightNames { get; init; } =
        ["Radiator Height", "Height", "Højde", "Hoejde"];

    /// <summary>
    /// Instance parameter names controlling how far the panel stands off its wall.
    ///
    /// 'Distance to wall' is a shared parameter this radiator family already carries, sitting
    /// at zero on every instance, with a 25 mm 'Offset From Wall' on the type behind it. It is
    /// the family's own control on the exact axis the seating problem lives on - so before
    /// concluding that a wall-hosted family cannot be moved off the side its host chose, it is
    /// worth asking the family whether it will move itself.
    /// </summary>
    public string[] WallOffsetNames { get; init; } =
        ["Distance to wall", "Offset From Wall", "Offset from Wall", "Afstand til væg"];

    /// <summary>Type parameter names carrying the panel's depth front-to-back.</summary>
    public string[] DepthNames { get; init; } =
        ["Radiator Thickness", "Depth", "Dybde", "Tykkelse"];

    /// <summary>Tolerance for "these two measurements agree".</summary>
    public double Tolerance { get; init; } = Measure.FromMillimetres(1.0);
}
