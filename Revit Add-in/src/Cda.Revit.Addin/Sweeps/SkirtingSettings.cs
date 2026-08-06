using Autodesk.Revit.DB;
using Cda.Revit.Addin.Doors;

namespace Cda.Revit.Addin.Sweeps;

/// <summary>
/// Inputs for <see cref="SkirtingGenerator"/>. Separated from the engine so the rules a
/// project argues about - which rooms are wet, how close casework has to be to matter -
/// are editable without touching geometry code.
/// </summary>
public sealed class SkirtingSettings
{
    /// <summary>
    /// Matched against the family name AND the type name, so either
    /// "Skirtingboard_21-80mm" as a type under Wall_Sweep V6, or a family of that name,
    /// resolves.
    /// </summary>
    public string TypeName { get; init; } = "Skirtingboard_21-80mm";

    /// <summary>
    /// Rooms whose Name OR Department contains any of these (case-insensitive) get no
    /// skirting. Substring rather than equality, so "Bad" catches "Badeværelse" and
    /// "Toilet" catches "Toilet 2" - which is the point, but it also means a room called
    /// "Badminton" would be excluded. Deliberate: a missing board gets noticed, skirting
    /// glued into a wet room does not.
    /// </summary>
    public string[] ExcludedRoomKeywords { get; init; } = ["Bad", "Toilet"];

    /// <summary>
    /// Rooms whose name STARTS with this are outdoors, and get no skirting on any wall.
    ///
    /// These are the placeholder rooms drawn with room separation lines to enclose a
    /// terrace, balcony or entrance area so it can be scheduled. They are real Room
    /// elements with real bounding walls, so nothing else in this engine can tell them
    /// apart from an interior room - which is precisely how skirting ends up on the
    /// outside face of a building.
    ///
    /// Defaulted from <see cref="UdvendigSettings.ExteriorPrefix"/> rather than repeating
    /// the literal, so this tool and the door resolver cannot drift to different ideas of
    /// what "outside" is named. Prefix, not substring: 'Udvendig', 'Udvendig 1' and
    /// 'Udvendig 3' all match, while a room called 'Trappe udvendig belysning' does not.
    /// </summary>
    public string ExteriorRoomPrefix { get; init; } = new UdvendigSettings().ExteriorPrefix;

    /// <summary>Height above the room's floor, in internal feet. Zero is the brief.</summary>
    public double Offset { get; init; }

    public bool BreakAtOpenings { get; init; } = true;

    public bool BreakAtCasework { get; init; } = true;

    /// <summary>
    /// Wrap a board across the wall thickness exposed inside an opening - the jamb return.
    ///
    /// An opening with nothing in it leaves the wall's own thickness on show at floor level,
    /// and that face is skirted on site exactly like the wall it belongs to. Stopping the run
    /// at the jamb leaves a raw end and under-reports the board ordered.
    /// </summary>
    public bool WrapIntoReveals { get; init; } = true;

    /// <summary>
    /// Whether ordinary DOORS get reveal boards too. Off by default: a door's frame and
    /// architrave already cover the reveal, so skirting there would be modelled through the
    /// lining. Cased openings are handled regardless - see <see cref="CasedOpeningHints"/>.
    /// </summary>
    public bool WrapIntoDoorReveals { get; init; }

    /// <summary>
    /// A door-category insert whose family or type name contains one of these is treated as
    /// a cased opening - a doorway with no leaf - and does get reveal boards.
    ///
    /// Name matching is a heuristic and it is the weak point of this feature. There is no
    /// reliable API answer to "does this door have a leaf": the geometry differs per family,
    /// per manufacturer, per office. A name test is at least inspectable and correctable by
    /// whoever names the families.
    /// </summary>
    public string[] CasedOpeningHints { get; init; } =
        ["opening", "åbning", "aabning", "cased", "karm", "passage"];

    /// <summary>
    /// How close to the floor an opening must reach to get reveal boards, ~50 mm. This is
    /// what keeps windows out of it: a window sill is well above the skirting.
    /// </summary>
    public double RevealFloorTolerance { get; init; } = 0.164;

    /// <summary>
    /// Place a reveal board only when the far side of the opening is ALSO inside a placed
    /// room, and never when it emerges outdoors.
    ///
    /// A reveal board spans the wall's whole thickness, so an opening in an external wall
    /// pushes it clean through to the outside face - skirting on the outside of the
    /// building. That is what this stops. Skirting is an interior finish; an opening onto
    /// open air has an external reveal detail, not a skirting return.
    ///
    /// It also catches openings into unmodelled space - a shaft, a void, anywhere no room
    /// is placed - because "no room the far side" and "outdoors" are the same question.
    /// </summary>
    public bool InteriorRevealsOnly { get; init; } = true;

