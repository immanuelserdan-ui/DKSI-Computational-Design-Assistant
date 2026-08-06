using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Linings;

/// <summary>The three lining edges an opening can carry.</summary>
public enum LiningSide
{
    Top,
    Left,
    Right,
}

/// <summary>
/// The configuration block from Resolve-Lining-Clashes_v1.0.py.
/// </summary>
public sealed class LiningSettings
{
    // ---- the office rule -----------------------------------------------------

    /// <summary>
    /// True = the office rule. BOTH openings uncheck the face that touches, so a ticked
    /// "Lining Top/Left/Right" carries a definite meaning: *nothing touches this side*.
    /// The checkbox states adjacency, not ownership. Each opening still banks its own
    /// uncovered remnant in "Lining Change" independently - a 2076 mm door jamb met by a
    /// 976 mm window keeps 1100 mm, while the window's own 976 mm face keeps nothing.
    ///
    /// False = ownership instead: exactly one of the pair keeps the lining, decided by
    /// <see cref="CategoryPriority"/>, ties on lower ElementId. Fallback, not the rule.
    /// </summary>
    public bool SymmetricUncheck { get; init; } = true;

    /// <summary>Only consulted when <see cref="SymmetricUncheck"/> is false. Higher keeps.</summary>
    public IReadOnlyDictionary<long, int> CategoryPriority { get; init; } = new Dictionary<long, int>
    {
        [(long)BuiltInCategory.OST_Windows] = 2,
        [(long)BuiltInCategory.OST_Doors] = 1,
    };

    /// <summary>
    /// How an opening's "Left" lining edge is located along the wall.
    /// </summary>
    public enum Handedness
    {
        /// <summary>
        /// "Left" is always the same direction along the wall axis, for every opening.
        ///
        /// This is the office rule, calibrated against wall 28377592: windows 1876, 1884
        /// and 1888 are placed hand-flipped while 1875, 1877, 1881, 1883, 1887 and every
        /// door are not, and the flip follows no geometric pattern - it is incidental to
        /// how each was placed. Letting it drive the parameter mapping made the edge that
        /// meets a door come out as "Right" on one side of the door and "Left" on the
        /// other, which is exactly the reported fault.
        /// </summary>
        WallAxis,

        /// <summary>
        /// "Left" follows the instance's own HandOrientation, so a hand-flipped placement
        /// swaps which world edge the family calls Left. Correct only if the families are
        /// authored so the lining parameters mirror with the instance.
        /// </summary>
        InstanceHand,
    }

    /// <summary>
    /// Whether a hand-flipped placement swaps the meaning of Lining Left / Lining Right.
    /// See <see cref="Handedness"/> for why WallAxis is the default.
    /// </summary>
    public Handedness HandednessSource { get; init; } = Handedness.WallAxis;

    /// <summary>
    /// Which side of the family's own X axis carries the "Lining Left" geometry.
    /// +1 = "Lining Left" sits at family +X, i.e. on the HandOrientation side.
    ///
    /// +1 follows from the office rule "the front of the door is the face you see standing
    /// outside the room". Only hand-flipping mirrors a family along the wall, so
    /// HandOrientation alone tracks where that geometry lands - flipping the facing swaps
    /// inside/outside and does not move it.
    ///
    /// CALIBRATION: dry-run one door with a window hard against its left jamb. If the
    /// report says the LEFT edge was blocked, this is right. If it says RIGHT, set -1.
    /// One check settles it for the whole office.
    /// </summary>
    public int LeftIsFamilyPlusX { get; init; } = 1;

    /// <summary>
    /// Should a neighbour whose master "Lining YN" is unchecked still block?
    ///
    /// True, and this matters. The office rule is purely geometric: if a door side touches
    /// a window side at zero distance, that side's lining comes off regardless of whether
    /// the window itself carries lining. On the validation wall both windows have
    /// "Lining YN" off, so gating on it found zero clashes and the tool did nothing.
    /// </summary>
    public bool LiningOffCanBlock { get; init; } = true;

    // ---- master "Lining YN" propagation, door -> touching windows ------------

