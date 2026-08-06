using Autodesk.Revit.DB;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Sweeps;

namespace Cda.Revit.Addin.Qa;

/// <summary>
/// Tunables for the QA checks, kept out of the inspectors for the same reason
/// <see cref="SkirtingSettings"/> is kept out of the generator: the numbers a project
/// argues about should be editable without touching geometry code.
///
/// WHY THIS BORROWS RATHER THAN REDECLARES
///   A QA check that disagrees with the tool it audits is worse than no check at all - it
///   reports failures the generator was right to produce. So the room rules, the board
///   height and the opening tolerances are taken FROM <see cref="SkirtingSettings"/>, not
///   restated here. Only the numbers that are genuinely about reporting - how big a gap has
///   to be before it is worth a row in the grid - are new.
/// </summary>
public sealed class QaSettings
{
    /// <summary>
    /// The rules the skirting generator works to. The checks apply the SAME room
    /// qualification, so a wet room is never reported as missing its boards.
    /// </summary>
    public SkirtingSettings Skirting { get; init; } = new();

    /// <summary>Names of the finish parameters this add-in writes onto rooms.</summary>
    public FinishSettings Finishes { get; init; } = new();

    /// <summary>
    /// Shortest uncovered stretch worth reporting, ~50 mm.
    ///
    /// Deliberately larger than the generator's <see cref="SkirtingSettings.MinimumRun"/> of
    /// 15 mm. The generator's number decides whether to PLACE a piece; this one decides
    /// whether to make a human look at something. A 20 mm sliver at a mitre is a modelling
    /// artefact, and a QA list that reports fifty of them is a list nobody reads.
    /// </summary>
    public double MinReportableGap { get; init; } = 0.164;

    /// <summary>
    /// How close to the end of a boundary segment a gap must start or finish before it is
    /// called a CORNER gap rather than a mid-run gap, ~150 mm.
    ///
    /// The distinction matters because the two have different causes and different fixes. A
    /// mid-run gap is usually a blocker that should not have blocked - a mis-categorised
    /// radiator, a void family filed as casework. A corner gap is a join failure: two runs
    /// that should have mitred into each other and did not, which is the known slanted-wall
    /// symptom.
    /// </summary>
    public double CornerReach { get; init; } = 0.492;

    /// <summary>
    /// How far off the room's boundary curve a board may sit and still count as being ON
    /// that face, ~60 mm.
    ///
    /// THIS IS THE MOST IMPORTANT NUMBER IN THE SWEEP CHECK. A wall between two rooms
    /// carries a board on each face, and both project onto the same stretch of the same
    /// boundary line. Without a proximity test the check reads the neighbour's board as
    /// this room's and reports a wall as covered when this side is bare - a false pass,
    /// which is the one kind of error a QA tool must not make.
    ///
    /// Boundaries are taken at <see cref="SpatialElementBoundaryLocation.Finish"/>, so a
    /// board on this side sits within its own depth of the curve while the far side's board
    /// is a wall thickness away.
    /// </summary>
    public double FaceProximity { get; init; } = 0.197;

    /// <summary>
    /// Vertical window around the room's floor in which a board counts, ~400 mm up and
    /// ~100 mm down.
    ///
    /// Keeps a crown moulding, a dado rail, and the skirting of the storey above out of the
    /// answer. All three are real wall sweeps at the wrong height, and all three would
    /// otherwise mark a bare wall as covered.
    /// </summary>
    public double BandAbove { get; init; } = 1.31;

    /// <summary>See <see cref="BandAbove"/>. Small negative allowance for boards modelled
    /// fractionally below the floor plane.</summary>
    public double BandBelow { get; init; } = 0.328;

    /// <summary>
    /// Fraction of a wall face that must be bare before it is reported as having NO skirting
    /// rather than a gap. Below this it is a gap in an existing run.
    /// </summary>
    public double MissingThreshold { get; init; } = 0.9;