    /// <summary>
    /// Never place a board on the OUTSIDE face of a wall whose type Function is Exterior,
    /// Foundation or Retaining.
    ///
    /// This is the structural backstop to the <see cref="ExteriorRoomPrefix"/> name test,
    /// and it exists because that test can only catch outdoor areas someone remembered to
    /// name 'Udvendig'. A terrace modelled as a room called 'Altan', or a covered entrance
    /// called 'Indgang', is just as outdoors and just as invisible to a name match - but its
    /// boundary still lies on the exterior face of an exterior wall, and that is a fact
    /// about geometry rather than about naming discipline.
    ///
    /// Note what this deliberately does NOT do: an exterior wall's INTERIOR face still gets
    /// skirting. Every perimeter room in the building would otherwise lose its boards.
    /// </summary>
    /// <summary>
    /// OFF by default, and that is a deliberate reversal.
    ///
    /// It skipped a boundary segment when the wall type's Function said Exterior and the run
    /// lay on the orientation side. The flaw is that it trusts the Function parameter, which
    /// is set by hand and is wrong often enough to matter: an interior partition typed
    /// Exterior loses its skirting silently, and the brief is explicit that every valid
    /// interior wall must get a board.
    ///
    /// It is also unnecessary. A board is placed on a room's own boundary curve, so it is
    /// inside that room by construction - it can only end up outdoors if the ROOM is
    /// outdoors, and that is caught properly by <see cref="ExteriorRoomPrefix"/> and
    /// <see cref="RequireDepartment"/>. Excluding at the wall level could only ever produce
    /// false negatives on top of those.
    /// </summary>
    public bool SkipExteriorWallFaces { get; init; }

    /// <summary>
    /// Clearance either side of an opening. ZERO: the board abuts the frame exactly.
    ///
    /// This was 5 mm to keep coincident faces from flickering in rendered views. That is a
    /// real rendering artefact, but it is the wrong trade here - a 5 mm gap at every jamb is
    /// visible in plan, wrong on site, and it is what the door-frame screenshots are
    /// showing. Skirting is scribed tight to the architrave.
    ///
    /// Set to a small positive value if the coincident-face shimmer matters more than the
    /// joint being right.
    /// </summary>
    public double OpeningPad { get; init; }

    /// <summary>
    /// How far a casework unit may stand off the wall and still break the board, ~50 mm.
    /// A cabinet with a scribe gap behind it still gets skirting cut around it on site.
    /// </summary>
    public double CaseworkReach { get; init; } = 0.164;

    /// <summary>
    /// Top of the board above the floor, ~80 mm, matching Skirtingboard_21-80mm.
    ///
    /// THE SINGLE MOST IMPORTANT NUMBER HERE. Nothing breaks the run unless it comes down
    /// into this band. A window with a metre of wall under it does not touch the skirting,
    /// so the board runs straight through beneath it; only openings that actually reach the
    /// floor - doorways, cased openings, full-height glazing - interrupt it.
    ///
    /// Blocking on every insert regardless of height is what produced boards that appeared
    /// to be cut at random: each window silently deleted a window's width of skirting.
    /// </summary>
    public double BoardHeight { get; init; } = 0.262;

    /// <summary>
    /// Board thickness off the wall, ~20 mm. Used as the corner extension - see
    /// <see cref="ExtendAtCorners"/>.
    /// </summary>
    public double BoardDepth { get; init; } = 0.066;

    /// <summary>
    /// Close corners with a real mitre length, and as a BUTT JOINT so nothing overlaps.
    ///
    /// Room boundary curves stop on the finish face, so two boards turning a corner both
    /// end exactly at the corner point - leaving a notch the size of the board's own depth.
    /// The fix has to close that notch without the two boards occupying the same space.
    ///
    /// Two rules do it. The extension length is the true mitre run for the angle,
    /// <c>depth / tan(interior / 2)</c>, not a flat depth - that is only correct at 90°, and
    /// on a 45° wall it overshoots by more than double. And the extension is applied to ONE
    /// side of each corner only: the run that ENDS there grows to fill the notch, the run
    /// that STARTS there butts against its face. Extending both would close the notch and
    /// then bury one board inside the other.
    /// </summary>
    public bool MitreAtCorners { get; init; } = true;

    /// <summary>
    /// Clip every board to the stretch that is genuinely inside the room, by asking the room.
    ///
    /// The boundary curve a board is built from SHOULD already be inside the room, so in
    /// principle this can never fire. In practice it does: a host wall running past the
    /// room, a boundary that follows the wall rather than the room at a junction, or a
    /// family whose geometry reaches beyond its own placement curve all put material outside
    /// the room, and none of them are visible from the curve alone.
    ///
    /// So this stops reasoning about why and asks Revit directly - IsPointInRoom along the
    /// board - then keeps only the part that answers yes. It is the last gate before
    /// placement and it cannot be argued with by a wall.
    /// </summary>
    public bool ConfineToRoom { get; init; } = true;

    /// <summary>
    /// How far off the wall face to probe when testing containment, ~10 mm. Far enough to
    /// clear the face itself, where IsPointInRoom is a coin toss, and well inside the board.
    /// </summary>
    public double ContainmentProbe { get; init; } = 0.033;

    /// <summary>Spacing of containment samples along a board, ~100 mm.</summary>
    public double ContainmentSample { get; init; } = 0.328;