    /// <summary>
    /// A door decides the lining for the windows it physically touches. Direct
    /// door-to-window contact only - never chains window-to-window, and never affects a
    /// window that touches no door.
    /// </summary>
    public bool PropagateMasterFromDoors { get; init; } = true;

    /// <summary>
    /// True = mirror the door both ways: a touching window is checked when the door is
    /// checked and unchecked when it is not. This is what removes the manual ticking.
    /// False = only ever uncheck.
    /// </summary>
    public bool MirrorMasterBothWays { get; init; } = true;

    // ---- material propagation, door -> touching windows ----------------------

    /// <summary>
    /// The first letter of these codes is the element (D door, W window) and the rest is
    /// the material, so a door's code converts to the window's by swapping the prefix:
    /// DDL -> WDL, DYL -> WYL.
    /// </summary>
    public bool MaterialFromDoors { get; init; } = true;

    public string DoorPrefix { get; init; } = "D";
    public string WindowPrefix { get; init; } = "W";

    /// <summary>Exceptions to the prefix rule, keyed on the upper-case door code.</summary>
    public IReadOnlyDictionary<string, string> MaterialOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // ---- family schema -------------------------------------------------------

    public IReadOnlyDictionary<LiningSide, string> SideYn { get; init; } = new Dictionary<LiningSide, string>
    {
        [LiningSide.Top] = "Lining Top YN",
        [LiningSide.Left] = "Lining Left YN",
        [LiningSide.Right] = "Lining Right YN",
    };

    public IReadOnlyDictionary<LiningSide, string> SideLength { get; init; } = new Dictionary<LiningSide, string>
    {
        [LiningSide.Top] = "Lining Top",
        [LiningSide.Left] = "Lining Left",
        [LiningSide.Right] = "Lining Right",
    };

    public string Change { get; init; } = "Lining Change";
    public string Master { get; init; } = "Lining YN";
    public string DoorMaterial { get; init; } = "Door Material";
    public string WindowMaterial { get; init; } = "Window Material";

    public IReadOnlyList<string> Total { get; init; } = ["Lining Length (Door)", "Lining Length (Window)"];

    /// <summary>
    /// "Lining Top/Left/Right" collapse to 0 when their checkbox is off, so they CANNOT be
    /// used to measure the opening. These are plain dimension reporters, gated by nothing.
    /// </summary>
    public IReadOnlyList<string> Width { get; init; } = ["Door Width", "Window Width"];

    public IReadOnlyList<string> Height { get; init; } = ["Door Height", "Window Height"];

    /// <summary>Elements whose Comments contain this marker are left completely alone.</summary>
    public string SkipMarker { get; init; } = "#nolining-auto";

    /// <summary>
    /// Should an opening that carries no lining parameters block its neighbours?
    ///
    /// False = Dynamo parity. The graph dropped those openings entirely, so they never
    /// blocked anything; keeping that means lining results match the graph exactly.
    ///
    /// They are still loaded either way, so a touching door can still drive their
    /// Window Material and Lining YN - those rules are about contact, not about lining
    /// parameters. Set true only if you decide such openings should also take a reveal.
    /// </summary>
    public bool NoLiningParamsCanBlock { get; init; }

    // ---- tolerances, in millimetres ------------------------------------------

    /// <summary>Max clear gap between two linings that still counts as touching.</summary>
    public double GapToleranceMm { get; init; } = 60.0;

    /// <summary>Leftover lining shorter than this is discarded as a sliver.</summary>
    public double MinRemnantMm { get; init; } = 50.0;

    /// <summary>Treat a side as clashing only if at least this much is covered.</summary>
    public double MinBlockMm { get; init; } = 1.0;

    /// <summary>Two wall location lines count as one line within this distance.</summary>
    public double CollinearToleranceMm { get; init; } = 10.0;

    /// <summary>Beyond this, an intersection is a modelling error, not rounding noise.</summary>
    public double OverlapWarnMm { get; init; } = 5.0;

    /// <summary>How far a neighbour may sit inside an edge and still count as touching.</summary>
    public double TouchSlackMm { get; init; } = 5.0;
}
