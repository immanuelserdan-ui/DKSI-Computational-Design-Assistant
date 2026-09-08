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
    /// skirting. Substring rather than equality, so "Toilet" catches "Toilet 2" - which is
    /// the point, but it also means a room whose name merely contains the word is excluded
    /// too. Deliberate: a missing board gets noticed, skirting glued into a wet room does not.
    ///
    /// EMPTY AS OF 2026-08-14, at the office's instruction: wet rooms are skirted like any
    /// other room, and Bad and Toilet are treated the same.
    ///
    /// It previously held ["Bad", "Toilet"], and that rule was excluding 'Bad 4' in T05 - a
    /// real 4.8 m2 bathroom, placed and enclosed - together with every wall face bounding it.
    /// The symptom was 2.4 m of a partition with no board, which reads as missing skirting
    /// rather than as a rule being applied, and cost a long time to trace back to a setting.
    ///
    /// The mechanism is kept rather than deleted because it is the right shape for the
    /// question: some project will want a room type left bare, and this is where that goes.
    /// An empty list excludes nothing, which is the current brief.
    /// </summary>
    public string[] ExcludedRoomKeywords { get; init; } = [];

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
    /// Whether ordinary DOORS get reveal boards too. ON, and that is a deliberate reversal.
    ///
    /// It was off on the argument that a door's frame and architrave already cover the
    /// reveal, so a board there is modelled through the lining. That argument holds for a
    /// door with a full lining and fails for everything else in a real model - a door set
    /// into a thick wall leaves reveal either side of the frame, and a frame modelled as a
    /// thin plane leaves the whole thickness on show. The brief now asks for door jambs
    /// explicitly, both as geometry and as quantity.
    ///
    /// Turning this on does NOT mean every door gets a board. What each door actually gets is
    /// decided against its own geometry by <see cref="SkipLinedReveals"/>: a fully lined door
    /// leaves nothing bare and gets nothing. This setting only decides whether doors are
    /// CONSIDERED; the family decides what happens next.
    /// </summary>
    public bool WrapIntoDoorReveals { get; init; } = true;

    /// <summary>
    /// Subtract the door family's own solids from the reveal before placing a board, and
    /// place boards only on what is left bare.
    ///
    /// THIS IS WHAT STOPS A LINED DOOR DOUBLING UP, AND WHY IT IS MEASURED RATHER THAN
    /// CONFIGURED. Whether a reveal is already covered is a property of the individual
    /// FAMILY, not of the project: one door type carries a full lining through the wall, the
    /// next carries a thin frame at one face, the next is a bare cased opening. A single
    /// switch is wrong for two of those three whichever way it is set, and a name heuristic
    /// is wrong whenever somebody renames a type.
    ///
    /// So the reveal run is intersected with the door's real solids and the board is placed
    /// on the remainder:
    ///
    ///   full lining     -> nothing bare, no board, nothing to double up
    ///   frame one side  -> a board on the uncovered depth only
    ///   cased opening   -> no solids in the reveal, the whole run gets a board
    ///
    /// It also delivers the harder half of the brief for free. Nothing this tool places can
    /// overlap the door model, because the door model is subtracted before anything is
    /// placed - the board is never created in that space rather than created and cleaned up.
    /// </summary>
    public bool SkipLinedReveals { get; init; } = true;

    /// <summary>
    /// Subtract the real solids of any nearby door, window or opening family from a WALL RUN,
    /// not just from a jamb board.
    ///
    /// THE HOLE IN THE OPENING LOGIC, AND WHY IT SHOWS UP AT PARTITION ENDS. Openings are
    /// found with <c>Wall.FindInserts</c>, which is by definition the list of things cut into
    /// THAT wall. It is the right authority for "where is the hole in this wall", and it is no
    /// authority at all for "is there a door frame standing in the way".
    ///
    /// A door hosted in wall B has architrave and lining that project onto the face of wall A
    /// wherever the two meet - which is every wall junction and every partition end next to a
    /// doorway. The board on wall A never sees that door, because the door is not one of wall
    /// A's inserts, so it runs straight into the lining. That is the collision in the corner
    /// screenshots, and no amount of tuning the jamb measurement reaches it: the fault is that
    /// the door was never considered for this run in the first place.
    ///
    /// So the door's own geometry is subtracted, from whichever wall hosts it, exactly as it
    /// is for a jamb board. Solids, not bounding boxes: a swung leaf's box covers a metre of
    /// wall the door merely passes over, while its solids occupy only where it actually is.
    /// </summary>
    public bool AvoidOpeningGeometry { get; init; } = true;

    /// <summary>
    /// How far from the run an opening family's geometry must be to be ignored, ~30 mm past
    /// the board's own depth. Anything further away cannot be in the board's way.
    /// </summary>
    public double OpeningGeometryReach { get; init; } = 0.164;

    /// <summary>
    /// Shift each board into the room by however far its profile hangs behind its own
    /// insertion line, so the back face lands on the wall instead of inside it.
    ///
    /// MEASURED IN THE MODEL, NOT ASSUMED. A placed board's centre was found sitting exactly
    /// on its wall's finish face: the profile is centred on its insertion line, so half the
    /// board - 20 mm of a 40 mm section - was buried in the wall and half showed.
    ///
    /// That single fact invalidated every corner calculation in this engine. The old
    /// measurement returned the largest offset from the insertion line, which for a centred
    /// profile is the HALF width, and the corner trim then used it as though it were the
    /// board's full depth. Every corner was therefore under-trimmed by the other half, which
    /// is precisely the 20 mm square measured overlapping at a corner in the model.
    ///
    /// The proper fix is for the family to carry its profile proud of the insertion line, and
    /// this is written so that fix costs nothing: the shift is the MEASURED overhang, so a
    /// re-authored family measures zero and nothing moves.
    /// </summary>
    public bool SeatProfileAgainstWall { get; init; } = true;

    /// <summary>
    /// Place jamb and reveal boards UNHOSTED, rather than face-hosted onto the wall.
    ///
    /// A wall sweep is a wall-hosted thing and belongs on a wall face. A jamb board is not:
    /// it runs perpendicular INTO the wall, across a face that belongs to the opening rather
    /// than to either side of the wall. Face-hosting it means asking for the nearest wall
    /// side face and getting one of the two the board is perpendicular to - the board is then
    /// hosted on a plane it crosses at right angles, which is at best arbitrary and at worst
    /// refused outright by the family.
    ///
    /// Keeping the two placements distinct is also what keeps this out of the door family's
    /// own lining logic: a jamb board sits in the opening as an independent component and has
    /// no host relationship with the wall the door is cut into.
    /// </summary>
    public bool HostRevealsOnWall { get; init; }

    /// <summary>
    /// Insert categories whose jambs get reveal boards.
    ///
    /// Wall openings are matched by CLASS rather than by category - see RevealOpenings - so
    /// they need no entry here. This list is what widens the net beyond doors: a floor-height
    /// window or a glazed door reaching the floor leaves exactly the same reveal, and the
    /// height gate below is what keeps ordinary windows out of it.
    /// </summary>
    public BuiltInCategory[] RevealCategories { get; init; } =
    [
        BuiltInCategory.OST_Doors,
        BuiltInCategory.OST_Windows,
        BuiltInCategory.OST_GenericModel,
    ];

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
    /// Foundation or Retaining. OFF by default, and that is a deliberate reversal.
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
    /// shallow angle sends <c>depth / sin(interior)</c> toward infinity; clamping keeps a
    /// near-straight join from trimming a board by a metre.
    ///
    /// RAISED FROM 3.0, because at 3.0 the clamp was itself producing overlaps. The true
    /// clearance is depth / sin(interior), which passes 3d at about 19 degrees and 5.8d at
    /// 10 degrees - so every corner sharper than 19 degrees was being under-trimmed, and the
    /// two boards shared material by exactly the shortfall. 6.0 covers everything down to
    /// about 9.6 degrees, which is sharper than any wall junction in a dwelling.
    ///
    /// It doubles as the corner allowance for containment - see ClipToRoom. A board is
    /// forgiven up to this much apparent excursion at an end that is a genuine corner,
    /// because at a sharp corner the board legitimately occupies space the ROOM does not:
    /// the boundary is a line and a board has thickness.
    /// </summary>
    public double MaxMitreFactor { get; init; } = 6.0;

    /// <summary>
    /// Skirt room boundaries that are bounded by something other than a wall.
    ///
    /// A room is not bounded only by walls. A column standing in a room, a structural pier,
    /// a shaft wall modelled as a generic host - all of them present a face to the room at
    /// floor level and all of them are skirted on site. Every one of them used to be
    /// discarded by a hard <c>as Wall</c> cast, which is the single largest source of the
    /// "sweep skipped part of the room" symptom: the boundary segment resolved to a real
    /// element, failed the cast, and vanished into a counter.
    ///
    /// What must still be refused is a boundary with no physical face behind it. Those are
    /// listed in <see cref="NonSkirtableBoundaryCategories"/>.
    /// </summary>
    public bool SkirtNonWallBoundaries { get; init; } = true;

    /// <summary>
    /// Boundary elements that get no board however <see cref="SkirtNonWallBoundaries"/> is
    /// set, because there is no face to fix one to.
    ///
    /// A room separation line is the case that matters: it is a real element with a real
    /// boundary curve and nothing physical behind it. Skirting it would put a board across
    /// the open side of a room.
    /// </summary>
    public BuiltInCategory[] NonSkirtableBoundaryCategories { get; init; } =
    [
        BuiltInCategory.OST_RoomSeparationLines,
        BuiltInCategory.OST_Lines,
        BuiltInCategory.OST_Rooms,
        BuiltInCategory.OST_MEPSpaces,
        BuiltInCategory.OST_Areas,

        // Horizontal construction. It can bound a room, but its boundary is the edge of a
        // slab or a ceiling, and a skirting board does not run along either.
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Roofs,
    ];

    /// <summary>
    /// Cut the ends of each board to the corner angle, so corners are MITRED rather than
    /// butted. Requires the family to expose writable end-angle parameters - see
    /// <see cref="MitreStartParameterNames"/>.
    ///
    /// WHY THIS IS THE ONLY THING THAT CLOSES A CORNER PROPERLY, VERIFIED RATHER THAN
    /// ASSUMED. Two square-ended boards butt cleanly at 90 degrees and at no other angle, and
    /// even at 90 the joint shows a step one board deep where the second board's square end
    /// meets the first board's face. That step is what reads as a gap. It cannot be closed by
    /// moving the boards: it is a property of their END FACES.
    ///
    /// Revit's own mitring API - LocationCurve.JoinType with JoinType.Miter - applies to
    /// walls and concrete beams only; a FamilyInstance has no equivalent. JoinGeometry was
    /// tried against this family and refused every time. GeometryCreationUtilities documents
    /// that swept solids "may not meet smoothly" at path corners. So the cut has to be in the
    /// family.
    ///
    /// THE CONVENTION, which the family must match: the angle is measured from the plane
    /// PERPENDICULAR to the board's axis, and equals <c>90 - interior/2</c> degrees. At a 90
    /// degree corner that is 45 degrees - an ordinary mitre. At 135 degrees interior it is
    /// 22.5. At an external corner (interior over 180) it goes negative, which is the same
    /// formula producing the opposite hand, so internal and external need no special case.
    /// Both boards at a corner receive the same value; the family mirrors it by which end it
    /// is applied to.
    /// </summary>
    public bool MitreCorners { get; init; } = true;

    /// <summary>
    /// Candidate names for the end-angle parameter at the board's START, in order of
    /// preference. English and Danish, because the family may be authored in either.
    /// </summary>
    public string[] MitreStartParameterNames { get; init; } =
        ["Angle Start", "Start Angle", "Mitre Start", "Miter Start", "Vinkel Start", "Gering Start"];

    /// <summary>Candidate names for the end-angle parameter at the board's END.</summary>
    public string[] MitreEndParameterNames { get; init; } =
        ["Angle End", "End Angle", "Mitre End", "Miter End", "Vinkel Slut", "Gering Slut"];

    /// <summary>
    /// Fill every corner and let Revit resolve the shared volume with JoinGeometry.
    ///
    /// THE ONLY WAY BOTH CORNER RULES CAN HOLD AT ONCE. Two square-ended boards butt cleanly
    /// at 90 degrees and at no other angle - that was measured, not argued: at any other
    /// angle the interface between them is a slanted line and a square cut cannot follow it,
    /// so the pair must either show a gap or share material. There is no trim value that
    /// avoids both.
    ///
    /// Sharing material is the half that Revit can fix. JoinGeometry resolves an intersection
    /// so exactly ONE element owns the shared volume: the boards read as one continuous
    /// mitred run, the edge between them disappears, and the volume is counted once rather
    /// than twice. So the corner is filled to the apex - no gap - and the overlap that
    /// creates is handed to the join.
    ///
    /// Square corners keep the trim, because there the butt is exact and needs no join.
    /// </summary>
    public bool JoinAtCorners { get; init; } = true;

    /// <summary>
    /// How far from a right angle a corner may be and still be treated as square, ~1 degree.
    ///
    /// Inside this band the trim is exact and produces a clean butt with no gap and no shared
    /// material. Outside it, no trim can do both and the corner is filled and joined instead.
    /// </summary>
    public double SquareCornerTolerance { get; init; } = 0.0175;

    /// <summary>
    /// Refuse a candidate board wherever another board already occupies the same line.
    ///
    /// The office standard is that no two sweeps may overlap, and the corner trim alone
    /// cannot deliver that: it settles corners, not the case where the SAME face is reached
    /// twice. That happens on a wall shared by two boundary loops, on a wall a room touches
    /// in two places, and against boards left behind by a previous run that could not be
    /// deleted because another user owns them.
    ///
    /// So the check is made against everything already standing - placed this run or found
    /// in the model - and the candidate is cut back to the part nothing covers rather than
    /// refused outright. Cutting back is what lets a partially-covered face still contribute
    /// its missing stretch.
    /// </summary>
    public bool PreventOverlaps { get; init; } = true;

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