    /// <summary>
    /// Report gaps at all, as distinct from wholly missing runs. Off makes the check a pure
    /// "which walls have no skirting" sweep, which is the faster conversation to have first
    /// on a model that has never been through the tool.
    /// </summary>
    public bool ReportGaps { get; init; } = true;

    /// <summary>
    /// Also treat native <see cref="WallSweep"/> elements as skirting.
    ///
    /// This project places skirting as a COMPONENT family, not a native sweep - see
    /// <see cref="SkirtingPlacer"/>. But a model can contain both: boards drawn by hand
    /// before the tool existed, boards from a consultant, or a wall type with a sweep built
    /// into its structure. Counting native sweeps too is what stops the check reporting
    /// hand-modelled skirting as missing.
    /// </summary>
    public bool CountNativeWallSweeps { get; init; } = true;

    /// <summary>
    /// Also treat any component whose family or type name matches
    /// <see cref="SkirtingSettings.TypeName"/> as skirting, even without this add-in's stamp.
    ///
    /// A board placed by hand from the same family is real skirting on site. Reporting it as
    /// missing because it carries no stamp would train people to ignore the tool.
    /// </summary>
    public bool CountUnstampedComponents { get; init; } = true;

    /// <summary>Categories a sweep can legitimately be filed under, for the name-match sweep.</summary>
    public BuiltInCategory[] ComponentCategories { get; init; } =
    [
        BuiltInCategory.OST_GenericModel,
        BuiltInCategory.OST_Casework,
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_CurtainWallPanels,
        BuiltInCategory.OST_SpecialityEquipment,
    ];

    /// <summary>
    /// What counts as the CONTENTS of a room, for the isolated view.
    ///
    /// WHY THE ISOLATED VIEW NEEDS THESE AT ALL
    ///   Isolating only the boundary leaves a box of bare walls with holes in it. It is
    ///   geometrically correct and almost impossible to read: with the doors gone there is no
    ///   way to tell which opening is a doorway and which is a hole where a window should be,
    ///   and with the fittings gone there is nothing to orient against. Adding the room's own
    ///   contents is what turns the isolation from a diagram into somewhere you can navigate.
    ///
    ///   Doors and windows are NOT in this list - they come from the bounding walls
    ///   themselves, which is both cheaper and more accurate than a containment test on
    ///   something embedded in a wall.
    /// </summary>
    public BuiltInCategory[] RoomContentCategories { get; init; } =
    [
        BuiltInCategory.OST_Casework,
        BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_LightingFixtures,
        BuiltInCategory.OST_LightingDevices,
        BuiltInCategory.OST_DuctTerminal,
        BuiltInCategory.OST_SpecialityEquipment,
        BuiltInCategory.OST_Furniture,
        BuiltInCategory.OST_FurnitureSystems,
    ];

    /// <summary>
    /// How far above the room's floor to probe when asking whether a fitting stands in this
    /// room, ~500 mm.
    ///
    /// The centre of a bounding box is the obvious probe point and it is wrong for exactly
    /// the things that matter here: a ceiling-mounted diffuser's centre is above the room's
    /// upper limit, and a floor-standing unit's centre can sit below a raised floor. Probing
    /// from the element's plan centre at a fixed height inside the room answers the question
    /// actually being asked - "is this fitting in this room?" - rather than "is its centroid".
    /// </summary>
    public double ContentProbeHeight { get; init; } = 1.64;

    /// <summary>
    /// How far outside the room's boundary faces the section box sits, in MILLIMETRES.
    ///
    /// Held in millimetres rather than internal feet because it is a number someone types
    /// into the window, and a setting the user reads in the units they think in is one they
    /// can check. Converted on use.
    ///
    /// A thousand is enough to show the bounding walls in full thickness plus the reveal of
    /// any opening, and little enough that the neighbouring room stays out.
    /// </summary>
    public double SectionBoxOffsetMm { get; init; } = 1000.0;
}