    /// <summary>
    /// Ceiling on the computed mitre run, as a multiple of <see cref="BoardDepth"/>. A very
    /// shallow angle sends <c>depth / tan(interior / 2)</c> toward infinity; clamping keeps
    /// a near-straight join from growing a board by a metre.
    /// </summary>
    public double MaxMitreFactor { get; init; } = 3.0;

    /// <summary>
    /// Rooms with no Department value are excluded entirely.
    ///
    /// Department is this project's marker for "this room has been designed". A room
    /// without one is a placeholder, a survey artefact, or something nobody has decided
    /// about yet - and skirting it produces quantities for space that is not real.
    /// </summary>
    public bool RequireDepartment { get; init; } = true;

    /// <summary>
    /// The ONLY categories allowed to interrupt a run.
    ///
    /// Mechanical and electrical equipment are deliberately absent. A radiator, a convector
    /// or a socket is fixed to the wall ABOVE or IN FRONT of the skirting - the board runs
    /// straight behind it, and cutting around it both looks wrong and under-orders material.
    /// Casework is different: a base unit sits on the floor, tight to the wall, and the
    /// board genuinely stops.
    /// </summary>
    public BuiltInCategory[] BlockingCategories { get; init; } = [BuiltInCategory.OST_Casework];

    /// <summary>
    /// Names that force an element to pass the board through even if its category says it
    /// should block.
    ///
    /// The belt to <see cref="BlockingCategories"/>' braces. Radiator and convector families
    /// are routinely authored in the wrong category - Casework and Specialty Equipment both
    /// happen - and a category-only rule then cuts the skirting behind every radiator in the
    /// building. Since a radiator is wall-hung above the board in every case, refusing by
    /// name costs nothing and covers the mis-typed families.
    /// </summary>
    public string[] NeverBlockHints { get; init; } =
    [
        "radiator", "konvektor", "convector", "heater", "varme",
        "stikkontakt", "socket", "outlet",

        // Void cutters filed as Casework. 'Floor Void' turned up cutting six separate
        // boards in the kitchen: it is a cutting tool, not a cabinet, and nothing about it
        // exists to stop a skirting board.
        "void", "udsparing", "hulning", "opening",
    ];

    /// <summary>
    /// Categories that can NEVER interrupt a run, whatever else is configured. Checked on
    /// the element itself, so it holds even when a family is filed under a blocking
    /// category by mistake.
    ///
    /// Every one of these is either wall-hung above the board or stands clear in front of
    /// it. The skirting runs behind, unbroken, which is both how it is built and what keeps
    /// the ordered length right.
    /// </summary>
    public BuiltInCategory[] NeverBlockCategories { get; init; } =
    [
        BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_SpecialityEquipment,
        BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_LightingFixtures,
        BuiltInCategory.OST_LightingDevices,
        BuiltInCategory.OST_DuctTerminal,
        BuiltInCategory.OST_Sprinklers,
        BuiltInCategory.OST_CommunicationDevices,
        BuiltInCategory.OST_DataDevices,
        BuiltInCategory.OST_FireAlarmDevices,
        BuiltInCategory.OST_SecurityDevices,
        BuiltInCategory.OST_TelephoneDevices,
        BuiltInCategory.OST_NurseCallDevices,
    ];

    /// <summary>
    /// How far past the wall face a door's architrave may sit and still count as jamb, ~30 mm.
    /// </summary>
    public double JambMargin { get; init; } = 0.1;

    /// <summary>
    /// Shorter than this and the piece is not placed, ~15 mm. Sub-fragments come from two
    /// blockers nearly meeting; a 3 mm board is a modelling artefact, not a component.
    ///
    /// Was 50 mm, which threw away real boards: the report showed a 40 mm return in Stue
    /// dropped for being under it. 40 mm of skirting is small but it exists on site, and a
    /// visible gap is worse than a short piece.
    /// </summary>
    public double MinimumRun { get; init; } = 0.05;

    /// <summary>
    /// Two obstructions closer together than this count as one, ~150 mm.
    ///
    /// A row of kitchen units has a millimetre or two between each carcass. Measured
    /// separately they leave a sliver of bare wall in every joint, and the engine puts a
    /// 20 mm board in it - visible in the model as isolated stubs, and wrong, because a run
    /// of joinery has no skirting between its units.
    ///
    /// 150 mm because that is roughly the shortest piece anyone would actually cut and fit.
    /// Anything less is a modelling artefact of where one cabinet ends and the next begins.
    /// </summary>
    public double BlockerBridge { get; init; } = 0.492;

    /// <summary>
    /// Written to Comments on every instance this tool places.
    ///
    /// It is the re-run guard: without it a second run puts a second board along every wall,
    /// coplanar with the first, invisible in any view and double in every schedule. It is
    /// also how someone deletes exactly what this tool made, and nothing else.
    /// </summary>
    public const string Stamp = "DKSI skirting";

    /// <summary>How far past the wall face to probe when testing which side the room is on.</summary>
    public const double SideProbe = 0.1;

    /// <summary>Height above the room's base to probe at, avoiding the floor plane itself.</summary>
    public const double ProbeHeight = 1.5;
}
