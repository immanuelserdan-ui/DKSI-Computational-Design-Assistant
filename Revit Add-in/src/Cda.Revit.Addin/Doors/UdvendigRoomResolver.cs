using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Doors;

public sealed class UdvendigSettings
{
    /// <summary>
    /// Any room whose NAME begins with this counts as outside. 'Udvendig', 'Udvendig 1'
    /// and 'Udvendig 3' all match. Case-insensitive.
    /// </summary>
    public string ExteriorPrefix { get; init; } = "Udvendig";

    public string NumFrom { get; init; } = "02-SCRP Num fr";
    public string NameFrom { get; init; } = "04-SCRP Nam fr";
    public string NumTo { get; init; } = "03-SCRP Num to";
    public string NameTo { get; init; } = "05-SCRP Nam to";

    /// <summary>
    /// The SUBSTITUTED room - number and name - for the 'Ext Door * @V05' set to read.
    ///
    /// WHY A SECOND PAIR EXISTS AT ALL. One door has to publish two different answers. The
    /// 'Door * FROM/TO' set must show 'Udvendig' where a door faces out; the 'Ext Door * @V05'
    /// set must show the room on the other side instead. The four SCRP parameters can hold one
    /// of those, not both, and which one they hold is <see cref="SubstituteExteriorSide"/>.
    /// This pair holds the other, so both schedule sets can be right at once and both follow a
    /// door flip, because this tool rewrites all six values on every door change.
    ///
    /// WHAT GOES IN IT: the FROM side after substitution. For an exterior door that is the room
    /// opposite the 'Udvendig' one - door 747 gets 'Entre', door 751 gets 'Bad'. For an interior
    /// door it is simply the FROM room, so a schedule reading this pair shows the same thing it
    /// always did for the doors that were never the problem.
    ///
    /// WHY NOT A FORMULA IN THE SCHEDULE, which needs nothing added: measured 2026-09-02, the
    /// office's own 'Override Rum Name' calculated field is EMPTY - written, never filled in.
    /// Revit's formula parser reads '04-SCRP Nam fr' as "04 minus SCRP Nam fr", and gives no way
    /// to quote a parameter name, so the field those columns need cannot be referenced. A value
    /// computed here and written to a real parameter has no such limit.
    ///
    /// NOT CREATED BY THIS TOOL - same rule as <see cref="UdvendigFlag"/> and for the same
    /// reason ScrpBinder refuses to mint SCRP definitions: a parameter created here would carry
    /// a NEW GUID, look correct in this model, and line up with nothing in any other project
    /// using the office standard. If these are not in the document, every door is resolved
    /// exactly as before and the pair is simply not written - inert, never wrong, and reported.
    /// </summary>
    public string NumSubstituted { get; init; } = "06-SCRP Num sub";

    /// <inheritdoc cref="NumSubstituted"/>
    public string NameSubstituted { get; init; } = "07-SCRP Nam sub";

    /// <summary>
    /// Yes/No parameter recording whether a door faces outside on at least one side.
    ///
    /// WHY THIS HAS TO BE WRITTEN DOWN AT ALL, rather than worked out later by a schedule
    /// filter: this resolver's entire job is to OVERWRITE the 'Udvendig' side with the room
    /// opposite it. Once a pass has run, nothing in the four SCRP values distinguishes an
    /// exterior door from an interior one - the marker the filter would key on is precisely
    /// what was just removed. The classification is free here (the substitution branches
    /// already know it) and unrecoverable anywhere downstream, so it is captured here.
    ///
    /// WHAT IT IS FOR: separating the 'Ext Door ...' schedules from the interior
    /// 'Door ... FROM/TO' ones. Both sets read the same SCRP columns, so without a
    /// discriminator every door appears in both. Filter the exterior schedules to Yes and the
    /// interior ones to No.
    ///
    /// WHY THIS ONE AND NOT A NEW '06-SCRP ...'. Measured in FM_Template 2027V1.00_EN: the
    /// doors already carry FIVE exterior-door flags - 'CRP Exterior Door', 'CRP Exterior Door
    /// Override', '3XD CRP Ext Door', '3XD Ext Door Override' and (on the Exterior Door family
    /// only) 'Exterior Door Override'. Every one of them reads 0, including on the doors that
    /// genuinely are exterior, so whatever was meant to maintain them never has. Minting a
    /// sixth flag beside five dead ones is how a model ends up with no answer to a question it
    /// can express five ways.
    ///
    /// 'CRP Exterior Door' is the computed half of the 'X' / 'X Override' pair and is already
    /// bound to every door, so nothing has to be added to the shared parameter file first.
    ///
    /// NOT CREATED BY THIS TOOL. If a model does not already carry it, every door is resolved
    /// exactly as before and the classification is simply not recorded - inert, never wrong.
    /// </summary>
    public string UdvendigFlag { get; init; } = "CRP Exterior Door";

    /// <summary>
    /// The manual counterpart to <see cref="UdvendigFlag"/>. When it is ticked on a door, this
    /// tool does not write that door's flag at all.
    ///
    /// THE SEMANTIC IS "HANDS OFF", NOT "FORCE EXTERIOR", and the choice is deliberate. The
    /// office's intent for this parameter is not recorded anywhere and both readings are
    /// plausible - but they fail differently. Read as "hands off" and the office meant "force
    /// exterior", a door simply does not get classified, which is visible in the report and
    /// costs nothing. Read as "force exterior" and the office meant "hands off", this tool
    /// overwrites a value somebody set deliberately, which is silent and unrecoverable.
    ///
    /// It also matches how the rest of this add-in treats human intent - see the
    /// '#noroom-auto' and '#nolining-auto' Comments markers, which are the same idea.
    ///
    /// Currently 0 on every door in the template, so this is inert today either way.
    /// </summary>
    public string UdvendigFlagOverride { get; init; } = "CRP Exterior Door Override";

    /// <summary>Doors whose Comments contain this marker are left alone.</summary>
    public string SkipMarker { get; init; } = "#noroom-auto";

    /// <summary>
    /// Work out each door's two sides from its FACING DIRECTION instead of asking Revit for
    /// From Room / To Room.
    ///
    /// WHY THIS EXISTS. Measured on FM_Template 2027V1.00_EN: Revit does NOT reassign From/To
    /// Room when a door's facing is flipped. Doors 747, 748, 749 and 750 all report
    /// FacingFlipped = true, and their From/To are byte-identical to what they were before the
    /// flips - so a schedule built on those fields cannot follow the flip control, however
    /// promptly it is recalculated. The add-in was reading them correctly and writing them
    /// within 10 ms; there was simply nothing new to read.
    ///
    /// WHAT IT DOES INSTEAD. Probes a point just past the wall on each side of the door, along
    /// and against <c>FacingOrientation</c> - which already accounts for the flip - and asks
    /// the document which room is there. TO is the room the door faces; FROM is the one behind
    /// it. Flip the door and the two swap, because the vector they are derived from swapped.
    ///
    /// OFF BY DEFAULT, AND THAT IS NOT TIMIDITY. Turning it on makes the SCRP values disagree
    /// with Revit's own From Room / To Room fields on any flipped door. That is the POINT, but
    /// it means anything else reading the built-in fields - another schedule, an IFC export, a
    /// downstream FM system - will disagree with these schedules, and the disagreement is
    /// silent. Switch it on deliberately, per office, once that is understood.
    ///
    /// FALLS BACK RATHER THAN GUESSING. If the door has no location point, no facing, or the
    /// probes find no room on either side, this defers to Revit's own answer for that door and
    /// says so in the summary. A blank side is never published in place of a real one.
    /// </summary>
    public bool DeriveSidesFromFacing { get; init; }

    /// <summary>
    /// REPLACE the built-in From/To Room columns in every schedule with the SCRP parameters.
    ///
    /// OFF BY DEFAULT NOW, AND IT USED TO BE ON. That default was wrong and this is the note
    /// explaining why, because the reasoning that produced it was not stupid.
    ///
    /// THE ORIGINAL ARGUMENT. Revit's built-in From Room / To Room are derived from geometry
    /// and read-only, so the Udvendig substitution cannot be expressed in them - write the SCRP
    /// parameters and a schedule still showing the built-in column displays 'Udvendig' forever.
    /// Swapping the column was the only way to make the corrected value visible.
    ///
    /// WHY IT WAS STILL WRONG. The columns it removes are an office standard that other people
    /// and other tools depend on, and this is not a decision an add-in gets to make silently
    /// across every schedule in the model. Measured here: 39 schedules examined, and the
    /// built-in From/To Room columns removed from all of them - door AND window - including the
    /// 'Ext Door' set, where the office had built calculated columns on top of the very fields
    /// this deleted.
    ///
    /// AND IT WAS UNFIXABLE BY HAND. The automation re-runs on every door, window and room
    /// edit, so a column restored manually was removed again on the next change. That is what
    /// turns a debatable default into a real defect: the user could not overrule it.
    ///
    /// The element parameters were never at risk - see the writes in Run(), which touch only
    /// the SCRP parameters and are guarded by IsReadOnly. This only ever edited schedule
    /// DEFINITIONS. But a schedule definition is a deliverable too.
    ///
    /// Leave it off. Add the SCRP columns to the schedules that want them, by hand, once - they
    /// are ordinary shared parameters and appear in the field list like any other.
    /// </summary>
    public bool RepointSchedules { get; init; }

    /// <summary>
    /// Put back the built-in From/To Room columns that <see cref="RepointSchedules"/> removed on
    /// earlier runs, leaving the SCRP columns in place beside them.
    ///
    /// A REPAIR, RUN ONCE - not a mode to leave on. It only ever ADDS a field, and only where an
    /// SCRP column is present and its built-in counterpart is missing, so running it twice does
    /// nothing the second time. It cannot remove anything.
    ///
    /// THE ONE SANCTIONED EXCEPTION TO "DO NOT TOUCH TEMPLATE SCHEDULES", AND NOT A PRECEDENT.
    /// The standing rule is that schedules shipped in the office Template are off limits - write
    /// parameters and let a person decide the columns. Schedules a tool GENERATES (the Painted
    /// Surface Area takeoff's own, the @V03 surface set) are that tool's to manage; these are
    /// not. This method is allowed near them for exactly one reason: it repairs damage THIS
    /// add-in did, and the alternative is re-adding columns to 39 schedules by hand.
    ///
    /// So it stays OPT-IN and defaulted off, deliberately, and it must not become the thin end
    /// of a wedge. Do not extend it to add a column somebody merely wants, do not call it from
    /// the automation, and do not add a sibling that removes or re-points one. If a value needs
    /// to be visible in a Template schedule, say so in the report and let the office add it.
    ///
    /// WHAT IT CANNOT PUT BACK: the original column heading, width, alignment and position. Those
    /// went with the removed field, and the replacement carried them off. Restored columns arrive
    /// with Revit's own default heading and land next to their SCRP counterpart, so a schedule
    /// will need tidying afterwards - but the FIELD is back, filters and formulas can reference it
    /// again, and no data was ever lost.
    /// </summary>
    public bool RestoreBuiltInRoomColumns { get; init; }

    /// <summary>
    /// Swap the Rum nr/Rum columns in the interior 'Door * FROM/TO' schedule set back from the
    /// SCRP parameters to Revit's own From Room/To Room fields - REMOVING the SCRP binding
    /// there, not merely adding beside it.
    ///
    /// WHY THIS EXISTS. Measured 2026-09-01: those 14 schedules were hand-repointed at SCRP
    /// THAT SAME EVENING (field 3 of 'Door Casing FROM @V03' read FromRoom/ROOM_NUMBER at
    /// 20:55, then Instance/02-SCRP Num fr by 21:10 - RepointScheduleColumns was false and
    /// every run in between logged "Schedules: LEFT ALONE", so the add-in did not do it). But
    /// Plan() overwrites SCRP on BOTH sides of every exterior door, so an 'Udvendig' side is
    /// erased there exactly as it is in the 'Ext Door * @V05' set - which defeats the rule that
    /// Udvendig is ALLOWED to stand in the FROM/TO set. One shared pair of SCRP values cannot
    /// satisfy both rules at once, so the FROM/TO set has to stop reading them and go back to
    /// the built-in fields the resolver never touches.
    ///
    /// SCOPE IS NARROW ON PURPOSE: only schedules named 'Door ... FROM ...' or 'Door ... TO ...'
    /// are touched - matched by name starting with "Door " (not "Ext Door ...") and containing
    /// the whole word FROM or TO. Running this must never affect the 'Ext Door * @V05' set,
    /// which still needs the substituted room to display.
    ///
    /// UNLIKE RestoreBuiltInRoomColumns, this REMOVES the SCRP field it replaces - leaving both
    /// would duplicate the column in a schedule meant to show exactly one Rum nr / Rum pair.
    ///
    /// A REPAIR, RUN ONCE - not a mode to leave on, same as RestoreBuiltInRoomColumns.
    /// </summary>
    public bool RestoreFromToScheduleColumns { get; init; }

    /// <summary>
    /// Un-hide the SCRP room columns in the 'Ext Door * @V05' set, and hide the empty
    /// calculated columns standing in front of them.
    ///
    /// WHAT IS ACTUALLY WRONG THERE. Measured in FM_Template 2027V1.00_EN, 2026-09-02: all
    /// seven 'Ext Door * @V05' schedules carry four correctly-bound SCRP fields - the report's
    /// 'Doors carry it?' column reads 'yes' on every one - and every one of them is HIDDEN.
    /// In their place sit calculated (Formula) columns headed 'Rum nr' and 'Rum' that evaluate
    /// to nothing. So the schedules show two empty columns while the populated columns beside
    /// them are switched off. The 'Window * FROM @V04/V05' set is the same wiring with the
    /// visibility the right way round, and is what these should look like.
    ///
    /// WHY THIS IS ALLOWED NEAR TEMPLATE SCHEDULES WHEN ALMOST NOTHING IS. It only ever
    /// changes VISIBILITY, a heading, and column order. It does not add a field and it does
    /// NOT DELETE ONE - the calculated columns are hidden, never removed, so whatever their
    /// formula was meant to do is still there to inspect and switch back on. Nothing it does
    /// costs data, and every part of it can be undone from the Fields dialog by hand.
    ///
    /// That is a deliberately weaker action than RestoreFromToScheduleColumns, which removes
    /// fields. Read the note on RestoreBuiltInRoomColumns about not making that exception into
    /// a precedent: this stays inside it by never destroying anything.
    ///
    /// A REPAIR, RUN ONCE. Idempotent - a schedule whose SCRP columns are already shown offers
    /// nothing to change, so a second run reports 'already correct' and stops.
    /// </summary>
    public bool RevealExtDoorRoomColumns { get; init; }

    /// <summary>
    /// Overwrite the 'Udvendig' side of an exterior door with the room opposite it.
    ///
    /// ON BY DEFAULT, because it is what this tool was built to do and turning it off silently
    /// would change what every existing consumer of the SCRP parameters reads.
    ///
    /// WHY IT CAN NOW BE TURNED OFF. Substitution exists for ONE consumer: the 'Ext Door * @V05'
    /// set, which bills an exterior door's casing against the interior room rather than against
    /// the outdoors. If that set is filtered to exclude exterior doors, it never sees an
    /// 'Udvendig' side, and the substitution has no consumer left.
    ///
    /// What it costs, and it is not nothing: with substitution ON, one door cannot publish both
    /// answers. The 'Door * FROM/TO' set needs the RAW sides - 'Udvendig' intact, following the
    /// flip control - and the SCRP parameters are the only values this tool maintains
    /// automatically. So a model that wants FROM/TO to auto-update on a flip AND to show
    /// 'Udvendig' has to stop substituting, or add a second parameter pair to the office shared
    /// parameter file. This setting is the first of those two.
    ///
    /// READ THIS BEFORE TURNING IT OFF. The SCRP parameters change meaning for exterior doors:
    /// '04-SCRP Nam fr' on door 747 goes from 'Entre' to 'Udvendig'. Anything else reading them
    /// - an IFC export, the FM system, the digital twin - sees the outdoors where it used to see
    /// a room. That is the correct raw measurement and it is a DIFFERENT NUMBER than yesterday,
    /// so decide it deliberately rather than discovering it downstream.
    /// </summary>
    public bool SubstituteExteriorSide { get; init; } = true;

    /// <summary>
    /// Point the Rum nr/Rum columns of the interior 'Door * FROM/TO' set AT the SCRP parameters
    /// - the exact inverse of <see cref="RestoreFromToScheduleColumns"/>, and the reason the
    /// schedules can follow a door flip at all.
    ///
    /// WHY THIS IS THE ONLY WAY TO GET AUTO-UPDATE. Those columns currently read Revit's
    /// built-in From/To Room. Two facts make that a dead end: the built-in fields do not follow
    /// the flip control (which is the whole reason <see cref="DeriveSidesFromFacing"/> exists),
    /// and they are derived and read-only, so nothing in this add-in can write them. A column
    /// bound to them can only be corrected by hand, and a hand correction is an override that
    /// never re-derives - it goes stale silently on the next flip.
    ///
    /// The SCRP parameters have neither problem. They are written on every door change through
    /// the DocumentChanged pipeline, so a flip updates them before the schedule redraws.
    ///
    /// TURN <see cref="SubstituteExteriorSide"/> OFF WITH THIS, or exterior doors in this set
    /// will show the interior room on BOTH sides instead of 'Udvendig' on the outward one. The
    /// two settings are a pair; using this one alone is what RestoreFromToScheduleColumns was
    /// built to undo.
    ///
    /// MUTUALLY EXCLUSIVE with RestoreFromToScheduleColumns, which swaps these same columns the
    /// other way. Enabling both is refused rather than resolved, because whichever ran last
    /// would win and the report would claim both succeeded.
    /// </summary>
    public bool RepointFromToScheduleColumns { get; init; }

    /// <summary>
    /// Filter the 'Ext Door * @V05' set to exclude doors classified exterior.
    ///
    /// THIS IS A CONTENT CHANGE, NOT A COSMETIC ONE - it changes which rows a schedule lists,
    /// and therefore what the quantities at the bottom of it add up to. It is the only setting
    /// in this file that does that, and it is off by default for exactly that reason.
    ///
    /// It pairs with <see cref="SubstituteExteriorSide"/> being off: substitution exists to
    /// serve this set, so this set excluding exterior doors is what makes the substitution
    /// unnecessary. Enabling this while substitution is still on is legal but pointless - the
    /// doors whose values were being substituted are the ones being filtered out.
    ///
    /// Uses <see cref="UdvendigFlag"/>, which this tool writes on every run, so the filter
    /// tracks the classification automatically rather than freezing today's answer.
    /// </summary>
    public bool FilterExtDoorSchedules { get; init; }

    /// <summary>
    /// Remove the filter <see cref="FilterExtDoorSchedules"/> added, putting the 'Ext Door *
    /// @V05' set back to listing every door it listed before.
    ///
    /// WHY THIS EXISTS. Measured in FM_Template 2027V1.00_EN, 2026-09-02: filtering that set to
    /// "CRP Exterior Door = No" emptied all seven schedules. Doors 747 and 751 are the only two
    /// the classifier calls exterior, and they are the only two those schedules ever listed, so
    /// excluding them left nothing. A set named 'Ext Door' that excludes exterior doors has no
    /// rows by construction - the rule read literally and the rule as meant were not the same
    /// thing, and this is the way back.
    ///
    /// Removes ONLY filters bound to <see cref="UdvendigFlag"/>, so a filter the office put on
    /// the same schedule for any other reason is untouched. The hidden flag FIELD is left in
    /// place: it costs nothing, it is what a future filter would need anyway, and removing
    /// fields is the part of this file that has to stay rare.
    /// </summary>
    public bool RemoveExtDoorFilter { get; init; }

    /// <summary>
    /// Point the Rum nr/Rum columns of the 'Ext Door * @V05' set at the SUBSTITUTED pair, so
    /// they never show 'Udvendig'.
    ///
    /// THE LAST STEP OF THE OFFICE RULE. The resolver computes the substituted room and writes
    /// it to <see cref="NumSubstituted"/> / <see cref="NameSubstituted"/>; this points the seven
    /// schedules at it. Door 751 stops reading '99 / Udvendig' and starts reading '46 / Bad',
    /// and it keeps following a door flip because both parameters are rewritten on every change.
    ///
    /// HIDES EVERY OTHER COLUMN HEADED 'Rum nr' OR 'Rum', which is the part that matters and
    /// the part that is easy to get wrong. The old pair is still there under the same heading,
    /// and two columns both headed 'Rum' showing different rooms is worse than the original
    /// fault. The two target columns are spared by FieldId, never by heading, so the reveal can
    /// never hide what it just revealed. Nothing is deleted - see RevealExtDoorRoomColumns.
    ///
    /// REQUIRES THE SUBSTITUTED PAIR TO EXIST. If the doors do not carry it the schedules are
    /// left exactly as they are and the report says so; there is no half-applied state where
    /// the old column is hidden and no new one has appeared.
    ///
    /// A REPAIR, RUN ONCE. Idempotent: a schedule already showing the pair reports 'already
    /// correct' and is not touched.
    /// </summary>
    public bool PointExtDoorToSubstituted { get; init; }

    /// <summary>
    /// Optional name filter. EMPTY BY DEFAULT, meaning every schedule in the model is examined.
    ///
    /// It started as "Door Casing", inherited from the Dynamo graph, which knew about the
    /// casing schedules and nothing else. 'Door Frames TO @V03' and 'Door Lining TO @V03' were
    /// therefore skipped in silence - identical columns, same read-only From/To Room fields,
    /// still reading 'Udvendig' after a run that reported success. Widening it to "Door" fixed
    /// those two and would have failed again on the next schedule named something else.
    ///
    /// THE NAME WAS NEVER THE SAFETY MECHANISM. What protects a schedule is the field test:
    /// only columns that ARE built-in FromRoom/ToRoom room-number or room-name fields are ever
    /// swapped, and only for a parameter that is schedulable there. Every other schedule in the
    /// model is walked and left completely alone. Filtering by name only ever decided which
    /// schedules got MISSED.
    ///
    /// Set it to narrow the sweep deliberately; leave it empty to catch everything.
    /// </summary>
    public IReadOnlyList<string> ScheduleNameContains { get; init; } = [];
}

public sealed class DoorPlan
{
    public required Element Door { get; init; }
    public required long Id { get; init; }
    public required string Mark { get; init; }
    public required string TypeName { get; init; }

    /// <summary>
    /// The sides THIS TOOL resolved, before substitution: from number, from name, to number,
    /// to name.
    ///
    /// THIS IS NOT NECESSARILY WHAT REVIT REPORTS, and the column heading used to say it was.
    /// When <see cref="UdvendigSettings.DeriveSidesFromFacing"/> is on these come from probing
    /// either side of the facing vector, which deliberately disagrees with Revit's own From/To
    /// Room on any flipped door - that is the entire point of the setting. See
    /// <see cref="Native"/> for why printing only this one made the report actively misleading.
    /// </summary>
    public required string[] Raw { get; init; }

    /// <summary>
    /// What Revit's OWN From/To Room fields say, plus each side's Department: from number,
    /// from name, from department, to number, to name, to department.
    ///
    /// WHY THIS IS IN THE REPORT AT ALL. The door schedules read Revit's native fields - not
    /// the derived answer in <see cref="Raw"/>, and not always the SCRP parameters either. So
    /// when a schedule cell disagrees with this tool, the native value is the only thing that
    /// explains it, and it was the one number the report did not print. Diagnosing a blank or
    /// wrong cell from the old report meant reading a derived value under a heading that
    /// claimed it was Revit's, and concluding the wrong thing - which is exactly what happened.
    ///
    /// DEPARTMENT IS HERE BECAUSE THE 'Lejlighed' COLUMN IS IT. Every door schedule in this
    /// template binds 'Lejlighed' to the built-in From Room: Department field, so a blank
    /// Lejlighed means the native From Room has no Department - most often because that side
    /// is the 'Udvendig' placeholder, which carries none. Printing it turns "why is this cell
    /// empty" into a lookup instead of an investigation.
    ///
    /// Read-only evidence. Nothing here changes a single decision.
    /// </summary>
    public required string[] Native { get; init; }

    /// <summary>What will be written to the four SCRP parameters.</summary>
    public required string[] Desired { get; init; }

    public required string[] Current { get; init; }

    /// <summary>
    /// What will be written to the substituted pair - number then name. Always computed, even
    /// when the door does not carry the parameters. See UdvendigSettings.NumSubstituted.
    /// </summary>
    public required string[] Substituted { get; init; }

    /// <summary>
    /// What the substituted pair reads now, or NULL when the door does not carry it. Null is
    /// not the same as two empty strings: absent means there is nothing to write and never
    /// will be, empty means the parameter is there and waiting.
    /// </summary>
    public required string[]? CurrentSubstituted { get; init; }

    public required string Note { get; init; }

    /// <summary>
    /// True when at least one side of this door faces an exterior placeholder room - i.e. this
    /// is one of the doors the 'Ext Door ...' schedules are for. Computed from the same test
    /// that drives the substitution, so the two can never disagree.
    /// </summary>
    public required bool IsExterior { get; init; }

    /// <summary>
    /// What <see cref="UdvendigSettings.UdvendigFlag"/> currently reads on this door: 0, 1, or
    /// -1 when the parameter is absent, unreadable or not a Yes/No. -1 means "cannot record",
    /// which is deliberately NOT the same as "records No" - a door that cannot carry the flag
    /// must not be counted as a write that is owed.
    /// </summary>
    public required int FlagCurrent { get; init; }

    /// <summary>
    /// True when a human has ticked <see cref="UdvendigSettings.UdvendigFlagOverride"/> on this
    /// door, which takes the flag out of this tool's hands entirely.
    /// </summary>
    public required bool FlagOverridden { get; init; }

    /// <summary>
    /// The door's two flip states, recorded purely as evidence.
    ///
    /// WHY THEY ARE IN THE REPORT. "I flipped the door and the schedule did not change" has two
    /// completely different causes and they are indistinguishable on screen:
    ///
    ///   FLIP HAND (the swing-side control) mirrors which way the leaf opens. The door still
    ///   FACES the same way, so Revit's From/To Room are unchanged - correctly. The drawing
    ///   changes, the schedule does not, and nothing is broken.
    ///
    ///   FLIP FACING (the through-the-wall control) reverses the facing vector, which is what
    ///   Revit derives From/To Room from, so those two swap and the schedule follows.
    ///
    /// Without these columns the two look identical from outside, and the add-in gets blamed
    /// for the first one. This tool reads From/To fresh on every pass and writes exactly what
    /// Revit reports - it cannot see a change Revit is not making.
    /// </summary>
    public required bool FacingFlipped { get; init; }

    public required bool HandFlipped { get; init; }

    /// <summary>
    /// The flag needs writing: it can be written at all, and what it holds disagrees with what
    /// this pass computed. Folded into <see cref="Changed"/> so the first run AFTER the office
    /// binds the parameter still writes it - by then the four room values are usually already
    /// correct, so a Changed that ignored the flag would leave every door unclassified with
    /// nothing reporting why.
    /// </summary>
    /// <summary>
    /// The sides this tool resolved are NOT the ones Revit's own From/To Room report.
    ///
    /// THE SINGLE MOST USEFUL COLUMN IN THE REPORT when a schedule cell looks wrong, because
    /// the 'Door * FROM/TO' schedules read the native fields. When this is YES, the schedule
    /// and this tool are describing different rooms - and the schedule is not going to change,
    /// because those fields are derived and read-only.
    /// </summary>
    public bool SidesDifferFromNative =>
        Raw[0] != Native[0] || Raw[1] != Native[1] ||
        Raw[2] != Native[3] || Raw[3] != Native[4];

    public bool FlagChanged =>
        UdvendigClassification.NeedsFlagWrite(IsExterior, FlagCurrent, FlagOverridden);

    /// <summary>
    /// True when the substituted pair is present AND holds something other than the answer.
    /// A door without the parameters is never "changed" by them - see CurrentSubstituted.
    /// </summary>
    public bool SubstitutedChanged =>
        CurrentSubstituted is not null && !CurrentSubstituted.SequenceEqual(Substituted);

    public bool Changed => !Current.SequenceEqual(Desired) || FlagChanged || SubstitutedChanged;
}

public sealed class UdvendigResult
{
    public required IReadOnlyList<string> Summary { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Port of Resolve-Udvendig-Rooms_v1.0.dyn.
///
/// A door leading outside reports an exterior room - 'Udvendig 1', 'Udvendig 3' - on one
/// side. Wherever a side reads 'Udvendig...', it is replaced by the room on the OTHER
/// side of the same door.
///
/// This cannot be done in the schedule: the 'Rum nr' / 'Rum' columns are Revit's built-in
/// From Room / To Room fields with renamed headings, and those are derived from geometry
/// and read-only. So the corrected values go into the door's own SCRP shared parameters.
///
/// SCHEDULES ARE NOT TOUCHED. This class once ALSO swapped every built-in From/To Room column
/// in the model over to those parameters, because otherwise a schedule still showing the
/// built-in column displays 'Udvendig' for ever. That is off now and off by default - see
/// <see cref="UdvendigSettings.RepointSchedules"/> for why, and
/// <see cref="UdvendigSettings.RestoreBuiltInRoomColumns"/> for the repair. Add the SCRP
/// parameters as schedule fields by hand wherever they are wanted; they are ordinary shared
/// parameters and appear in the field list like any other.
///
/// NOTHING HERE EVER WRITES A BUILT-IN PARAMETER. The only element writes are the four SCRP
/// parameters and the exterior classification flag, and both are guarded on IsReadOnly.
/// Revit's own From Room / To Room are derived and read-only, and are left exactly as Revit
/// computes them.
///
/// The four parameters are always written in full, not only when a substitution happens,
/// because they become the schedule's source of truth. Re-running is therefore idempotent.
/// </summary>
public sealed class UdvendigRoomResolver
{
    /// <summary>(schedule field type, the room property it shows) -> replacement parameter.</summary>
    private static readonly (ScheduleFieldType FieldType, BuiltInParameter RoomBip, Func<UdvendigSettings, string> Target)[] FieldMap =
    [
        (ScheduleFieldType.FromRoom, BuiltInParameter.ROOM_NUMBER, s => s.NumFrom),
        (ScheduleFieldType.FromRoom, BuiltInParameter.ROOM_NAME, s => s.NameFrom),
        (ScheduleFieldType.ToRoom, BuiltInParameter.ROOM_NUMBER, s => s.NumTo),
        (ScheduleFieldType.ToRoom, BuiltInParameter.ROOM_NAME, s => s.NameTo),
    ];

    private readonly Document _doc;
    private readonly UdvendigSettings _settings;
    private readonly List<string> _warnings = [];
    private readonly List<string> _skipped = [];

    /// <summary>
    /// Door/window schedules that read a room field but have no Phase set - filled by
    /// <see cref="AuditSchedulePhases"/> and reported by <see cref="BuildSummary"/>.
    /// </summary>
    private readonly List<string> _phaseless = [];

    /// <summary>
    /// Schedule columns bound to a parameter that shares its NAME with one the doors carry but
    /// is not the same parameter - filled by <see cref="DescribeScheduleFields"/>.
    /// </summary>
    private readonly List<string> _mismatched = [];

    public UdvendigRoomResolver(Document doc, UdvendigSettings settings)
    {
        _doc = doc;
        _settings = settings;
    }

    public UdvendigResult Run(bool apply, IReadOnlyList<Element>? selection = null)
    {
        var doors = selection is { Count: > 0 }
            ? selection.OfType<FamilyInstance>().Cast<Element>().ToList()
            : new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .ToList();

        // Stage 1 - point the schedule columns at the SCRP parameters. Without this the
        // parameter writes below are invisible.
        var scheduleRows = new List<IReadOnlyList<string>>();
        var restoreRows = new List<IReadOnlyList<string>>();
        var restoreFromToRows = new List<IReadOnlyList<string>>();
        var revealRows = new List<IReadOnlyList<string>>();
        var repointFromToRows = new List<IReadOnlyList<string>>();
        var filterRows = new List<IReadOnlyList<string>>();

        // THE REPAIR RUNS FIRST, and independently of the swap. Someone fixing a model that an
        // earlier build damaged wants the built-in columns back without re-enabling the thing
        // that removed them, so the two settings must not depend on each other.
        if (_settings.RestoreBuiltInRoomColumns)
        {
            var sample = doors.FirstOrDefault(d => ParameterHelper.Find(d, _settings.NumFrom) is not null);
            restoreRows = RestoreBuiltInColumns(sample, apply);
        }

        // RUNS BEFORE RepointSchedules TOO, and independently of it. This undoes a PAST manual
        // repointing of the FROM/TO set, not anything RepointSchedules itself did, so the two
        // settings have no ordering dependency on each other either.
        if (_settings.RestoreFromToScheduleColumns)
        {
            var sample = doors.FirstOrDefault(d => ParameterHelper.Find(d, _settings.NumFrom) is not null);
            restoreFromToRows = RestoreFromToSchedules(sample, apply);
        }

        // INDEPENDENT of the schedule-column settings above: these change only which parameter
        // the 'Ext Door' room columns read, plus their visibility and order.
        //
        // ONE OR THE OTHER, NEVER BOTH, and REFUSED rather than resolved when both are on. They
        // point the SAME TWO COLUMNS at different parameters - Reveal at the raw pair, Point at
        // the substituted one - so running both would leave whichever ran last in place while
        // the report claimed both had succeeded. Silently preferring one was the earlier
        // behaviour and it is worse than doing nothing: the setting that lost left no trace,
        // so the only symptom was a column reading the wrong room with nothing to explain it.
        if (_settings.PointExtDoorToSubstituted && _settings.RevealExtDoorRoomColumns)
        {
            _warnings.Add(
                "PointExtDoorToSubstituted and RevealExtDoorRoomColumns both point the 'Ext Door' " +
                "Rum nr/Rum columns, at different parameters, and both are switched on. Neither " +
                $"ran. Pick one: Point shows '{_settings.NumSubstituted}'/'{_settings.NameSubstituted}', " +
                $"so an exterior door shows the room opposite its '{_settings.ExteriorPrefix}' side; " +
                $"Reveal shows '{_settings.NumFrom}'/'{_settings.NameFrom}', which is the raw " +
                $"measurement and DOES show '{_settings.ExteriorPrefix}'.");
        }
        else if (_settings.PointExtDoorToSubstituted)
        {
            var sample = doors.FirstOrDefault(d => ParameterHelper.Find(d, _settings.NumSubstituted) is not null);
            revealRows = RevealExtDoorColumns(sample, apply,
                                              _settings.NumSubstituted, _settings.NameSubstituted);
        }
        else if (_settings.RevealExtDoorRoomColumns)
        {
            var sample = doors.FirstOrDefault(d => ParameterHelper.Find(d, _settings.NumFrom) is not null);
            revealRows = RevealExtDoorColumns(sample, apply, _settings.NumFrom, _settings.NameFrom);
        }

        // THE INVERSE OF RestoreFromToSchedules, and refused if both are on. Whichever ran last
        // would win and the report would claim both had succeeded, which is worse than doing
        // neither and saying so.
        if (_settings.RepointFromToScheduleColumns)
        {
            if (_settings.RestoreFromToScheduleColumns)
            {
                _warnings.Add(
                    "RepointFromToScheduleColumns and RestoreFromToScheduleColumns are opposites " +
                    "and both are switched on. Neither ran. Pick one: Repoint makes the FROM/TO " +
                    "columns follow a door flip; Restore puts them back on Revit's built-in " +
                    "fields, which cannot.");
            }
            else
            {
                var sample = doors.FirstOrDefault(d => ParameterHelper.Find(d, _settings.NumFrom) is not null);

                if (sample is null) ReportMissingScrp(doors);

                repointFromToRows = RepointSchedules(sample, apply, name =>
                    name.StartsWith("Door ", StringComparison.OrdinalIgnoreCase) &&
                    FromToScheduleName.IsMatch(name));
            }
        }

        // REFUSED WHEN BOTH ARE ON, same reasoning as the pair above: one adds the filter and
        // the other takes it away, so together they describe no outcome at all. The removal
        // path used to win silently, which meant switching the filter ON while forgetting to
        // switch removal OFF looked exactly like the filter never working.
        if (_settings.FilterExtDoorSchedules && _settings.RemoveExtDoorFilter)
        {
            _warnings.Add(
                "FilterExtDoorSchedules and RemoveExtDoorFilter are opposites and both are " +
                $"switched on. Neither ran. Pick one: Filter EXCLUDES doors flagged " +
                $"'{_settings.UdvendigFlag}' from the 'Ext Door * @V05' set; Remove takes that " +
                "filter back off. Note that filtering that set empties it in a model where every " +
                "door it lists is an exterior one.");
        }
        else if (_settings.FilterExtDoorSchedules || _settings.RemoveExtDoorFilter)
        {
            var sample = doors.FirstOrDefault(d => ParameterHelper.Find(d, _settings.UdvendigFlag) is not null);
            filterRows = FilterExtDoorSchedules(sample, apply);
        }

        if (_settings.RepointSchedules)
        {
            var sample = doors.FirstOrDefault(d => ParameterHelper.Find(d, _settings.NumFrom) is not null);

            // WHY THE DIAGNOSTIC IS BUILT HERE AND NOT INSIDE RepointSchedules: this is the
            // only place that still has the doors. Once no sample is found, the reason is
            // either "there are no doors" or "the doors do not carry these parameters", and
            // those need completely different actions from the user. Reporting a bare "no
            // door available" for both cost this tool three weeks of running silently - it
            // fired 21 times in one day, warned once each time, and changed nothing.
            if (sample is null) ReportMissingScrp(doors);

            scheduleRows = RepointSchedules(sample, apply);
        }

        // Stage 2 - compute the corrected room values.
        var plans = new List<DoorPlan>();
        foreach (var door in doors)
        {
            try
            {
                var plan = Plan(door);
                if (plan is not null) plans.Add(plan);
            }
            catch (Exception ex)
            {
                _skipped.Add($"{SafeId(door)} -- {ex.Message}");
            }
        }

        // The classification is the one thing here that can be entirely absent without any
        // door failing, so it gets its own diagnostic. Every other missing parameter makes a
        // door skip loudly; this one just quietly never separates the schedules.
        if (plans.Count > 0 &&
            plans.All(p => p.FlagCurrent == UdvendigClassification.FlagAbsent) &&
            !_flagTypeWarned)
        {
            _warnings.Add(
                $"'{_settings.UdvendigFlag}' is not on the doors in this model, so exterior doors " +
                "were resolved correctly but NOT classified. The 'Ext Door ...' schedules and the " +
                "interior 'Door ... FROM/TO' schedules therefore cannot be separated - every " +
                "exterior door still appears in both, and because its FROM and TO now name the " +
                "SAME room after substitution, its casing/frame/lining is billed to that room " +
                "TWICE. Bind it to Doors as a Yes/No (it is a project parameter in the office " +
                "template, not a shared one). Everything else in this run is unaffected.");
        }

        // Stage 3 - write.
        var applied = 0;
        var failed = new List<string>();

        if (apply)
        {
            foreach (var plan in plans.Where(p => p.Changed))
            {
                try
                {
                    var names = new[] { _settings.NumFrom, _settings.NameFrom, _settings.NumTo, _settings.NameTo };
                    for (var i = 0; i < names.Length; i++)
                    {
                        var parameter = ParameterHelper.Find(plan.Door, names[i]);
                        if (parameter is not null && !parameter.IsReadOnly) parameter.Set(plan.Desired[i]);
                    }

                    // The substituted pair, when the door carries it. Same guard as above:
                    // absent is skipped silently here because it is reported once for the whole
                    // model in BuildSummary, not once per door.
                    var subNames = new[] { _settings.NumSubstituted, _settings.NameSubstituted };
                    for (var i = 0; i < subNames.Length; i++)
                    {
                        var parameter = ParameterHelper.Find(plan.Door, subNames[i]);
                        if (parameter is not null && !parameter.IsReadOnly) parameter.Set(plan.Substituted[i]);
                    }

                    // WRITTEN ON EVERY DOOR, exterior and interior alike, for the same reason
                    // the four room values are: the schedules read it as their source of truth,
                    // and a door left unset is a door that silently belongs to neither the
                    // 'Ext Door ...' schedules nor the interior ones.
                    if (!plan.FlagOverridden)
                    {
                        var flag = ParameterHelper.Find(plan.Door, _settings.UdvendigFlag);
                        if (flag is { IsReadOnly: false, StorageType: StorageType.Integer })
                            flag.Set(plan.IsExterior ? 1 : 0);
                    }

                    applied++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{plan.Id} -- {ex.Message}");
                }
            }
        }

        return new UdvendigResult
        {
            // AuditSchedulePhases BEFORE BuildSummary: it fills _phaseless, which the summary
            // reads. C# evaluates object initialisers top to bottom, so this ordering holds -
            // but it is load-bearing, so do not reorder these two lines.
            Rows = BuildRows(plans, scheduleRows, restoreRows, restoreFromToRows, revealRows,
                             repointFromToRows, filterRows,
                             AuditSchedulePhases(), DescribeScheduleFields(doors)),
            Warnings = _warnings,
            Summary = BuildSummary(plans, scheduleRows, restoreRows, restoreFromToRows, revealRows,
                                   repointFromToRows, filterRows, apply, applied, failed),
        };
    }

    // ------------------------------------------------------------------ stage 2

    private DoorPlan? Plan(Element door)
    {
        if (ParameterHelper.Find(door, _settings.NumFrom) is null)
        {
            _skipped.Add($"{door.Id.Value} -- no SCRP room parameters");
            return null;
        }

        var comments = BipString(door, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments.Contains(_settings.SkipMarker, StringComparison.OrdinalIgnoreCase))
        {
            _skipped.Add($"{door.Id.Value} -- carries {_settings.SkipMarker}");
            return null;
        }

        var mark = BipString(door, BuiltInParameter.ALL_MODEL_MARK);
        var typeName = "?";
        if (door is FamilyInstance { Symbol: { } symbol })
            typeName = $"{symbol.Family.Name}: {symbol.Name}";

        // Evidence only - neither value changes a single decision below. See DoorPlan.
        bool facingFlipped = false, handFlipped = false;
        if (door is FamilyInstance flippable)
        {
            try
            {
                facingFlipped = flippable.FacingFlipped;
                handFlipped = flippable.HandFlipped;
            }
            catch
            {
                // A door that will not report its flip state still resolves normally.
            }
        }

        var (fromRoom, toRoom) = DoorRooms(door);
        var (fromNum, fromName) = RoomIdentity(fromRoom);
        var (toNum, toName) = RoomIdentity(toRoom);

        // WHAT THE SCHEDULES READ, captured separately and always - see DoorPlan.Native.
        // DoorRooms() may have derived the sides from the facing vector; the schedules do not.
        // Reading these unconditionally costs one extra pair of lookups per door and is the
        // difference between a report that explains a schedule cell and one that cannot.
        var nativeFrom = RoomOf(door, from: true);
        var nativeTo = RoomOf(door, from: false);
        var (nativeFromNum, nativeFromName) = RoomIdentity(nativeFrom);
        var (nativeToNum, nativeToName) = RoomIdentity(nativeTo);

        var extFrom = IsExterior(fromName);
        var extTo = IsExterior(toName);

        var outFrom = (Num: fromNum, Name: fromName);
        var outTo = (Num: toNum, Name: toName);
        var note = string.Empty;

        // SUBSTITUTION OFF: the SCRP values stay exactly as the sides were measured, so an
        // exterior door keeps 'Udvendig' on the side that faces out. See the setting's own
        // doc comment for when that is the right answer and what it costs downstream.
        if (!_settings.SubstituteExteriorSide)
        {
            note = extFrom || extTo
                ? $"'{_settings.ExteriorPrefix}' kept (substitution off)"
                : string.Empty;
        }
        else if (extFrom && extTo)
        {
            note = "both sides exterior -- left unchanged";
            _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: both sides are " +
                          $"'{_settings.ExteriorPrefix}' rooms ({fromName} / {toName}); no interior counterpart exists");
        }
        else if (extTo)
        {
            if (toNum.Length > 0 || toName.Length > 0)
            {
                outTo = (fromNum, fromName);
                note = $"TO '{toName}' ({toNum}) -> '{fromName}' ({fromNum})";
            }

            if (fromNum.Length == 0 && fromName.Length == 0)
            {
                _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: TO is '{toName}' " +
                              "but there is no room on the other side to copy");
                outTo = (toNum, toName);
                note = "no counterpart -- left unchanged";
            }
        }
        else if (extFrom)
        {
            if (fromNum.Length > 0 || fromName.Length > 0)
            {
                outFrom = (toNum, toName);
                note = $"FROM '{fromName}' ({fromNum}) -> '{toName}' ({toNum})";
            }

            if (toNum.Length == 0 && toName.Length == 0)
            {
                _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: FROM is '{fromName}' " +
                              "but there is no room on the other side to copy");
                outFrom = (fromNum, fromName);
                note = "no counterpart -- left unchanged";
            }
        }

        // THE SUBSTITUTED PAIR, computed regardless of SubstituteExteriorSide - that setting
        // decides what the FOUR SCRP parameters hold, not what this pair holds. Both are always
        // worked out so the two schedule sets can disagree deliberately. See NumSubstituted.
        //
        // Only the FROM side moves, and only when FROM is the exterior one: if TO is exterior
        // then FROM is already the interior room and is what this pair wants. Both sides
        // exterior leaves it alone - there is no interior room to name, and inventing one is
        // the failure this whole file is written to avoid.
        var subFrom = UdvendigClassification.SubstitutedSide(
            extFrom, extTo, fromNum, fromName, toNum, toName);

        if (fromNum.Length == 0 && fromName.Length == 0)
            _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: no FROM room");
        if (toNum.Length == 0 && toName.Length == 0)
            _warnings.Add($"{door.Id.Value} [{(mark.Length > 0 ? mark : "-")}]: no TO room");

        var current = new[]
        {
            ParameterHelper.Find(door, _settings.NumFrom)?.AsString() ?? string.Empty,
            ParameterHelper.Find(door, _settings.NameFrom)?.AsString() ?? string.Empty,
            ParameterHelper.Find(door, _settings.NumTo)?.AsString() ?? string.Empty,
            ParameterHelper.Find(door, _settings.NameTo)?.AsString() ?? string.Empty,
        };

        // ABSENT AND EMPTY MUST NOT LOOK THE SAME HERE. A door that does not carry the
        // substituted pair reads null, and comparing null to the desired value would mark
        // every such door as needing a write on every run - a permanently dirty model that
        // never converges. Null means "not there, nothing to do"; empty means "there and
        // blank", which IS a write.
        var subParameters = new[]
        {
            ParameterHelper.Find(door, _settings.NumSubstituted),
            ParameterHelper.Find(door, _settings.NameSubstituted),
        };

        var currentSub = subParameters.Any(p => p is null)
            ? null
            : new[] { subParameters[0]!.AsString() ?? string.Empty, subParameters[1]!.AsString() ?? string.Empty };

        return new DoorPlan
        {
            Door = door,
            Id = door.Id.Value,
            Mark = mark,
            TypeName = typeName,
            Raw = [fromNum, fromName, toNum, toName],
            Native =
            [
                nativeFromNum, nativeFromName, RoomDepartment(nativeFrom),
                nativeToNum, nativeToName, RoomDepartment(nativeTo),
            ],
            Desired = [outFrom.Num, outFrom.Name, outTo.Num, outTo.Name],
            Current = current,
            Substituted = [subFrom.Num, subFrom.Name],
            CurrentSubstituted = currentSub,
            Note = note,

            // Read off the RAW sides, before substitution - which is the only place the answer
            // still exists. outFrom/outTo have had the exterior side replaced by now.
            IsExterior = UdvendigClassification.IsExteriorDoor(extFrom, extTo),
            FlagCurrent = ReadFlag(door),
            FlagOverridden = IsFlagOverridden(door),
            FacingFlipped = facingFlipped,
            HandFlipped = handFlipped,
        };
    }

    /// <summary>
    /// The two rooms a door separates, by whichever rule is configured.
    ///
    /// The facing-derived answer is used only when it is COMPLETE enough to trust - see
    /// <see cref="FacingRooms"/>. Anything less falls through to Revit's own From/To, so a
    /// door this cannot measure is resolved exactly as it always was rather than blanked.
    /// </summary>
    private (Room? From, Room? To) DoorRooms(Element door)
    {
        if (_settings.DeriveSidesFromFacing && door is FamilyInstance instance)
        {
            var derived = FacingRooms(instance);

            if (derived is { } sides)
            {
                _facingDerived++;
                return sides;
            }

            _facingFallbacks++;
        }

        return (RoomOf(door, from: true), RoomOf(door, from: false));
    }

    /// <summary>
    /// The rooms either side of a door, found by probing along its facing vector.
    ///
    /// TO IS THE ROOM THE DOOR FACES. <c>FacingOrientation</c> already carries the instance's
    /// flip state, so this is the whole reason the derived answer follows the flip control when
    /// Revit's own From/To does not.
    ///
    /// THE PROBE IS LIFTED OFF THE FLOOR. A door's location point sits at the level, which is
    /// the room's own base plane - a point exactly on a boundary belongs to no room as far as
    /// GetRoomAtPoint is concerned, so probing at the door's own Z returns null on both sides
    /// and the whole derivation silently declines. Lifting it clear of the floor is what makes
    /// the query answerable at all.
    ///
    /// HAND FLIP IS DELIBERATELY IGNORED. It mirrors which way the leaf swings and moves the
    /// door through no wall, so both sides are unchanged by it. Only facing decides.
    ///
    /// Returns null when the answer would be a guess: no location, no facing, or no room found
    /// on EITHER side. One side missing is kept - a door onto a space with no room placed is a
    /// real arrangement, and Revit reports it the same way.
    /// </summary>
    private (Room? From, Room? To)? FacingRooms(FamilyInstance instance)
    {
        try
        {
            if (instance.Location is not LocationPoint location) return null;

            var facing = instance.FacingOrientation;
            if (facing is null || facing.IsZeroLength()) return null;

            facing = facing.Normalize();

            var origin = location.Point;
            var reach = ProbeDistance(instance);
            var lift = XYZ.BasisZ * ProbeLiftFeet;
            var phase = PhaseOf(instance);

            var to = RoomAt(origin + facing * reach + lift, phase);
            var from = RoomAt(origin - facing * reach + lift, phase);

            // Nothing found either way means the probe missed - a sloped floor, an unplaced
            // level, a door in a shaft. Revit's answer is better than two blanks.
            if (to is null && from is null) return null;

            // THE SAME ROOM ON BOTH SIDES IS NOT AN ANSWER, IT IS A MISSED PROBE.
            //
            // A door separates two places. When both probes land in one room the reach was too
            // short to clear the wall, or the room wraps around the wall's end and both points
            // fell inside it - either way the pair says nothing about which side is which, and
            // writing it produces a door whose FROM and TO are identical.
            //
            // Measured in FM_Template: doors 747 (29307762) and 748 (29308380) both resolved
            // to Entre on both sides. They are the two the report has been flagging as
            // disagreeing with Revit's native From/To, and Revit was right about them.
            //
            // Rejecting the pair defers to Revit for that door, which is what this method
            // already does for every other case it cannot answer confidently. A door genuinely
            // opening within a single room loses nothing: Revit's own From/To says the same
            // thing, and says it without pretending the two sides were distinguished.
            if (from is not null && to is not null && from.Id == to.Id) return null;

            return (from, to);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// How far past the door's centreline to look, in feet: half the host wall plus a margin
    /// clear of its finish faces.
    ///
    /// Kept deliberately short. Probing further would step through a narrow room entirely and
    /// report whatever lies beyond it - the corridor in this template is 1800 mm, so a generous
    /// reach would find the wrong room on the far side of it.
    /// </summary>
    private static double ProbeDistance(FamilyInstance instance)
    {
        var half = DefaultHalfWallFeet;

        try
        {
            if (instance.Host is Wall wall && wall.Width > 0) half = wall.Width / 2.0;
        }
        catch
        {
            // Unhosted or an unreadable host; the default clears a normal partition.
        }

        return half + ProbeMarginFeet;
    }

    /// <summary>Half a typical partition, for a door whose host cannot be measured.</summary>
    private const double DefaultHalfWallFeet = 0.5;

    /// <summary>Clearance past the wall face - about 300 mm, inside the room and short of the far wall.</summary>
    private const double ProbeMarginFeet = 1.0;

    /// <summary>About 900 mm up, to clear the room's base plane. See <see cref="FacingRooms"/>.</summary>
    private const double ProbeLiftFeet = 3.0;

    private int _facingDerived;
    private int _facingFallbacks;

    private Room? RoomAt(XYZ point, Phase? phase)
    {
        try
        {
            return phase is not null
                ? _doc.GetRoomAtPoint(point, phase)
                : _doc.GetRoomAtPoint(point);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The door's own creation phase, which is the phase its rooms must be read in.</summary>
    private Phase? PhaseOf(Element door)
    {
        try
        {
            var parameter = door.get_Parameter(BuiltInParameter.PHASE_CREATED);
            return parameter is null ? null : _doc.GetElement(parameter.AsElementId()) as Phase;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The From or To room of a door, resolved in the door's own phase. Revit exposes
    /// these as phase-dependent accessors; the bare property is the single-phase fallback.
    /// </summary>
    private Room? RoomOf(Element door, bool from)
    {
        if (door is not FamilyInstance instance) return null;

        var phase = PhaseOf(instance);

        if (phase is not null)
        {
            try { return from ? instance.get_FromRoom(phase) : instance.get_ToRoom(phase); }
            catch { /* fall back to the bare property */ }
        }

        try { return from ? instance.FromRoom : instance.ToRoom; }
        catch { return null; }
    }

    /// <summary>
    /// A room's Department - what every door schedule in this template shows as 'Lejlighed'.
    /// Empty for no room, and empty for the 'Udvendig' placeholder, which carries none.
    /// </summary>
    private static string RoomDepartment(Room? room) =>
        room is null ? string.Empty : BipString(room, BuiltInParameter.ROOM_DEPARTMENT);

    private (string Number, string Name) RoomIdentity(Room? room) =>
        room is null
            ? (string.Empty, string.Empty)
            : (BipString(room, BuiltInParameter.ROOM_NUMBER), BipString(room, BuiltInParameter.ROOM_NAME));

    private bool IsExterior(string name) =>
        UdvendigClassification.IsExteriorRoomName(name, _settings.ExteriorPrefix);

    /// <summary>
    /// The current value of the Udvendig classification flag, or -1 when it cannot be recorded
    /// on this door.
    ///
    /// A WRONG STORAGE TYPE IS REPORTED, NOT SILENTLY TREATED AS ABSENT. If someone adds
    /// '06-SCRP Udvendig' to the shared parameter file as Text rather than Yes/No, every door
    /// would otherwise return -1 forever and the schedules would never split, with the log
    /// saying only that the parameter was missing - which it is not. That distinction is the
    /// difference between "ask the office to add it" and "ask the office to fix its type".
    /// </summary>
    private int ReadFlag(Element door)
    {
        var parameter = ParameterHelper.Find(door, _settings.UdvendigFlag);
        if (parameter is null) return UdvendigClassification.FlagAbsent;

        if (parameter.StorageType != StorageType.Integer)
        {
            if (!_flagTypeWarned)
            {
                _flagTypeWarned = true;
                _warnings.Add(
                    $"'{_settings.UdvendigFlag}' exists on the doors but is {parameter.StorageType}, " +
                    "not a Yes/No. The exterior/interior classification cannot be recorded, so the " +
                    "'Ext Door ...' and interior door schedules cannot be separated by it. It needs " +
                    "to be a Yes/No (integer) parameter on Doors.");
            }

            return UdvendigClassification.FlagAbsent;
        }

        try { return parameter.AsInteger(); }
        catch { return UdvendigClassification.FlagAbsent; }
    }

    private bool _flagTypeWarned;

    /// <summary>
    /// Whether a human has taken this door's flag out of the tool's hands. Anything that
    /// cannot be read counts as NOT overridden - an unreadable override must not quietly
    /// disable the classification on every door in the model.
    /// </summary>
    private bool IsFlagOverridden(Element door)
    {
        try
        {
            var parameter = ParameterHelper.Find(door, _settings.UdvendigFlagOverride);

            return parameter is { StorageType: StorageType.Integer } && parameter.AsInteger() != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string BipString(Element element, BuiltInParameter bip)
    {
        try
        {
            var parameter = element.get_Parameter(bip);
            if (parameter is null || !parameter.HasValue || parameter.StorageType != StorageType.String)
                return string.Empty;

            return parameter.AsString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeId(Element element)
    {
        try { return element.Id.Value.ToString(); }
        catch { return "?"; }
    }

    // ------------------------------------------------------------------ stage 1

    /// <summary>
    /// Swaps built-in From/To Room columns over to the SCRP shared parameters, preserving
    /// heading, width, alignment and visibility.
    ///
    /// Idempotent: a column already pointing at a SCRP parameter is not a From/To Room
    /// field, so it simply is not matched. Never throws - a schedule that cannot be
    /// converted is reported and skipped.
    /// </summary>
    /// <summary>
    /// Says exactly why no door could supply the SCRP parameter ids, and what the doors DO
    /// carry that looks close.
    ///
    /// The four names are an office standard bound as shared parameters. This tool cannot
    /// create them: their GUIDs have to match the ones the door schedules and every other
    /// model already use, and inventing a GUID would produce parameters that look correct,
    /// schedule correctly in this model, and silently fail to line up with anything else.
    /// So the honest failure is a precise report of what is missing.
    /// </summary>
    private void ReportMissingScrp(IReadOnlyList<Element> doors)
    {
        if (doors.Count == 0)
        {
            _warnings.Add("no doors in scope - the model has none, or none were selected. " +
                          "Nothing to resolve.");
            return;
        }

        var probe = doors[0];
        var near = ParameterHelper.NamesContaining(probe, "SCRP");

        var expected = string.Join(", ",
            _settings.NumFrom, _settings.NameFrom, _settings.NumTo, _settings.NameTo);

        _warnings.Add(
            $"none of the {doors.Count} door(s) carry the SCRP room parameters, so every door " +
            "was skipped and nothing was written.");

        _warnings.Add($"expected on each door: {expected}");

        if (near.Count > 0)
        {
            _warnings.Add(
                $"door {probe.Id.Value} does carry these SCRP-like parameters: {string.Join(", ", near)}" +
                " - if one of those is meant to be used, the names in UdvendigSettings need to match it.");
            return;
        }

        _warnings.Add($"door {probe.Id.Value} carries no parameter with 'SCRP' in the name at all.");

        // IS THE DEFINITION ALREADY IN THIS DOCUMENT, just not bound to Doors? That is a very
        // different situation from "absent", and only one of the two can be fixed here. A
        // definition already present carries the office GUID, so binding it to Doors keeps
        // every existing schedule and model lined up. A definition that is absent can only
        // come from the office shared parameter file - creating it here would mint a NEW GUID,
        // producing parameters that look right, schedule right in this model, and match
        // nothing anywhere else.
        var inDocument = SharedDefinitionsPresent();

        _warnings.Add(inDocument.Count > 0
            ? "these SCRP definitions DO exist in this document but are not bound to Doors: " +
              $"{string.Join(", ", inDocument)}. Bind them to the Doors category (instance) - " +
              "they already carry the correct GUIDs, so nothing else in the model shifts."
            : "none of the four exist in this document at all. Bind them to the Doors category " +
              "(instance) from the office shared parameter file, then re-run. This tool will not " +
              "create them: their GUIDs must match the ones the door schedules already use, and " +
              "a new GUID would look correct here and line up with nothing else.");
    }

    /// <summary>
    /// Which of the four SCRP names already exist as shared parameter definitions in this
    /// document, whatever they are bound to. Purely diagnostic.
    /// </summary>
    private List<string> SharedDefinitionsPresent()
    {
        // The four SHARED room parameters only. The classification flag is a project parameter
        // in the office template, so it would never appear in a SharedParameterElement sweep
        // and listing it here would report it permanently absent - see OfferScrpBinding.
        var wanted = new[] { _settings.NumFrom, _settings.NameFrom, _settings.NumTo, _settings.NameTo }
            .ToDictionary(ParameterHelper.Squash, n => n);

        var found = new List<string>();

        try
        {
            foreach (var element in new FilteredElementCollector(_doc)
                         .OfClass(typeof(SharedParameterElement)))
            {
                try
                {
                    var name = (element as SharedParameterElement)?.Name;
                    if (name is null) continue;

                    if (wanted.TryGetValue(ParameterHelper.Squash(name), out var matched))
                        found.Add(matched);
                }
                catch
                {
                    // An unreadable definition tells us nothing either way.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Udvendig: could not enumerate shared parameter definitions: {ex.Message}");
        }

        return found.Distinct().ToList();
    }

    /// <param name="scope">
    /// Extra name test, ANDed with <see cref="UdvendigSettings.ScheduleNameContains"/>. Null
    /// means the settings decide alone, which is the original whole-model behaviour. Supplied
    /// by <see cref="UdvendigSettings.RepointFromToScheduleColumns"/> to confine the swap to
    /// the interior 'Door * FROM/TO' set.
    /// </param>
    private List<IReadOnlyList<string>> RepointSchedules(
        Element? sampleDoor, bool apply, Func<string, bool>? scope = null)
    {
        var rows = new List<IReadOnlyList<string>>();

        if (sampleDoor is null)
        {
            // The detailed reason is already in _warnings - see ReportMissingScrp, which runs
            // where the doors are still in hand. Nothing to add here.
            return rows;
        }

        // ElementId of each SCRP parameter, taken from a real door instance.
        var wanted = new Dictionary<(ScheduleFieldType, BuiltInParameter), (ElementId Id, string Name)>();
        foreach (var (fieldType, roomBip, target) in FieldMap)
        {
            var name = target(_settings);
            var parameter = ParameterHelper.Find(sampleDoor, name);
            if (parameter is null)
            {
                _warnings.Add($"parameter '{name}' not found on door {sampleDoor.Id.Value}");
                continue;
            }

            wanted[(fieldType, roomBip)] = (parameter.Id, name);
        }

        foreach (var schedule in new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            try
            {
                if (schedule.IsTemplate) continue;

                var scheduleName = schedule.Name;

                if (scope is not null && !scope(scheduleName)) continue;

                // An empty list means no name filtering at all - every schedule is examined,
                // and the field test decides what actually gets touched.
                if (_settings.ScheduleNameContains.Count > 0 &&
                    !_settings.ScheduleNameContains.Any(t =>
                        scheduleName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var definition = schedule.Definition;

                // Reverse order: inserting at i and removing i+1 keeps the field count
                // stable, so lower indices stay valid either way.
                for (var index = definition.GetFieldCount() - 1; index >= 0; index--)
                {
                    var field = definition.GetField(index);
                    var fieldType = field.FieldType;

                    if (fieldType is not (ScheduleFieldType.FromRoom or ScheduleFieldType.ToRoom)) continue;

                    (ScheduleFieldType, BuiltInParameter)? key = null;
                    foreach (var (mapType, roomBip, _) in FieldMap)
                    {
                        if (mapType != fieldType) continue;
                        if (field.ParameterId == new ElementId(roomBip))
                        {
                            key = (fieldType, roomBip);
                            break;
                        }
                    }

                    if (key is null || !wanted.TryGetValue(key.Value, out var replacement))
                    {
                        rows.Add(new[] { scheduleName, field.GetName(), fieldType.ToString(), "-",
                                         "no mapping for this column" });
                        continue;
                    }

                    // A filter or sort rule pointing at this column is MIGRATED, not a reason to
                    // give up. The old behaviour refused the swap and told the user to clear the
                    // rule by hand, which meant every schedule that grouped by room number - i.e.
                    // all of them - kept showing 'Udvendig' after a run that reported success.
                    //
                    // Dropping the rules silently would change which rows appear, which is why
                    // refusing was the safe first version. Re-pointing them at the replacement
                    // column keeps the same grouping and the same filtering, on a field that now
                    // holds the corrected value instead of the read-only built-in one.
                    var rules = CaptureRules(definition, field);

                    var schedulable = definition.GetSchedulableFields()
                        .FirstOrDefault(c => c.ParameterId == replacement.Id);

                    if (schedulable is null)
                    {
                        rows.Add(new[] { scheduleName, field.GetName(), fieldType.ToString(), replacement.Name,
                                         "SKIPPED - parameter not schedulable here" });
                        continue;
                    }

                    if (!apply)
                    {
                        rows.Add(new[] { scheduleName, field.GetName(), fieldType.ToString(), replacement.Name,
                                         "would swap" });
                        continue;
                    }

                    var heading = field.ColumnHeading;
                    var width = field.GridColumnWidth;
                    var alignment = field.HorizontalAlignment;
                    var hidden = field.IsHidden;

                    ScheduleField newField;
                    try
                    {
                        newField = definition.InsertField(schedulable, index);
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new[] { scheduleName, heading, fieldType.ToString(), replacement.Name,
                                         $"FAILED to insert: {ex.Message}" });
                        continue;
                    }

                    TrySet(() => newField.ColumnHeading = heading);
                    TrySet(() => newField.GridColumnWidth = width);
                    TrySet(() => newField.HorizontalAlignment = alignment);
                    TrySet(() => newField.IsHidden = hidden);

                    // RULES BEFORE REMOVAL. Revit will not let a field be removed while a filter
                    // or sort rule still points at it, so the rules have to be moved onto the new
                    // column first. Doing it the other way round fails on exactly the schedules
                    // this was written to fix.
                    var moved = MigrateRules(definition, rules, newField.FieldId, scheduleName, heading);

                    try
                    {
                        definition.RemoveField(index + 1);

                        rows.Add(new[] { scheduleName, heading, fieldType.ToString(), replacement.Name,
                                         moved > 0 ? $"swapped, {moved} rule(s) re-pointed" : "swapped" });
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new[] { scheduleName, heading, fieldType.ToString(), replacement.Name,
                                         $"FAILED to remove old column: {ex.Message}" });
                        _warnings.Add($"{scheduleName}: '{heading}' now appears twice -- delete the built-in " +
                                      "one by hand.");
                    }
                }
            }
            catch (Exception ex)
            {
                rows.Add(new[] { SafeName(schedule), "-", "-", "-", $"schedule skipped: {ex.Message}" });
            }
        }

        return rows;
    }

    /// <summary>
    /// Re-adds the built-in From/To Room columns that earlier runs removed.
    ///
    /// THE RULE IS DELIBERATELY NARROW: for each of the four (field type, room property) pairs,
    /// if the schedule carries the SCRP replacement but NOT the built-in original, add the
    /// original back. Nothing else qualifies. A schedule that never had the column is left
    /// alone - this repairs what this tool broke and does not impose a column on anyone.
    ///
    /// ADDITIVE ONLY. There is no RemoveField anywhere in this method, which is what makes it
    /// safe to run on a model whose schedules have since been edited by hand.
    /// </summary>
    private List<IReadOnlyList<string>> RestoreBuiltInColumns(Element? sampleDoor, bool apply)
    {
        var rows = new List<IReadOnlyList<string>>();

        if (sampleDoor is null) return rows;

        // The SCRP parameter id for each pair, read off a real door exactly as the swap did.
        var scrp = new Dictionary<(ScheduleFieldType, BuiltInParameter), ElementId>();
        foreach (var (fieldType, roomBip, target) in FieldMap)
        {
            var parameter = ParameterHelper.Find(sampleDoor, target(_settings));
            if (parameter is not null) scrp[(fieldType, roomBip)] = parameter.Id;
        }

        if (scrp.Count == 0) return rows;

        foreach (var schedule in new FilteredElementCollector(_doc)
                     .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            try
            {
                if (schedule.IsTemplate) continue;

                var scheduleName = schedule.Name;
                var definition = schedule.Definition;

                foreach (var (fieldType, roomBip, target) in FieldMap)
                {
                    if (!scrp.TryGetValue((fieldType, roomBip), out var scrpId)) continue;

                    var builtInId = new ElementId(roomBip);

                    var hasScrp = false;
                    var hasBuiltIn = false;
                    var scrpIndex = 0;

                    for (var index = 0; index < definition.GetFieldCount(); index++)
                    {
                        var field = definition.GetField(index);

                        if (field.ParameterId == scrpId) { hasScrp = true; scrpIndex = index; }

                        // The built-in column is identified by BOTH its parameter and its field
                        // type - From Room: Number and To Room: Number share a parameter id and
                        // differ only in which room they read.
                        if (field.FieldType == fieldType && field.ParameterId == builtInId)
                            hasBuiltIn = true;
                    }

                    if (!hasScrp || hasBuiltIn) continue;

                    var schedulable = definition.GetSchedulableFields()
                        .FirstOrDefault(c => c.FieldType == fieldType && c.ParameterId == builtInId);

                    if (schedulable is null)
                    {
                        rows.Add(new[] { scheduleName, target(_settings), fieldType.ToString(),
                                         "SKIPPED - the built-in field is not schedulable here" });
                        continue;
                    }

                    if (!apply)
                    {
                        rows.Add(new[] { scheduleName, target(_settings), fieldType.ToString(),
                                         "would restore" });
                        continue;
                    }

                    try
                    {
                        // Beside its SCRP counterpart, so the pair reads together.
                        definition.InsertField(schedulable, Math.Min(scrpIndex + 1, definition.GetFieldCount()));

                        rows.Add(new[] { scheduleName, target(_settings), fieldType.ToString(),
                                         "RESTORED - heading/width need tidying by hand" });
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new[] { scheduleName, target(_settings), fieldType.ToString(),
                                         $"FAILED: {ex.Message}" });
                    }
                }
            }
            catch (Exception ex)
            {
                rows.Add(new[] { SafeName(schedule), "-", "-", $"schedule skipped: {ex.Message}" });
            }
        }

        return rows;
    }

    /// <summary>
    /// Only 'Door ... FROM ...' / 'Door ... TO ...' schedules qualify - never 'Ext Door ...'.
    /// A whole-word match on FROM/TO avoids tripping on incidental substrings like "Doorstep".
    /// </summary>
    private static readonly Regex FromToScheduleName = new(@"\b(FROM|TO)\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Swaps the SCRP-bound Rum nr/Rum columns in the interior 'Door * FROM/TO' schedules back
    /// to Revit's built-in From Room/To Room fields, removing the SCRP field each replaces.
    ///
    /// See <see cref="UdvendigSettings.RestoreFromToScheduleColumns"/> for why. This is the
    /// mirror image of <see cref="RepointSchedules"/> - same field-preservation and rule-
    /// migration mechanics, opposite direction - scoped to the one schedule set that must stop
    /// reading a value the resolver overwrites for every exterior door.
    /// </summary>
    private List<IReadOnlyList<string>> RestoreFromToSchedules(Element? sampleDoor, bool apply)
    {
        var rows = new List<IReadOnlyList<string>>();

        if (sampleDoor is null) return rows;

        // SCRP parameter id -> which built-in (field type, room property) it replaced. The
        // exact reverse of the lookup RepointSchedules builds.
        var byScrpId = new Dictionary<ElementId, (ScheduleFieldType FieldType, BuiltInParameter RoomBip)>();
        foreach (var (fieldType, roomBip, target) in FieldMap)
        {
            var parameter = ParameterHelper.Find(sampleDoor, target(_settings));
            if (parameter is not null) byScrpId[parameter.Id] = (fieldType, roomBip);
        }

        if (byScrpId.Count == 0) return rows;

        foreach (var schedule in new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            try
            {
                if (schedule.IsTemplate) continue;

                var scheduleName = schedule.Name;

                // SCOPE: the interior 'Door ... FROM/TO' set only. 'Ext Door ...' schedules
                // still need the substituted room and must never be touched here.
                if (!scheduleName.StartsWith("Door ", StringComparison.OrdinalIgnoreCase)) continue;
                if (!FromToScheduleName.IsMatch(scheduleName)) continue;

                var definition = schedule.Definition;

                for (var index = definition.GetFieldCount() - 1; index >= 0; index--)
                {
                    var field = definition.GetField(index);
                    if (!byScrpId.TryGetValue(field.ParameterId, out var target)) continue;

                    var builtInId = new ElementId(target.RoomBip);
                    var schedulable = definition.GetSchedulableFields()
                        .FirstOrDefault(c => c.FieldType == target.FieldType && c.ParameterId == builtInId);

                    if (schedulable is null)
                    {
                        rows.Add(new[] { scheduleName, field.GetName(), target.FieldType.ToString(),
                                         "SKIPPED - built-in field not schedulable here" });
                        continue;
                    }

                    if (!apply)
                    {
                        rows.Add(new[] { scheduleName, field.GetName(), target.FieldType.ToString(), "would restore" });
                        continue;
                    }

                    var heading = field.ColumnHeading;
                    var width = field.GridColumnWidth;
                    var alignment = field.HorizontalAlignment;
                    var hidden = field.IsHidden;

                    // RULES BEFORE REMOVAL - see the identical note in RepointSchedules.
                    var rules = CaptureRules(definition, field);

                    ScheduleField newField;
                    try
                    {
                        newField = definition.InsertField(schedulable, index);
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new[] { scheduleName, heading, target.FieldType.ToString(),
                                         $"FAILED to insert: {ex.Message}" });
                        continue;
                    }

                    TrySet(() => newField.ColumnHeading = heading);
                    TrySet(() => newField.GridColumnWidth = width);
                    TrySet(() => newField.HorizontalAlignment = alignment);
                    TrySet(() => newField.IsHidden = hidden);

                    var moved = MigrateRules(definition, rules, newField.FieldId, scheduleName, heading);

                    try
                    {
                        definition.RemoveField(index + 1);

                        rows.Add(new[] { scheduleName, heading, target.FieldType.ToString(),
                                         moved > 0 ? $"restored to built-in, {moved} rule(s) re-pointed" : "restored to built-in" });
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new[] { scheduleName, heading, target.FieldType.ToString(),
                                         $"FAILED to remove SCRP column: {ex.Message}" });
                        _warnings.Add($"{scheduleName}: '{heading}' now appears twice -- delete the SCRP one by hand.");
                    }
                }
            }
            catch (Exception ex)
            {
                rows.Add(new[] { SafeName(schedule), "-", "-", $"schedule skipped: {ex.Message}" });
            }
        }

        return rows;
    }

    /// <summary>
    /// Shows the hidden SCRP room columns in the 'Ext Door * @V05' set and hides the empty
    /// calculated columns in front of them. See <see cref="UdvendigSettings.RevealExtDoorRoomColumns"/>.
    ///
    /// ORDER MATTERS TO THE READER, so the name column is moved to sit immediately after the
    /// number column. Left alone it surfaces wherever it happens to live - field 17 of 18 in
    /// 'Ext Door Casing @V05' - which puts 'Rum' at the far right of the schedule, nowhere near
    /// the 'Rum nr' it belongs to. Revit keeps field order and column order as one list, so
    /// this is the only way to place it.
    ///
    /// EVERY STEP IS INDEPENDENT AND GUARDED. A schedule missing one of the two SCRP fields
    /// still gets the other shown; a reorder that Revit refuses leaves the columns visible and
    /// merely out of place. Half a repair is worth more than none here, because the failure is
    /// that the values are invisible, and any of these steps alone makes some of them visible.
    /// </summary>
    /// <param name="numberName">Parameter the 'Rum nr' column should read.</param>
    /// <param name="nameName">Parameter the 'Rum' column should read.</param>
    private List<IReadOnlyList<string>> RevealExtDoorColumns(
        Element? sampleDoor, bool apply, string numberName, string nameName)
    {
        var rows = new List<IReadOnlyList<string>>();

        if (sampleDoor is null) return rows;

        var numberParameter = ParameterHelper.Find(sampleDoor, numberName);
        var nameParameter = ParameterHelper.Find(sampleDoor, nameName);

        if (numberParameter is null && nameParameter is null)
        {
            _warnings.Add(
                $"neither '{numberName}' nor '{nameName}' is on the doors, so the 'Ext Door' " +
                "columns were left as they are.");
            return rows;
        }

        foreach (var schedule in new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            try
            {
                if (schedule.IsTemplate) continue;

                var scheduleName = schedule.Name;

                // SCOPE: the 'Ext Door ...' set only. The interior 'Door ... FROM/TO' set reads
                // Revit's built-in room fields on purpose and must never be touched here.
                if (!scheduleName.StartsWith("Ext Door ", StringComparison.OrdinalIgnoreCase)) continue;

                var definition = schedule.Definition;

                ScheduleField? numberField = null;
                ScheduleField? nameField = null;

                for (var index = 0; index < definition.GetFieldCount(); index++)
                {
                    var field = definition.GetField(index);

                    if (numberParameter is not null && field.ParameterId == numberParameter.Id)
                        numberField = field;
                    else if (nameParameter is not null && field.ParameterId == nameParameter.Id)
                        nameField = field;
                }

                // ADDED IF ABSENT, because the substituted pair is new and no schedule carries
                // it yet. Added HIDDEN and revealed below by the same code that reveals a field
                // which was already there, so both paths end in one state.
                numberField ??= AddHidden(definition, numberParameter, apply);
                nameField ??= AddHidden(definition, nameParameter, apply);

                if (numberField is null && nameField is null)
                {
                    rows.Add(new[] { scheduleName, "-", "-", "SKIPPED - neither column present or schedulable" });
                    continue;
                }

                // EVERY OTHER COLUMN CLAIMING THESE HEADINGS. Broader than the calculated
                // fields this started with, and it has to be: pointing the schedule at a new
                // pair leaves the OLD pair still visible under the same heading, and two
                // columns both headed 'Rum' showing different rooms is worse than the bug.
                //
                // Identity, not heading, decides what is spared - the two target fields are
                // excluded by FieldId, so a heading match can never hide the column being
                // revealed.
                var rivals = new List<ScheduleField>();

                for (var index = 0; index < definition.GetFieldCount(); index++)
                {
                    var field = definition.GetField(index);

                    if (numberField is not null && field.FieldId == numberField.FieldId) continue;
                    if (nameField is not null && field.FieldId == nameField.FieldId) continue;

                    if (string.Equals(field.ColumnHeading, "Rum nr", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(field.ColumnHeading, "Rum", StringComparison.OrdinalIgnoreCase))
                        rivals.Add(field);
                }

                var formulas = rivals;

                // IDEMPOTENCE. Already-correct schedules must report and change nothing, so a
                // second run is safe and says so rather than silently rewriting headings.
                var alreadyShown = numberField is not { IsHidden: true } && nameField is not { IsHidden: true };
                if (alreadyShown && formulas.All(f => f.IsHidden))
                {
                    rows.Add(new[] { scheduleName, "-", "-", "already correct" });
                    continue;
                }

                if (!apply)
                {
                    if (numberField is { IsHidden: true })
                        rows.Add(new[] { scheduleName, numberField.GetName(), "show as 'Rum nr'", "would reveal" });
                    if (nameField is { IsHidden: true })
                        rows.Add(new[] { scheduleName, nameField.GetName(), "show as 'Rum'", "would reveal" });
                    foreach (var formula in formulas.Where(f => !f.IsHidden))
                        rows.Add(new[] { scheduleName, formula.ColumnHeading ?? "?", "calculated, empty", "would hide" });
                    continue;
                }

                // HIDE THE STAND-INS FIRST. Two columns headed 'Rum nr' visible at once is a
                // worse schedule than the broken one, and if a later step throws, the state
                // left behind should be "one empty column hidden", not "two columns the same".
                foreach (var formula in formulas.Where(f => !f.IsHidden))
                {
                    try
                    {
                        formula.IsHidden = true;
                        rows.Add(new[] { scheduleName, formula.ColumnHeading ?? "?", "calculated, empty", "HIDDEN" });
                    }
                    catch (Exception ex)
                    {
                        rows.Add(new[] { scheduleName, formula.ColumnHeading ?? "?", "calculated, empty",
                                         $"FAILED to hide: {ex.Message}" });
                    }
                }

                Reveal(numberField, "Rum nr", scheduleName, rows);
                Reveal(nameField, "Rum", scheduleName, rows);

                // ANCHORED AFTER 'Lejlighed', not merely beside each other. Revealing or adding
                // a field always puts it last, which is why the pair used to land at the far
                // right of the schedule - column H/I of nine - instead of beside the Afdeling /
                // Lejlighed group it belongs with. The interior 'Door * FROM/TO' set already has
                // Rum nr and Rum sitting right after Lejlighed natively; this makes the 'Ext
                // Door' set match that layout instead of inventing a different one.
                //
                // Falls back to the pair's OWN original position - not the end of the schedule -
                // when no 'Lejlighed' column exists here, so a schedule this office built
                // without that column is left where it was rather than shuffled to a place
                // nobody chose.
                if (numberField is not null)
                {
                    try
                    {
                        var order = definition.GetFieldOrder().ToList();
                        var originalNumberIndex = order.IndexOf(numberField.FieldId);

                        var block = new List<ScheduleFieldId> { numberField.FieldId };
                        if (nameField is not null) block.Add(nameField.FieldId);

                        foreach (var id in block) order.Remove(id);

                        var anchor = FindFieldByHeading(definition, "Lejlighed");
                        var anchorIndex = anchor is not null ? order.IndexOf(anchor.FieldId) : -1;

                        var insertAt = anchorIndex >= 0 ? anchorIndex + 1
                            : originalNumberIndex >= 0 ? Math.Min(originalNumberIndex, order.Count)
                            : order.Count;

                        for (var i = 0; i < block.Count; i++)
                            order.Insert(insertAt + i, block[i]);

                        definition.SetFieldOrder(order);

                        rows.Add(new[]
                        {
                            scheduleName, nameField is not null ? "Rum nr / Rum" : "Rum nr", "column order",
                            anchorIndex >= 0 ? "moved after 'Lejlighed'" : "kept together",
                        });
                    }
                    catch (Exception ex)
                    {
                        _warnings.Add($"{scheduleName}: columns are visible but could not be re-ordered " +
                                      $"({ex.Message}). Drag them in the Fields dialog.");
                    }
                }
            }
            catch (Exception ex)
            {
                rows.Add(new[] { SafeName(schedule), "-", "-", $"schedule skipped: {ex.Message}" });
            }
        }

        return rows;
    }

    /// <summary>
    /// The first field in a schedule carrying this exact heading, or null. Used to anchor the
    /// revealed room columns beside a column that already means something to the reader,
    /// rather than wherever Revit happened to append them.
    /// </summary>
    private static ScheduleField? FindFieldByHeading(ScheduleDefinition definition, string heading)
    {
        for (var index = 0; index < definition.GetFieldCount(); index++)
        {
            var field = definition.GetField(index);
            if (string.Equals(field.ColumnHeading, heading, StringComparison.OrdinalIgnoreCase))
                return field;
        }

        return null;
    }

    /// <summary>
    /// Adds a parameter to a schedule as a hidden field, or returns null if it is not
    /// schedulable there. Null <paramref name="parameter"/> returns null - a door that does not
    /// carry the parameter cannot have it scheduled, and that is reported by the caller.
    /// </summary>
    private static ScheduleField? AddHidden(ScheduleDefinition definition, Parameter? parameter, bool apply)
    {
        if (parameter is null) return null;

        try
        {
            var schedulable = definition.GetSchedulableFields()
                .FirstOrDefault(c => c.ParameterId == parameter.Id);

            // In a dry run the field is not added, so there is nothing to return. The caller
            // reports "would reveal" from the parameter it already holds.
            if (schedulable is null || !apply) return null;

            var field = definition.AddField(schedulable);
            field.IsHidden = true;
            return field;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shows one column and gives it the heading the office expects. Separate from the caller
    /// so a failure on the number column cannot cost the name column, and vice versa.
    /// </summary>
    private static void Reveal(
        ScheduleField? field, string heading, string scheduleName, List<IReadOnlyList<string>> rows)
    {
        if (field is null) return;

        var was = field.ColumnHeading ?? string.Empty;

        try
        {
            var changed = false;

            if (field.IsHidden)
            {
                field.IsHidden = false;
                changed = true;
            }

            // The SCRP columns carry Revit's default headings here ('From Room: Name'), which
            // is not what the office prints. Only rewritten when it differs, so a heading
            // someone deliberately chose is not churned on every run.
            if (!string.Equals(was, heading, StringComparison.Ordinal))
            {
                field.ColumnHeading = heading;
                changed = true;
            }

            rows.Add(new[]
            {
                scheduleName, was.Length > 0 ? was : "(no heading)", $"show as '{heading}'",
                changed ? "REVEALED" : "already shown",
            });
        }
        catch (Exception ex)
        {
            rows.Add(new[] { scheduleName, was, $"show as '{heading}'", $"FAILED: {ex.Message}" });
        }
    }

    /// <summary>
    /// Adds "<see cref="UdvendigSettings.UdvendigFlag"/> = No" to every 'Ext Door * @V05'
    /// schedule, so doors classified exterior stop appearing there.
    ///
    /// THE FLAG IS THE RIGHT THING TO FILTER ON, and the family name is not. Measured in
    /// FM_Template 2027V1.00_EN: door 752 is an 'Interior Door V17' between two interior rooms
    /// and door 747 an 'Exterior Door V22' onto 'Udvendig', but a family name says only which
    /// family somebody picked, not which side of the building the door faces. The flag is
    /// re-derived from the measured rooms on every run, so a door that stops being exterior
    /// leaves the filter's scope by itself.
    ///
    /// IDEMPOTENT. A schedule that already carries this filter is left alone - the filter is
    /// matched by field and rule, not by position, so a second run adds nothing and reports
    /// 'already filtered'.
    /// </summary>
    private List<IReadOnlyList<string>> FilterExtDoorSchedules(Element? sampleDoor, bool apply)
    {
        var rows = new List<IReadOnlyList<string>>();

        if (sampleDoor is null) return rows;

        var flag = ParameterHelper.Find(sampleDoor, _settings.UdvendigFlag);

        if (flag is null)
        {
            _warnings.Add(
                $"'{_settings.UdvendigFlag}' is not on the doors, so the 'Ext Door' schedules " +
                "cannot be filtered by it. Nothing was changed.");
            return rows;
        }

        foreach (var schedule in new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
        {
            try
            {
                if (schedule.IsTemplate) continue;

                var scheduleName = schedule.Name;
                if (!scheduleName.StartsWith("Ext Door ", StringComparison.OrdinalIgnoreCase)) continue;

                var definition = schedule.Definition;

                // The flag has to BE a field before it can be filtered on. Revit will not
                // filter by a parameter the schedule does not carry, so an absent field is
                // added hidden - it is machinery, not a column anybody wants to read.
                ScheduleField? flagField = null;
                for (var index = 0; index < definition.GetFieldCount(); index++)
                {
                    var candidate = definition.GetField(index);
                    if (candidate.ParameterId == flag.Id) { flagField = candidate; break; }
                }

                // REMOVAL MODE. Runs before the add path and returns for this schedule either
                // way, so the two can never both act on one schedule in a single pass.
                if (_settings.RemoveExtDoorFilter)
                {
                    if (flagField is null)
                    {
                        rows.Add(new[] { scheduleName, _settings.UdvendigFlag, "-", "no flag field, nothing to remove" });
                        continue;
                    }

                    var current = definition.GetFilters();
                    var doomed = new List<int>();

                    for (var i = 0; i < current.Count; i++)
                        if (current[i].FieldId == flagField.FieldId) doomed.Add(i);

                    if (doomed.Count == 0)
                    {
                        rows.Add(new[] { scheduleName, _settings.UdvendigFlag, "-", "no filter to remove" });
                        continue;
                    }

                    if (!apply)
                    {
                        rows.Add(new[] { scheduleName, _settings.UdvendigFlag, $"{doomed.Count} filter(s)", "would remove" });
                        continue;
                    }

                    // Reverse order: removing at i shifts every later index down by one.
                    for (var i = doomed.Count - 1; i >= 0; i--)
                        definition.RemoveFilter(doomed[i]);

                    rows.Add(new[] { scheduleName, _settings.UdvendigFlag, $"{doomed.Count} filter(s)", "REMOVED" });
                    continue;
                }

                if (flagField is null)
                {
                    var schedulable = definition.GetSchedulableFields()
                        .FirstOrDefault(c => c.ParameterId == flag.Id);

                    if (schedulable is null)
                    {
                        rows.Add(new[] { scheduleName, _settings.UdvendigFlag, "-",
                                         "SKIPPED - flag is not schedulable here" });
                        continue;
                    }

                    if (!apply)
                    {
                        rows.Add(new[] { scheduleName, _settings.UdvendigFlag, "add hidden field + filter",
                                         "would filter" });
                        continue;
                    }

                    flagField = definition.AddField(schedulable);
                    flagField.IsHidden = true;
                }

                var already = definition.GetFilters()
                    .Any(f => f.FieldId == flagField.FieldId);

                if (already)
                {
                    rows.Add(new[] { scheduleName, _settings.UdvendigFlag, "-", "already filtered" });
                    continue;
                }

                if (!apply)
                {
                    rows.Add(new[] { scheduleName, _settings.UdvendigFlag, "= No", "would filter" });
                    continue;
                }

                // Yes/No is stored as an integer, so the filter compares against 0 rather than
                // against a boolean - Revit has no boolean filter rule for these.
                definition.AddFilter(new ScheduleFilter(flagField.FieldId, ScheduleFilterType.Equal, 0));

                rows.Add(new[] { scheduleName, _settings.UdvendigFlag, "= No", "FILTERED" });
            }
            catch (Exception ex)
            {
                rows.Add(new[] { SafeName(schedule), _settings.UdvendigFlag, "-", $"FAILED: {ex.Message}" });
            }
        }

        return rows;
    }

    /// <summary>Which filter and sort/group rules currently point at this column.</summary>
    private sealed record ScheduleRules(
        IReadOnlyList<int> FilterIndexes, IReadOnlyList<int> SortIndexes)
    {
        public int Count => FilterIndexes.Count + SortIndexes.Count;
    }

    /// <summary>
    /// Records the rules that reference a column, BY INDEX rather than by object.
    ///
    /// The rule objects are snapshots; the schedule is about to gain a field, and re-reading
    /// the lists afterwards is the only way to hold something still valid. Indexes survive an
    /// insert because InsertField changes the FIELD order, not the rule order.
    /// </summary>
    private static ScheduleRules CaptureRules(ScheduleDefinition definition, ScheduleField field)
    {
        var filters = new List<int>();
        var sorts = new List<int>();

        try
        {
            var current = definition.GetFilters();
            for (var i = 0; i < current.Count; i++)
                if (current[i].FieldId == field.FieldId) filters.Add(i);

            var grouping = definition.GetSortGroupFields();
            for (var i = 0; i < grouping.Count; i++)
                if (grouping[i].FieldId == field.FieldId) sorts.Add(i);
        }
        catch
        {
            // Unreadable rules cannot be migrated; the removal below will report if Revit
            // then refuses to drop the column, which is the honest outcome.
        }

        return new ScheduleRules(filters, sorts);
    }

    /// <summary>
    /// Re-points captured rules at <paramref name="replacement"/>. Returns how many moved.
    ///
    /// GROUPING AND FILTERING ARE PRESERVED, not discarded. A schedule grouped by room number
    /// still groups by room number - on the column that now carries the resolved value instead
    /// of the read-only built-in one, so the rows a user sees are unchanged except that
    /// 'Udvendig' has become a real room.
    /// </summary>
    private int MigrateRules(
        ScheduleDefinition definition, ScheduleRules rules, ScheduleFieldId replacement,
        string scheduleName, string heading)
    {
        if (rules.Count == 0) return 0;

        var moved = 0;

        try
        {
            if (rules.FilterIndexes.Count > 0)
            {
                var filters = definition.GetFilters();

                foreach (var index in rules.FilterIndexes)
                {
                    if (index >= filters.Count) continue;

                    var filter = filters[index];
                    filter.FieldId = replacement;
                    definition.SetFilter(index, filter);
                    moved++;
                }
            }

            if (rules.SortIndexes.Count > 0)
            {
                var grouping = definition.GetSortGroupFields();

                foreach (var index in rules.SortIndexes)
                {
                    if (index >= grouping.Count) continue;

                    var sort = grouping[index];
                    sort.FieldId = replacement;
                    definition.SetSortGroupField(index, sort);
                    moved++;
                }
            }
        }
        catch (Exception ex)
        {
            _warnings.Add($"{scheduleName}: '{heading}' - {moved} of {rules.Count} filter/sort " +
                          $"rule(s) were re-pointed before Revit refused ({ex.Message}). The column " +
                          "may have been left in place; check the schedule's grouping.");
        }

        return moved;
    }

    private static void TrySet(Action action)
    {
        try { action(); }
        catch { /* a property the field type does not support */ }
    }

    private static string SafeName(Element element)
    {
        try { return element.Name; }
        catch { return "?"; }
    }

    /// <summary>
    /// Every field in every door/window schedule, with what it is actually bound to.
    ///
    /// WHY THIS IS WORTH A TABLE OF ITS OWN. A schedule column that shows nothing is the least
    /// diagnosable failure in Revit: the heading is whatever someone typed, so 'Rum nr' tells
    /// you what it was MEANT to be and nothing about what it reads. Measured on this template -
    /// the 'Ext Door ...' schedules show a populated 'Lejlighed' beside a blank 'Rum nr' and
    /// 'Rum', and the re-pointing table proves those two are not room fields at all. Something
    /// else is behind them, and no amount of staring at the schedule will say what.
    ///
    /// This resolves each field to its real parameter - built-in by enum name, project and
    /// shared by definition name - so a blank column can be traced to a parameter that is
    /// unbound, empty, or simply not the one anybody intended.
    ///
    /// Read-only and reported, never acted on. Deciding what a column OUGHT to point at is a
    /// modelling decision, and this tool has no business guessing at it.
    /// </summary>
    private List<IReadOnlyList<string>> DescribeScheduleFields(IReadOnlyList<Element> doors)
    {
        var rows = new List<IReadOnlyList<string>>();

        // NAME -> the parameter id the DOORS actually carry. Built once, from a real door, and
        // it is what turns this table from descriptive into diagnostic. See CarriedByDoor.
        var carried = DoorParameterIds(doors);

        try
        {
            foreach (var schedule in new FilteredElementCollector(_doc)
                         .OfClass(typeof(ViewSchedule))
                         .Cast<ViewSchedule>())
            {
                try
                {
                    if (schedule.IsTemplate) continue;

                    var scheduleName = schedule.Name;

                    // Door and window schedules only. The whole-model sweep is what makes the
                    // re-pointing safe, but a field dump of every schedule in the project would
                    // bury the ones this is about.
                    if (!scheduleName.Contains("Door", StringComparison.OrdinalIgnoreCase) &&
                        !scheduleName.Contains("Window", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var definition = schedule.Definition;

                    for (var index = 0; index < definition.GetFieldCount(); index++)
                    {
                        try
                        {
                            var field = definition.GetField(index);
                            var verdict = CarriedByDoor(field, carried);

                            // Collected for the summary, because a mismatch buried in the
                            // longest table in the report is a mismatch nobody reads.
                            if (verdict.StartsWith("NAME MATCHES", StringComparison.Ordinal))
                                _mismatched.Add($"'{scheduleName}' column '{field.ColumnHeading}'");

                            rows.Add(new[]
                            {
                                scheduleName,
                                index.ToString(),
                                field.ColumnHeading ?? string.Empty,
                                field.FieldType.ToString(),
                                FieldParameter(field),
                                verdict,
                                field.IsHidden ? "hidden" : "shown",
                            });
                        }
                        catch (Exception ex)
                        {
                            rows.Add(new[] { scheduleName, index.ToString(), "?", "?", $"unreadable: {ex.Message}", "?", "?" });
                        }
                    }
                }
                catch (Exception ex)
                {
                    rows.Add(new[] { SafeName(schedule), "-", "-", "-", $"schedule unreadable: {ex.Message}", "-", "-" });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Udvendig: could not enumerate schedule fields: {ex.Message}");
        }

        return rows;
    }

    /// <summary>
    /// Whether a schedule's Phase is set, for every door/window schedule that reads a room.
    ///
    /// THE FAILURE THIS CATCHES, AND WHY NOTHING ELSE IN THIS FILE CATCHES IT. From Room and
    /// To Room are not stored on the door. Revit derives them per PHASE - the API spells this
    /// out as FamilyInstance.get_FromRoom(Phase), and a schedule supplies that phase from its
    /// own Phase property. Leave the property unset and there is no phase to derive against,
    /// so EVERY room-relationship field in that schedule comes back blank at once: number,
    /// name and Department together. Ordinary instance and type columns are unaffected,
    /// because they were never phase-dependent.
    ///
    /// That signature - the room columns blank while Type and the office's own project
    /// columns print normally - is exactly what a mis-bound column looks like from the
    /// outside, and DescribeScheduleFields cannot tell them apart: the field is bound
    /// correctly, to the right built-in, and still shows nothing.
    ///
    /// MEASURED IN FM_Template 2027V1.00_EN, 2026-09-02. 'Door Casing FROM @V03' (28389474)
    /// carried Phase = 0 while all 20 other door schedules carried 12589, the one phase the
    /// doors and rooms are both in. Same view template (28389834) and same phase filter (375)
    /// on the schedules either side of it, so the phase was the only variable. Its Lejlighed,
    /// Rum nr and Rum columns were blank on all 6 doors; 'Ext Door Casing @V05' printed them.
    ///
    /// REPORTED, NEVER REPAIRED. Phase is a property of a Template schedule, and the standing
    /// rule is that those are the office's - see RestoreBuiltInRoomColumns for the one
    /// sanctioned exception and why it is not a precedent. Setting a schedule's phase also
    /// changes WHICH ELEMENTS IT LISTS, not merely what the room columns say, so it is a
    /// modelling decision and not a repair an add-in gets to make on someone's behalf.
    /// </summary>
    private List<IReadOnlyList<string>> AuditSchedulePhases()
    {
        var rows = new List<IReadOnlyList<string>>();

        try
        {
            foreach (var schedule in new FilteredElementCollector(_doc)
                         .OfClass(typeof(ViewSchedule))
                         .Cast<ViewSchedule>())
            {
                try
                {
                    if (schedule.IsTemplate) continue;

                    var scheduleName = schedule.Name;

                    if (!scheduleName.Contains("Door", StringComparison.OrdinalIgnoreCase) &&
                        !scheduleName.Contains("Window", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Only schedules that actually READ a room are worth reporting. A door
                    // schedule with no room column is unaffected by its phase being unset, and
                    // listing it would bury the ones that are broken.
                    var roomFields = RoomFieldCount(schedule);
                    if (roomFields == 0) continue;

                    var phaseId = schedule.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId()
                                  ?? ElementId.InvalidElementId;

                    var phase = _doc.GetElement(phaseId) as Phase;

                    // InvalidElementId is the usual shape of "never set", but an id that no
                    // longer resolves to a Phase fails identically and reads the same way to
                    // the person holding the blank schedule, so both land here.
                    var broken = phase is null;

                    if (broken) _phaseless.Add(scheduleName);

                    rows.Add(new[]
                    {
                        scheduleName,
                        broken ? $"NOT SET (id {phaseId.Value})" : SafeName(phase!),
                        roomFields.ToString(),
                        broken
                            ? "BLANK ROOM COLUMNS -- set Phase in the schedule's Properties"
                            : "ok",
                    });
                }
                catch (Exception ex)
                {
                    rows.Add(new[] { SafeName(schedule), "?", "?", $"unreadable: {ex.Message}" });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Udvendig: could not audit schedule phases: {ex.Message}");
        }

        return rows;
    }

    /// <summary>
    /// How many fields in this schedule are room-relationship fields - the ones that go blank
    /// together when the schedule has no phase. Counted by field TYPE, not by heading: the
    /// heading is whatever someone typed, and this whole audit exists because headings lie.
    /// </summary>
    private static int RoomFieldCount(ViewSchedule schedule)
    {
        try
        {
            var definition = schedule.Definition;
            var count = 0;

            for (var index = 0; index < definition.GetFieldCount(); index++)
            {
                try
                {
                    var type = definition.GetField(index).FieldType;

                    if (type == ScheduleFieldType.FromRoom ||
                        type == ScheduleFieldType.ToRoom ||
                        type == ScheduleFieldType.Room)
                        count++;
                }
                catch
                {
                    // One unreadable field must not cost the count of the rest.
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Every non-built-in parameter a real door carries, as NAME -> the id of the definition
    /// behind it. Taken from an actual instance, so it is the ground truth about what these
    /// doors hold - not what a shared parameter file or a schedule field claims they hold.
    /// </summary>
    private static Dictionary<string, ElementId> DoorParameterIds(IReadOnlyList<Element> doors)
    {
        var map = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

        // First door that yields anything. They are all the same category, and a second pass
        // would only overwrite identical answers.
        foreach (var door in doors)
        {
            try
            {
                foreach (Parameter parameter in door.Parameters)
                {
                    try
                    {
                        // Built-ins are negative and are matched by enum, not by id, so they
                        // are not what this table is about.
                        if (parameter.Id.Value < 0) continue;

                        var name = parameter.Definition?.Name;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        map[name!] = parameter.Id;
                    }
                    catch
                    {
                        // One unreadable parameter must not cost the rest of the map.
                    }
                }
            }
            catch
            {
                continue;
            }

            if (map.Count > 0) break;
        }

        return map;
    }

    /// <summary>
    /// Whether the parameter a schedule field reads is THE SAME PARAMETER the doors carry, or
    /// merely one with the same name.
    ///
    /// THE FAILURE THIS EXISTS TO CATCH, AND WHY THE NAME COLUMN CANNOT CATCH IT. Two shared
    /// parameters can carry identical names and different GUIDs - from two shared parameter
    /// files, or one file edited between binds. Revit keeps them apart by GUID and shows them
    /// apart nowhere: both print the same text in the field list, in the parameter dialog, and
    /// in FieldParameter above. Bind the schedule column to one and the doors to the other and
    /// the column is permanently, silently empty, while every name a person can see agrees.
    ///
    /// MEASURED IN FM_Template 2027V1.00_EN, 2026-09-02. The 'Ext Door * @V05' set binds its
    /// 'Rum nr' and 'Rum' columns to '02-SCRP Num fr' and '04-SCRP Nam fr', and prints blank on
    /// all three exterior doors - while door 747 (29307762) holds '02-SCRP Num fr' = 6 and
    /// '04-SCRP Nam fr' = Entre, and appears in that very schedule. A field bound to a
    /// parameter its element demonstrably holds cannot come back empty, so the field and the
    /// door are not looking at the same parameter.
    ///
    /// Comparing ids settles it. Same id, the binding is sound and a blank cell means the
    /// VALUE is empty. Different id under the same name, the binding is the fault and no
    /// amount of re-running this tool will fill the column - the doors are being written
    /// correctly, to a parameter the schedule is not reading.
    /// </summary>
    private static string CarriedByDoor(ScheduleField field, Dictionary<string, ElementId> carried)
    {
        try
        {
            var id = field.ParameterId;

            // Built-in fields are matched by enum and are never ambiguous this way.
            if (id == ElementId.InvalidElementId || id.Value < 0) return "n/a (built-in)";

            var heading = field.GetName();

            if (string.IsNullOrWhiteSpace(heading) || !carried.TryGetValue(heading, out var doorId))
                return "NO -- doors carry no parameter of this name";

            return doorId == id
                ? "yes"
                : $"NAME MATCHES, ID DOES NOT -- field reads {id.Value}, doors carry {doorId.Value} " +
                  "(two parameters share this name; the column can never fill)";
        }
        catch (Exception ex)
        {
            return $"unreadable: {ex.Message}";
        }
    }

    /// <summary>
    /// What a schedule field actually reads. Built-in parameters carry a negative id and are
    /// named by their enum; everything else is a real element whose definition holds the name
    /// the user sees in the parameter dialog.
    /// </summary>
    private string FieldParameter(ScheduleField field)
    {
        try
        {
            var id = field.ParameterId;

            if (id == ElementId.InvalidElementId) return "(not a parameter)";

            // Built-in parameter ids are negative by construction.
            if (id.Value < 0) return $"BuiltIn.{(BuiltInParameter)id.Value}";

            var element = _doc.GetElement(id);

            // SharedParameterElement derives from ParameterElement, so this covers both the
            // office's shared parameters and plain project ones.
            if (element is ParameterElement parameter)
                return parameter.GetDefinition()?.Name ?? SafeName(parameter);

            return element is null ? $"id {id.Value} (missing)" : SafeName(element);
        }
        catch (Exception ex)
        {
            return $"unreadable: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------- output

    private static List<IReadOnlyList<string>> BuildRows(
        IReadOnlyList<DoorPlan> plans,
        IReadOnlyList<IReadOnlyList<string>> scheduleRows,
        IReadOnlyList<IReadOnlyList<string>> restoreRows,
        IReadOnlyList<IReadOnlyList<string>> restoreFromToRows,
        IReadOnlyList<IReadOnlyList<string>> revealRows,
        IReadOnlyList<IReadOnlyList<string>> repointFromToRows,
        IReadOnlyList<IReadOnlyList<string>> filterRows,
        IReadOnlyList<IReadOnlyList<string>> phaseRows,
        IReadOnlyList<IReadOnlyList<string>> fieldRows)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "ElementId", "Mark", "Type",

                // RENAMED FROM 'Revit FROM nr' etc. These are the sides THIS TOOL resolved,
                // which are Revit's own only when DeriveSidesFromFacing is off. The old
                // heading asserted otherwise and sent three separate investigations down the
                // wrong path - see DoorPlan.Raw.
                "Resolved FROM nr", "Resolved FROM name", "Resolved TO nr", "Resolved TO name",

                // What the door schedules actually read. See DoorPlan.Native.
                "Native FROM nr", "Native FROM name", "Native FROM Lejlighed",
                "Native TO nr", "Native TO name", "Native TO Lejlighed",

                "Written FROM nr", "Written FROM name", "Written TO nr", "Written TO name",

                // What the 'Ext Door' set reads. Beside the written pair on purpose: the two
                // are meant to DIFFER on an exterior door, and seeing them side by side is how
                // anyone checks that the substitution actually happened.
                "Substituted nr", "Substituted name", "Sub pair present",

                "Udvendig", "Facing flipped", "Hand flipped", "Sides differ", "Changed", "Detail",
            },
        };

        foreach (var plan in plans)
        {
            rows.Add(new[]
            {
                plan.Id.ToString(), plan.Mark, plan.TypeName,
                plan.Raw[0], plan.Raw[1], plan.Raw[2], plan.Raw[3],
                plan.Native[0], plan.Native[1], plan.Native[2],
                plan.Native[3], plan.Native[4], plan.Native[5],
                plan.Desired[0], plan.Desired[1], plan.Desired[2], plan.Desired[3],
                plan.Substituted[0], plan.Substituted[1],
                plan.CurrentSubstituted is null ? "NOT ON DOOR" : "yes",
                plan.FlagCurrent == UdvendigClassification.FlagAbsent ? "n/a"
                    : plan.FlagOverridden ? "overridden"
                    : plan.IsExterior ? "YES" : "no",
                plan.FacingFlipped ? "YES" : "no",
                plan.HandFlipped ? "YES" : "no",
                plan.SidesDifferFromNative ? "YES" : "no",
                plan.Changed ? "YES" : "no", plan.Note,
            });
        }

        if (scheduleRows.Count > 0)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[] { "Schedule", "Column", "Was", "Now", "Result" });
            rows.AddRange(scheduleRows);
        }

        if (restoreRows.Count > 0)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[] { "Schedule", "SCRP column present", "Built-in field restored", "Result" });
            rows.AddRange(restoreRows);
        }

        if (restoreFromToRows.Count > 0)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[] { "Schedule", "Column", "Field type", "Result" });
            rows.AddRange(restoreFromToRows);
        }

        if (revealRows.Count > 0)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[] { "Schedule", "Column", "Action", "Result" });
            rows.AddRange(revealRows);
        }

        if (repointFromToRows.Count > 0)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[] { "Schedule", "Column", "Was", "Now", "Result" });
            rows.AddRange(repointFromToRows);
        }

        if (filterRows.Count > 0)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[] { "Schedule", "Filter on", "Rule", "Result" });
            rows.AddRange(filterRows);
        }

        // BEFORE the field dump, because it is short and it answers the same question faster.
        // A blank room column has two causes: the field is bound to the wrong thing (the field
        // dump finds that) or the schedule has no phase (only this finds that). Checking the
        // one-line answer before reading the long table saves the reader the long table.
        if (phaseRows.Count > 0)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[] { "Schedule", "Phase", "Room fields", "Result" });
            rows.AddRange(phaseRows);
        }

        // LAST, because it is the longest table and the least often needed - but it is the only
        // one that can explain a column showing nothing. See DescribeScheduleFields.
        if (fieldRows.Count > 0)
        {
            rows.Add(Array.Empty<string>());
            rows.Add(new[]
            {
                "Schedule", "Field #", "Column heading", "Field type", "Reads parameter",
                "Doors carry it?", "Visibility",
            });
            rows.AddRange(fieldRows);
        }

        return rows;
    }

    private List<string> BuildSummary(
        IReadOnlyList<DoorPlan> plans,
        IReadOnlyList<IReadOnlyList<string>> scheduleRows,
        IReadOnlyList<IReadOnlyList<string>> restoreRows,
        IReadOnlyList<IReadOnlyList<string>> restoreFromToRows,
        IReadOnlyList<IReadOnlyList<string>> revealRows,
        IReadOnlyList<IReadOnlyList<string>> repointFromToRows,
        IReadOnlyList<IReadOnlyList<string>> filterRows,
        bool apply,
        int applied,
        IReadOnlyList<string> failed)
    {
        // StartsWith for the same reason as the FROM/TO counter below: RepointSchedules
        // appends ", N rule(s) re-pointed" whenever a column carried filter or sort rules,
        // and an equality test silently reported those swaps as zero.
        var swapped = scheduleRows.Count(
            r => r.Count > 4 && r[4].StartsWith("swapped", StringComparison.Ordinal));
        var substituted = plans.Count(p => p.Note.Contains("->"));

        var restored = restoreRows.Count(r => r.Count > 3 && r[3].StartsWith("RESTORED", StringComparison.Ordinal));
        var restoredFromTo = restoreFromToRows.Count(r => r.Count > 3 && r[3].StartsWith("restored to built-in", StringComparison.Ordinal));

        var summary = new List<string>
        {
            $"Udvendig room resolver -- {(apply ? "APPLIED" : "DRY RUN -- nothing was modified")}",
            _settings.RepointSchedules
                ? $"Schedules: {swapped} column(s) re-pointed to the SCRP parameters ({scheduleRows.Count} examined)."
                : "Schedules: LEFT ALONE. Column re-pointing is off, so Revit's built-in From/To Room " +
                  "columns are not touched. The SCRP parameters are still written to the doors; add " +
                  "them as schedule fields by hand where they are wanted.",
            $"Examined {plans.Count} doors.",
            $"{substituted} had an '{_settings.ExteriorPrefix}' side substituted.",
            $"{plans.Count(p => p.Changed)} need parameter writes; {applied} written.",
        };

        // The number the schedules depend on. Reported separately from the substitution count
        // because they are NOT the same: a door with an exterior side but no interior room to
        // copy is still an exterior door, and still belongs in the 'Ext Door ...' schedules,
        // even though nothing was substituted for it.
        // WHICH RULE PRODUCED THESE NUMBERS. Reported unconditionally when the facing rule is
        // on, because a value derived by probing and a value taken from Revit are
        // indistinguishable once written, and only one of them follows the flip control.
        if (_settings.DeriveSidesFromFacing)
        {
            summary.Add(
                $"Door sides FROM FACING: {_facingDerived} door(s) measured by probing either side " +
                $"of the facing vector, so their FROM/TO follow the flip control. " +
                (_facingFallbacks > 0
                    ? $"{_facingFallbacks} could not be probed and kept Revit's own From/To - those " +
                      "will NOT follow a flip."
                    : "None needed Revit's own From/To as a fallback.") +
                " Note these values deliberately disagree with Revit's built-in From Room / To " +
                "Room on any flipped door.");
        }

        // THE LINE THAT EXPLAINS A WRONG SCHEDULE CELL. Reported whenever it is non-zero,
        // independently of DeriveSidesFromFacing, because a divergence can also come from a
        // door Revit simply reports differently - and either way the 'Door * FROM/TO'
        // schedules show the native value, not the one this tool resolved.
        var diverged = plans.Where(p => p.SidesDifferFromNative).ToList();
        if (diverged.Count > 0)
        {
            summary.Add(
                $"NATIVE FROM/TO DISAGREES on {diverged.Count} door(s): " +
                string.Join(", ", diverged.Take(10).Select(p => $"{p.Id}[{(p.Mark.Length > 0 ? p.Mark : "-")}]")) +
                (diverged.Count > 10 ? ", ..." : string.Empty) +
                ". Any schedule column bound to Revit's built-in From/To Room shows the NATIVE " +
                "value for these doors, which is read-only and cannot be corrected from here. " +
                "The 'Native ...' columns in the report show exactly what such a column displays.");
        }

        // FIRST OF THE SCHEDULE FAULTS, because it is the one that makes this tool look broken
        // when it is working perfectly. Every other line here explains a value; this one
        // explains a column that cannot show any value at all, no matter what is written.
        if (_mismatched.Count > 0)
        {
            summary.Add(
                $"COLUMN BOUND TO THE WRONG PARAMETER on {_mismatched.Count} schedule column(s): " +
                string.Join(", ", _mismatched.Take(6)) +
                (_mismatched.Count > 6 ? ", ..." : string.Empty) +
                ". Each reads a parameter whose NAME matches one the doors carry but whose id " +
                "does not - two shared parameters with the same name and different GUIDs. Revit " +
                "shows them identically everywhere a person can look, so the column appears " +
                "correctly bound and is permanently empty. THE DOORS ARE BEING WRITTEN " +
                "CORRECTLY; the schedule is reading a different parameter, and re-running this " +
                "tool cannot fix it. Delete the column and re-add the field, then confirm the " +
                "'Doors carry it?' column in the field table below reads 'yes'.");
        }

        // BEFORE the Lejlighed line below, and that order matters. A schedule with no phase
        // blanks its room columns wholesale, so if this fires, the Lejlighed explanation is
        // answering a question the reader does not have - the Department is not the reason
        // the cell is empty, and following that advice means editing rooms for nothing.
        if (_phaseless.Count > 0)
        {
            summary.Add(
                $"NO PHASE SET on {_phaseless.Count} door/window schedule(s): " +
                string.Join(", ", _phaseless.Take(6).Select(n => $"'{n}'")) +
                (_phaseless.Count > 6 ? ", ..." : string.Empty) +
                ". Revit derives From Room and To Room PER PHASE, so a schedule with no phase " +
                "has nothing to derive against and every room column in it - number, name and " +
                "Lejlighed alike - is blank for every row, while Type and the project columns " +
                "print normally. The fields are bound correctly; the schedule cannot resolve " +
                "them. Fix it in the schedule's Properties -> Phase, not in the fields, and " +
                "not by re-running this tool. See the 'Phase' table for which schedules and " +
                "what the others are set to. NOTE that changing a schedule's phase also " +
                "changes which elements it lists, so match the phase its siblings use.");
        }

        // Blank 'Lejlighed' is the most-reported symptom and has one boring cause almost every
        // time, so it gets named rather than left for someone to work out from the columns.
        //
        // BOTH SIDES, AND IT USED TO BE ONLY ONE. This checked Native[2]/Native[1] - the FROM
        // pair - and said nothing at all about the TO pair at Native[5]/Native[4]. Half the
        // door schedules in this template are the 'Door * TO ...' set, so for half the places
        // the symptom appears, the one line written to explain it stayed silent.
        //
        // Measured in FM_Template 2027V1.00_EN, 2026-09-02: 'Door Casing TO @V03' showed a
        // blank Lejlighed on its two 'Udvendig' rows. Room 'Udvendig 99' (12199000) carries
        // Department = "" - the exact condition this line exists to name - and the report did
        // not mention it, because the blank was on the TO side.
        foreach (var (side, nameIndex, departmentIndex) in new[]
                 {
                     ("FROM", 1, 2),
                     ("TO", 4, 5),
                 })
        {
            // A room with a name but no Department. Requiring the NAME to be present is what
            // separates "this room has no Department" from "there is no room on this side at
            // all" - the second is a different fault with a different fix, and rolling them
            // together is how a door with no room gets a Department typed into it for nothing.
            var noDepartment = plans
                .Where(p => p.Native[departmentIndex].Trim().Length == 0 &&
                            p.Native[nameIndex].Trim().Length > 0)
                .ToList();

            if (noDepartment.Count == 0) continue;

            // Named, because "which rooms" is the first question anyone asks next and the
            // answer is usually one or two rooms shared across every door in the list.
            var rooms = noDepartment
                .Select(p => p.Native[nameIndex].Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            summary.Add(
                $"BLANK 'Lejlighed' EXPECTED on {noDepartment.Count} door(s), {side} side: the " +
                $"native {side} room carries no Department. Room(s): " +
                string.Join(", ", rooms.Take(6).Select(n => $"'{n}'")) +
                (rooms.Count > 6 ? ", ..." : string.Empty) +
                $". 'Lejlighed' is bound to the built-in {(side == "FROM" ? "From" : "To")} Room: " +
                "Department field in these schedules, so it is empty whenever that room has none - " +
                $"which is always true of '{_settings.ExteriorPrefix}'. Not a fault in this tool, " +
                "and not fixable by re-running it: give the room a Department, or point the column " +
                "elsewhere - but consider whether blank is simply CORRECT here, because the " +
                "exterior belongs to no apartment and inventing one is worse than an empty cell." +
                (_phaseless.Count > 0
                    ? " IGNORE THIS for the schedules named in the NO PHASE line above - their " +
                      "cells are blank for that reason instead, and giving the room a Department " +
                      "will not fill them."
                    : string.Empty));
        }

        var classified = plans
            .Where(p => p.FlagCurrent != UdvendigClassification.FlagAbsent && !p.FlagOverridden)
            .ToList();
        if (classified.Count > 0)
        {
            var exterior = classified.Count(p => p.IsExterior);
            var overridden = plans.Count(p => p.FlagOverridden);

            summary.Add(
                $"Classification ('{_settings.UdvendigFlag}'): {exterior} exterior, " +
                $"{classified.Count - exterior} interior, across {classified.Count} door(s)." +
                (overridden > 0
                    ? $" {overridden} left alone ('{_settings.UdvendigFlagOverride}' is ticked)."
                    : string.Empty));
        }

        if (_settings.RestoreBuiltInRoomColumns)
        {
            summary.Add(
                $"BUILT-IN COLUMNS RESTORED: {restored} of {restoreRows.Count} candidate column(s) " +
                "were added back beside their SCRP counterpart. Heading, width and position could " +
                "not be recovered - they went with the removed field - so the restored columns " +
                "carry Revit's default heading and need tidying. Turn this setting off once the " +
                "model is repaired; it only ever adds, so leaving it on is harmless but pointless.");
        }

        if (_settings.RestoreFromToScheduleColumns)
        {
            summary.Add(
                $"DOOR FROM/TO SCHEDULES REPOINTED TO BUILT-IN: {restoredFromTo} of {restoreFromToRows.Count} " +
                "candidate column(s) swapped back from the SCRP parameters to Revit's own From Room/To " +
                "Room fields, so 'Udvendig' can show there again. The 'Ext Door * @V05' set was not " +
                "touched. Turn this setting off once the model is repaired - it is a one-time swap, and " +
                "running it again finds nothing left to swap.");
        }

        if (_settings.RepointFromToScheduleColumns && repointFromToRows.Count > 0)
        {
            // StartsWith, NOT equality. RepointSchedules reports a successful swap as either
            // "swapped" or "swapped, N rule(s) re-pointed" depending on whether the column
            // carried filter or sort rules that had to move with it. An equality test counted
            // only the first, so a repoint that worked on a schedule grouped by room number -
            // which is all of them - reported "0 column(s) re-pointed" and read exactly like a
            // no-op. Measured 2026-09-02: the columns WERE on the SCRP parameters at 03:13
            // after a run that claimed zero.
            var followTheFlip = repointFromToRows.Count(
                r => r.Count > 4 && r[4].StartsWith("swapped", StringComparison.Ordinal));

            summary.Add(
                $"DOOR FROM/TO SCHEDULES NOW FOLLOW THE FLIP: {followTheFlip} column(s) re-pointed from " +
                "Revit's built-in From/To Room to the SCRP parameters. Those are rewritten on " +
                "every door change, so flipping a door now updates the schedule with no manual " +
                "edit - and any room typed into these columns by hand is superseded, which is the " +
                "point: a typed value never re-derived. The 'Ext Door * @V05' set was not touched.");
        }

        if (_settings.RemoveExtDoorFilter && filterRows.Count > 0)
        {
            var removed = filterRows.Count(r => r.Count > 3 && r[3] == "REMOVED");

            summary.Add(
                $"EXT DOOR FILTER REMOVED from {removed} schedule(s): they list every door they " +
                "listed before it was added. Filtering that set to exclude exterior doors emptied " +
                "it - doors 747 and 751 are the only exterior ones and were the only ones those " +
                "schedules held. The hidden 'CRP Exterior Door' field was left in place; it costs " +
                "nothing and is what any future filter on this would need.");
        }

        if (_settings.FilterExtDoorSchedules && filterRows.Count > 0)
        {
            var filtered = filterRows.Count(r => r.Count > 3 && r[3] == "FILTERED");
            var alreadyFiltered = filterRows.Count(r => r.Count > 3 && r[3] == "already filtered");

            summary.Add(
                $"EXT DOOR SCHEDULES FILTERED: {filtered} schedule(s) now exclude doors classified " +
                $"'{_settings.UdvendigFlag}'" +
                (alreadyFiltered > 0 ? $"; {alreadyFiltered} already carried the filter" : string.Empty) +
                ". THIS CHANGES WHICH ROWS THEY LIST, so their totals have changed - check them " +
                "against yesterday's before issuing anything from this model. The filter reads " +
                "the flag this tool re-derives every run, so it follows the model rather than " +
                "freezing today's answer.");
        }

        // THE PAIR THE 'Ext Door' SET NEEDS, and the one thing this tool cannot supply itself.
        // Reported whether present or absent, because "the column is still blank" has two
        // completely different answers depending on which it is.
        var carriesSub = plans.Count(p => p.CurrentSubstituted is not null);

        if (plans.Count > 0 && carriesSub == 0)
        {
            summary.Add(
                $"SUBSTITUTED PAIR MISSING: no door carries '{_settings.NumSubstituted}' or " +
                $"'{_settings.NameSubstituted}', so the substituted room was computed for all " +
                $"{plans.Count} door(s) and written nowhere. The 'Ext Door * @V05' set cannot show " +
                "the room opposite an 'Udvendig' side until these exist. THIS TOOL WILL NOT CREATE " +
                "THEM: a parameter minted here carries a new GUID, looks right in this model and " +
                "matches nothing in any other project on the office standard. Add both to the " +
                "office shared parameter file as Text, bind them to Doors, and re-run - everything " +
                "else is already in place and no code change is needed.");
        }
        else if (carriesSub > 0)
        {
            summary.Add(
                $"SUBSTITUTED PAIR WRITTEN on {carriesSub} of {plans.Count} door(s): " +
                $"'{_settings.NumSubstituted}' / '{_settings.NameSubstituted}' hold the room " +
                "opposite an 'Udvendig' side, so the 'Ext Door * @V05' set can read them and show " +
                "a real room while the 'Door * FROM/TO' set keeps showing 'Udvendig'. Both follow " +
                "a door flip, because both are rewritten on every door change." +
                (carriesSub < plans.Count
                    ? $" {plans.Count - carriesSub} door(s) do not carry the pair and were skipped."
                    : string.Empty));
        }

        if (!_settings.SubstituteExteriorSide)
        {
            summary.Add(
                $"SUBSTITUTION OFF: the '{_settings.ExteriorPrefix}' side of an exterior door was " +
                "written as measured instead of being replaced by the room opposite it. The SCRP " +
                "parameters therefore now READ DIFFERENTLY for exterior doors than they did with " +
                "it on - anything downstream that consumes them (IFC, the FM system, the twin) " +
                "sees the outdoors where it used to see a room. That is the raw measurement and " +
                "it is the right value for a FROM/TO schedule; confirm it is the right value for " +
                "everything else reading these four parameters.");
        }

        if (_settings.PointExtDoorToSubstituted && revealRows.Count > 0)
        {
            var pointed = revealRows.Count(r => r.Count > 3 && r[3] == "REVEALED");
            var supplanted = revealRows.Count(r => r.Count > 3 && r[3] == "HIDDEN");

            summary.Add(
                $"EXT DOOR COLUMNS NOW READ THE SUBSTITUTED PAIR: {pointed} column(s) pointed at " +
                $"'{_settings.NumSubstituted}' / '{_settings.NameSubstituted}' and {supplanted} " +
                "column(s) that were claiming the same headings hidden. Those schedules no longer " +
                $"show '{_settings.ExteriorPrefix}' - they show the room on the other side - while " +
                "the 'Door * FROM/TO' set still shows it, and both follow a door flip. Nothing was " +
                "deleted: the old columns are hidden and can be switched back on.");
        }

        if (_settings.RevealExtDoorRoomColumns && !_settings.PointExtDoorToSubstituted)
        {
            var revealed = revealRows.Count(r => r.Count > 3 && r[3] == "REVEALED");
            var hidden = revealRows.Count(r => r.Count > 3 && r[3] == "HIDDEN");
            var already = revealRows.Count(r => r.Count > 3 && r[3] == "already correct");

            summary.Add(
                $"EXT DOOR ROOM COLUMNS REVEALED: {revealed} SCRP column(s) shown and {hidden} empty " +
                $"calculated column(s) hidden across the 'Ext Door * @V05' set" +
                (already > 0 ? $"; {already} schedule(s) were already correct" : string.Empty) +
                ". The calculated columns were HIDDEN, NOT DELETED - if one of them was meant to " +
                "do something the SCRP value does not, its formula is still there to switch back " +
                "on. Nothing else was touched, and the interior 'Door ... FROM/TO' set was not " +
                "in scope. Turn this setting off now; it is a one-time repair and a second run " +
                "finds every schedule already correct.");
        }

        if (_skipped.Count > 0)
            summary.Add($"Skipped {_skipped.Count}: {string.Join("; ", _skipped.Take(10))}");

        if (failed.Count > 0)
            summary.Add($"Write failures: {string.Join("; ", failed)}");

        if (_warnings.Count > 0)
            summary.Add($"{_warnings.Count} warning(s) -- see the log.");

        if (!apply && plans.Any(p => p.Changed))
            summary.Add("Run again and choose Apply to write these values.");

        if (_settings.RepointSchedules && apply && swapped == 0)
        {
            summary.Add("No columns were re-pointed. Either it was already done on a previous run, or no " +
                        "From/To Room columns were found -- the table in the report lists what was there.");
        }

        return summary;
    }
}
