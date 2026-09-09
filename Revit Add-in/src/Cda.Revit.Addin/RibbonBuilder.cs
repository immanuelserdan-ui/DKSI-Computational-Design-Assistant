using System.IO;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin;

/// <summary>
/// Builds the ribbon tab: every command as its own button, grouped into panels.
///
/// THE PULL-DOWN IS GONE, AND THIS IS A DELIBERATE REVERSAL.
///   ab75b5b collapsed the tab into one "DKSI Tools" pull-down on the argument that nobody
///   scans nine icons. That was reverted at the user's request: these are tools run while
///   working, and one extra click on every single use is a worse tax than a wide tab. The
///   old argument is not wrong about scanning - it is just outweighed by frequency of use.
///
///   What the pull-down did buy was that the names were READ rather than hunted for by icon.
///   Panel buttons get that back through two-line labels, which is why the text below is
///   broken with \n rather than left to Revit to truncate.
///
/// PANELS ARE THE GROUPING NOW, replacing what used to be separators inside the menu. Revit
/// draws a divider between panels and labels each one, so the grouping is more visible here
/// than it was in the pull-down, not less.
///
/// THE AVAILABILITY TRAP THAT THE PULL-DOWN CREATED IS GONE WITH IT. A PulldownButton with an
/// AvailabilityClassName greys out the PARENT and takes every child with it, so the old parent
/// deliberately carried none - otherwise Time Tracking, which must work with no document open,
/// would have been stranded. Now that each command is its own top-level button, each simply
/// carries its own availability and Time Tracking carries none. Nothing to get wrong.
///
/// THREE BUTTONS COME FROM ANOTHER PRODUCT. Painted Surface Area, Painted Area (project wide)
/// and Show / Hide Paint Areas are commands in the standalone Painted Material Takeoff
/// assembly, not in this one. They appear here so there is one tab to learn instead of two,
/// and they are omitted silently on a machine where that product is not installed. See
/// PaintTakeoffPath below for how they are addressed and what happens when it is absent.
/// </summary>
internal static class RibbonBuilder
{
    /// <summary>
    /// Revit resolves a button's command by (assembly path, class name), so it needs the
    /// path of the DLL that is actually running - not a hardcoded string that goes stale
    /// the moment someone moves the deployment folder.
    /// </summary>
    private static readonly string AssemblyPath = typeof(RibbonBuilder).Assembly.Location;

    /// <summary>
    /// The standalone Painted Material Takeoff assembly, or null when it is not installed.
    ///
    /// That "assembly path, class name" pair is why this works at all: a PushButton can name
    /// ANY assembly on disk, not only the one building the ribbon. So the three paint tools
    /// can sit on this tab without their code being copied into this project - which matters,
    /// because there is no PaintTakeoff source tree to copy from.
    ///
    /// NULL IS AN ORDINARY OUTCOME, not an error. Painted Material Takeoff is a separate
    /// product with its own installer; plenty of machines will have DKSI Revit Tools and not
    /// it. The three buttons are then simply absent, and the rest of the tab behaves exactly
    /// as before. Adding buttons that throw "file not found" on click would be worse than a
    /// shorter panel.
    /// </summary>
    private static readonly string? PaintTakeoffPath = FindPaintTakeoff();

    public static void Build(UIControlledApplication app)
    {
        CreateTab(app, CdaApplication.TabName);

        // ---- model ---------------------------------------------------------------

        var model = app.CreateRibbonPanel(CdaApplication.TabName, "Model");

        // BACK ON THE RIBBON, having been retired when OpeningAutomation started running it off
        // the DocumentChanged pipeline. The automation is unchanged and still runs: this is the
        // manual entry point, which is the only way to get a DRY RUN and the only way to run it
        // on demand rather than on a trigger.
        AddButton(model,
            name: "CdaResolveLiningClashes",
            text: "Door Lining\n& Door Material",
            command: typeof(Commands.ResolveLiningClashesCommand),
            tooltip: "Resolves door and window lining clashes and writes Door/Window Material.",
            longDescription: "Whole model by default, or the current selection. Offers a dry run " +
                             "before writing; the write is a single undo step.",
            icon: "lining",
            availability: typeof(ProjectDocumentAvailability));

        // ON THE RIBBON ALONGSIDE THE AUTOMATION, not instead of it. OpeningAutomation runs
        // this same resolver with apply:true on every model change AND on document open, so
        // the button is not what makes Udvendig happen. It is the manual entry point, and it
        // is the only route to three things the automation cannot offer:
        //
        //   * a run on SELECTION - the automation always sweeps every door
        //   * the SCRP BINDING prompt - ScrpBinder is reachable from here and nowhere else.
        //     Without it, a model whose doors lack those four parameters has every door
        //     skipped, reported in the log only, with nothing on screen. That failure ran
        //     unnoticed for days.
        //   * the REPORT - RunUdvendig logs a line and its warnings; only this button writes
        //     the CSV and sidecar log, which is where the per-door table, the schedule field
        //     dump and the classification counts actually live.
        //
        // NOT a dry run. That was removed from the command - the resolver applies unattended on
        // every model change now, so a report-only path from the button would be reporting on
        // work already done. This list said otherwise for a while, which is exactly the kind of
        // stale promise a tooltip should never carry.
        AddButton(model,
            name: "CdaResolveUdvendig",
            text: "Door\nUdvendig",
            command: typeof(Commands.ResolveUdvendigRoomsCommand),
            // DELIBERATELY NO LONGER "replaces 'Udvendig' everywhere". It does that for the
            // 'Ext Door' schedules and deliberately does NOT for the 'Door * FROM/TO' set,
            // where the office wants the placeholder to stand. A tooltip that describes only
            // half of a rule is worse than a vague one, because the half it omits is the half
            // somebody will later report as a bug.
            tooltip: "Records which room is on each side of every door, following the flip control.",
            longDescription: "Writes the office's own SCRP shared parameters on each door. The " +
                             "02/03/04/05 pair holds the sides AS MEASURED, so an exterior door keeps " +
                             "'Udvendig' on the side that faces out - which is what the 'Door * FROM/TO' " +
                             "schedules show. The 06/07 pair holds the SUBSTITUTED room, where an " +
                             "'Udvendig' side is replaced by the room opposite it - which is what the " +
                             "'Ext Door * @V05' schedules show. One door, two answers, both kept current. " +
                             "These parameters are where the values HAVE to live: Revit's built-in " +
                             "From/To Room are derived from geometry and read-only, and they do not " +
                             "follow the door's flip control, so neither the substitution nor a flip " +
                             "can be stored in them. Schedules are NOT modified unless a repair setting " +
                             "in opening-automation.json says so; otherwise add the SCRP parameters as " +
                             "fields wherever you want them. Offers to bind them to Doors when they are " +
                             "present in the model but not bound, which is the difference between this " +
                             "working and silently doing nothing. Runs automatically on every model " +
                             "change and on document open; this button adds a selection-only pass, that " +
                             "binding prompt, and the CSV report.",
            icon: "door",
            availability: typeof(ProjectDocumentAvailability));

        AddButton(model,
            name: "CdaPlaceSkirting",
            text: "Place Skirting\n(Wall Sweep)",
            command: typeof(Commands.PlaceSkirtingCommand),
            tooltip: "Places skirting boards in every room except those excluded by name or Department.",
            longDescription: "Rooms whose Name or Department contains 'Bad' or 'Toilet' are skipped. " +
                             "Boards break at doors, windows and openings through Revit's own wall " +
                             "sweep behaviour, and are cut where casework stands against them. Safe " +
                             "to re-run: faces that already have a board are left alone.",
            icon: "material",
            availability: typeof(ProjectDocumentAvailability));

        AddButton(model,
            name: "CdaPlaceRadiators",
            text: "Place\nRadiators",
            command: typeof(Commands.PlaceRadiatorsCommand),
            tooltip: "Places a radiator under every window, sized and cleared to Danish practice.",
            longDescription: "Whole model, every level. Each panel is centred on its window, held " +
                             "clear of the floor and of the sill so the convection loop works, and " +
                             "sized to the tallest type that fits under the sill and the longest of " +
                             "that height the free wall allows. Doors, columns, casework and " +
                             "radiators already standing are subtracted in three dimensions first, " +
                             "so only what reaches the panel's own height band counts. A window that " +
                             "cannot take a panel - full-height glazing, too low a sill, too full a " +
                             "wall - sends its radiator to the nearest exterior wall in the same room " +
                             "and says why. Safe to re-run: it removes its own previous work and " +
                             "rebuilds, and the whole run is one Ctrl+Z.",
            icon: "material",
            availability: typeof(ProjectDocumentAvailability));

        // Same shape as the two resolvers above: the automation runs this engine off
        // DocumentChanged, and the button is the manual entry point - the only way to get a
        // dry run, and the only way to sweep a model that was drawn before the tool existed.
        AddButton(model,
            name: "CdaCutCaseworkVoids",
            text: "Cut Walls & Floors\nwith Casework Voids",
            command: typeof(Commands.CutCaseworkVoidsCommand),
            tooltip: "Cuts every wall a casework fitting's side voids reach into, and every floor " +
                     "its bottom voids reach down into, replacing the manual Cut Geometry step.",
            longDescription: "Revit cuts a fitting's HOST wall by itself; the side voids that reach " +
                             "into adjacent and intersecting walls, and the bottom voids that reach " +
                             "down into the floor finish and the slab beneath it, are the ones " +
                             "nobody gets for free. Each nearby wall or floor is offered to Revit " +
                             "and only the ones the voids genuinely reach are cut, so a dry run and " +
                             "the real run can never disagree. Never removes a cut. Safe to re-run: " +
                             "elements already cut by that fitting are left alone, and the whole run " +
                             "is one Ctrl+Z.",
            icon: "material",
            availability: typeof(ProjectDocumentAvailability));

        // ---- finishes and paint --------------------------------------------------

        var finishes = app.CreateRibbonPanel(CdaApplication.TabName, "Finishes & Paint");

        // NO STAIR SOFFIT TOOL HERE, and it should not come back in this shape. Building a
        // room-bounding ceiling on a stair's underside was tried twice and abandoned twice.
        // The idea is sound - a stair sits in an opening through the slab, nothing bounds the
        // room below across that opening, so its volume leaks up through the hole - but the
        // instrument is wrong. Reading a reliable soffit plane off a stair is the part that
        // does not hold: run geometry, parent-stair geometry and supports each carry it on a
        // different stair type, and a wrong plane clips the room at a wrong height that looks
        // perfectly plausible in plan.
        //
        // MEASURING a stair underside is a separate, solved thing and stays: see the stair
        // tier in FinishSettings.CeilingFallbackTiers, which carries the access rule.

        // BOUNDARIES ONLY. This used to point at FinishSurfaceAreaCommand, which did the
        // boundary correction and then measured everything, bound seven shared parameters and
        // wrote a CSV. The measurement half is gone from the ribbon at the user's request, so
        // the entry now points at the command that does the boundary work and stops.
        AddButton(finishes,
            name: "CdaAdjustRoomBoundaries",
            text: "Adjust Room\nBoundaries",
            command: typeof(Commands.AdjustRoomBoundariesCommand),
            tooltip: "Adjusts room boundaries against ceilings, slabs and roofs.",
            longDescription: "Enables 'Areas and Volumes' if it is off, so rooms clip against " +
                             "bounding ceilings at all, then raises each room's Upper Offset just " +
                             "past the highest ceiling, slab or roof overlapping it - a sloped " +
                             "ceiling above the limit otherwise leaves the room sliced flat. " +
                             "Raise-only, so a room already bounded correctly is untouched. " +
                             "Binds no parameters, measures no areas and writes no CSV; one " +
                             "Ctrl+Z reverts the whole run.",
            icon: "finish",
            availability: typeof(ProjectDocumentAvailability));

        AddPaintTakeoffButtons(finishes);

        // Sits after the paint tools because it is about their OUTPUT, not about measuring:
        // the views over the rows Painted Surface Area places. Present whether or not that
        // product is installed, because a deleted or hand-edited schedule still needs putting
        // back on a model where the takeoff has already run.
        AddButton(finishes,
            name: "CdaSurfaceSchedules",
            // "Repair" IS THE HONEST WORD NOW. LegacySurfaceScheduleCleanup rebuilds these three
            // automatically on every 'Painted Surface Area' run, so this stopped being a step in
            // the workflow and became the way back from a deleted or hand-broken view. Labelling
            // it as a peer of the takeoff is what made three buttons look like one job.
            text: "Repair Surface\nSchedules",
            command: typeof(Commands.SurfaceSchedulesCommand),
            tooltip: "Fallback. Puts the three surface schedules back if one was deleted or edited " +
                     "by hand - 'Painted Surface Area' already rebuilds them on every run.",
            longDescription: "YOU DO NOT NORMALLY NEED THIS. Running 'Painted Surface Area' " +
                             "rebuilds the wall, floor and ceiling schedules automatically, in the " +
                             "same undo step. Reach for this only when one of them has been deleted " +
                             "or its columns changed by hand and you want it back. \n\nCreates or " +
                             "repairs the three views: adds any column they are missing, sets the " +
                             "Danish headings, and filters each to its own surface group. Measures " +
                             "nothing - a model where the takeoff has never run gets three " +
                             "correctly-shaped empty schedules. Existing columns are never removed. " +
                             "One Ctrl+Z reverts all three.",
            icon: "excel",
            availability: typeof(ProjectDocumentAvailability));

        // Beside the paint tools because it edits their OUTPUT: the room a takeoff row is
        // reported under. It measures nothing and recounts nothing.
        AddButton(finishes,
            name: "CdaReassignPaintRoom",
            text: "Reassign\nPaint Room",
            command: typeof(Commands.ReassignPaintRoomCommand),
            tooltip: "Reports a painted surface under a room other than the one it faces.",
            longDescription: "For the case geometry cannot express: the Danish stair rules put " +
                             "the finish under a run in the room ABOVE, and no room-bounding " +
                             "arrangement says so.\n\n" +
                             "Click a row in a takeoff schedule to select its carrier, run this, " +
                             "and pick the room it should be reported under. The decision is " +
                             "stored in the project and re-applied, because every takeoff run " +
                             "deletes and rebuilds the rows.\n\n" +
                             "No area moves and nothing is recounted. Both rooms and a required " +
                             "reason are recorded, and the original room's own finish parameters " +
                             "keep the geometric figure - so that room's total and the moved row " +
                             "will differ by the amount reassigned.",
            icon: "material",
            availability: typeof(ProjectDocumentAvailability));

        // NO "REGIONS TO PARTS" BUTTON, and it should not come back. Turning Split Face
        // regions into Revit Parts was built and withdrawn: it worked - five walls divided,
        // pieces per layer matching region count - but Parts were the wrong instrument for
        // what was wanted. They are not room-bounding, the paint takeoff reads Walls rather
        // than OST_Parts so no row changed, and it left a second set of elements shadowing
        // every wall it touched.
        //
        // The regions themselves never needed new elements. Face.GetRegions() already returns
        // them as addressable sub-faces, each with its own area, material and Reference - see
        // SplitFaceRegions, which is where region work belongs.

        // ---- views ---------------------------------------------------------------

        var views = app.CreateRibbonPanel(CdaApplication.TabName, "Views");

        // Its own panel rather than a fifth button on Model: this is the only tool here that
        // produces DRAWINGS rather than editing the model, and grouping it with the geometry
        // tools would misfile it for anyone scanning the tab.
        //
        // The icon is 'highlight' because no view icon is embedded. A missing name renders a
        // text-only button, which looks broken next to iconned neighbours - reuse beats blank.
        // ONE button, not two. The plan and 3D passes were separate commands until the 3D
        // camera stopped being inherited from the active view - once the orientation was fixed
        // to Top-Front-Right, nothing forced the user to be standing anywhere in particular,
        // and the grouping work could be done once and shared instead of twice and diverging.
        AddButton(views,
            name: "CdaUnitViews",
            text: "Unit\nViews",
            command: typeof(Commands.CreateUnitViewsCommand),
            tooltip: "Cropped plan and section-boxed 3D view per apartment unit, from the room 'Department'.",
            longDescription: "Groups placed rooms by unit number, then for each unit duplicates the " +
                             "annotated storey plan cropped to that unit, and builds a Top-Front-Right " +
                             "3D view section-boxed to it. Neighbouring units' tags, dimensions and " +
                             "casework are hidden in both. Names follow the document-ID convention, with " +
                             "the project, case and building segments read from the model. Offers a dry " +
                             "run listing every name first, and can do plans only or 3D only; the whole " +
                             "batch is one Ctrl+Z.",
            icon: "highlight",
            availability: typeof(ProjectDocumentAvailability));

        // Beside Unit Views rather than under Model for the same reason that one is here: it
        // produces DRAWING content and touches no geometry. A dimension is annotation, and a
        // user hunting for it under the geometry tools would not find it.
        AddButton(views,
            name: "CdaDimensionRooms",
            text: "Dimension\nRooms",
            command: typeof(Commands.DimensionRoomsCommand),
            tooltip: "Interior wall-to-wall dimensions for every placed room, 50 mm off the wall face.",
            longDescription: "Two chained dimension strings per room - along and across - with a " +
                             "witness line at every bounding face square to the run, so recesses read " +
                             "as separate figures instead of one overall. Placed with the project's " +
                             "own linear dimension style; text that will not fit between its ticks at " +
                             "the view's scale is moved clear with a leader.\n\n" +
                             "Asks which plan views to work in, because a dimension belongs to a " +
                             "drawing and not to the model - then sweeps every placed room on the " +
                             "levels those views cover. Offers a dry run whose counts come from Revit " +
                             "having really placed and rolled back the work. Safe to re-run: its own " +
                             "dimensions are replaced, hand-drawn ones are never touched.",
            icon: "finish",
            availability: typeof(ProjectDocumentAvailability));

        // ---- reporting -----------------------------------------------------------

        var reporting = app.CreateRibbonPanel(CdaApplication.TabName, "Reporting");

        AddButton(reporting,
            name: "CdaExportSchedules",
            text: "Export\nSchedules",
            command: typeof(Commands.ExportSchedulesCommand),
            tooltip: "Pick schedules with the filter dialog, then export them to one Excel " +
                     "workbook, one worksheet each.",
            longDescription: "Opens a picker first: filter by name, category, type, sheet " +
                             "placement or row count, tick what you want, then Export. " +
                             "Everything starts ticked, so exporting the whole model is one " +
                             "extra click.\n\n" +
                             "Read-only. Writes the .xlsx directly - no Excel install and no " +
                             "third-party library required.",
            icon: "excel",
            availability: typeof(ProjectDocumentAvailability));

        // Sits in Reporting rather than beside the takeoff buttons on purpose: it produces a
        // report and changes nothing, which is what everything on this panel has in common.
        AddButton(reporting,
            name: "CdaAuditPaintOverlaps",
            text: "Paint Overlap\nAudit",
            command: typeof(Commands.AuditPaintOverlapsCommand),
            tooltip: "Checks every placed paint takeoff carrier for surfaces claimed twice.",
            longDescription: "Read-only. Intersects the carriers actually in the model - both " +
                             "this add-in's rows and PaintedMaterialTakeoff's - and reports any " +
                             "pair sharing area, which is paint billed twice.\n\n" +
                             "Two carriers on one wall are NOT an error: a wall between two " +
                             "rooms is painted on both faces and each belongs to the room it " +
                             "fronts. Only carriers in the same plane are ever compared.",
            icon: "takeoff",
            availability: typeof(ProjectDocumentAvailability));

        // NO AVAILABILITY CLASS. Log.Verbose is a process-wide switch with no document of its
        // own, so this has to be clickable from the Revit start screen too - the same reason
        // Time Tracking below has none. TransactionMode.ReadOnly because it never touches a
        // document at all, not even to read one.
        AddButton(reporting,
            name: "CdaToggleVerboseLogging",
            text: "Verbose\nLogging",
            command: typeof(Commands.ToggleVerboseLoggingCommand),
            tooltip: "Turns per-phase automation timing on or off. Off by default, every launch.",
            longDescription: "Click to flip Log.Verbose. ON writes per-phase timing for every " +
                             "automatic pass - including the room-finish engine's " +
                             "OccludingElements, CeilingFallback.Resolve, MeasureInteriorSlabs, " +
                             "MeasureInteriorWalls and MeasureReveals calls - to the log file " +
                             "named in the dialog. Reproduce the slow operation, then click " +
                             "again to turn it back OFF.\n\nSession-only: resets to OFF on every " +
                             "Revit launch, so it cannot stay on unnoticed.",
            icon: "highlight",
            availability: null);

        // ---- time ----------------------------------------------------------------

        var time = app.CreateRibbonPanel(CdaApplication.TabName, "Time");

        // NO AVAILABILITY CLASS, unlike every other button here. Logging a client meeting or an
        // hour of coordination is something you do with no model open - often the Revit start
        // screen is exactly where you are when you remember to do it.
        AddButton(time,
            name: "CdaTimeTracking",
            text: "Time\nTracking",
            command: typeof(Commands.TimeTrackingCommand),
            tooltip: "Session time per project and view, manual entries, and the CSV export.",
            longDescription: "Time is recorded automatically from the active document and view; the " +
                             "clock pauses by itself after five minutes without input, or when Revit " +
                             "stops being the front window, and asks what to do with the gap when you " +
                             "come back. Off-model work is added by hand into the same log. Read-only " +
                             "as far as the model is concerned - nothing is written to the .rvt.",
            icon: "timer",
            availability: null);

        // ------------------------------------------------------------------------------
        // STILL OFF THE RIBBON. Three command classes exist in Commands/ and are reachable
        // again with one AddButton call each:
        //
        //   Finish Surface Area  (FinishSurfaceAreaCommand)
        //   Paint Takeoff        (PaintTakeoffCommand)
        //   Paint Highlight      (PaintHighlightCommand)
        //
        // Sync Material Parameters, Set Up Finish Schedules and Diagnose Parameters were listed
        // here too until this audit. THOSE CLASSES NO LONGER EXIST - the note promised a
        // one-line restore for three files that had already been deleted, which is worse than
        // no note at all. If you want them back they have to be written again, not re-wired.
        //
        // THIS ADD-IN'S OWN FINISH AND PAINT COMMANDS STAY OFF, and that is still deliberate.
        // The tab carries the STANDALONE product's three paint tools instead - see
        // AddPaintTakeoffButtons. The two sets are not the same code and not interchangeable,
        // so it is worth being exact about what is and is not reachable:
        //
        //   FINISH SURFACE AREA (this assembly) is the only thing that measures
        //   Wall/Floor/Ceiling Finish Area, Wall Paint Area and Net Floor Area, writes them to
        //   rooms and elements, and exports the per-room per-material CSV. Painted Surface Area
        //   from the other product is a takeoff, not a parameter write, and does NOT stand in
        //   for it. Adjust Room Boundaries above does the boundary half and none of the
        //   measuring.
        //
        //   PAINT TAKEOFF (this assembly) is the only thing that builds 'DKSI Paint Takeoff by
        //   Room', so that schedule still cannot be regenerated after the model changes.
        //
        //   PAINT HIGHLIGHT (this assembly) is the only way to see WHICH surface a DKSI takeoff
        //   row measured. Show / Hide Paint Areas toggles the other product's carrier geometry
        //   and is a different thing entirely. The 'Paint Host Id' column still names the
        //   element, so Select by ID remains as the manual substitute.
        //
        // The engines behind all three are untouched in Finishes/ and Schedules/, and
        // FinishAutomation still drives the finish pass off DocumentChanged - so room and
        // element parameters continue to update on their own. What is gone is the manual
        // trigger, the CSV export and the ability to rebuild the takeoff schedule.
        //
        // Sync Material Parameters and Diagnose Parameters lose least: both are setup and
        // diagnostic tools rather than production ones.
        //
        // Also still parked: Stamp Review Date, About, and the SMB Checklist under
        // "Revit Add-in\parked\smb-checklist\".
    }

    /// <summary>
    /// The three Painted Material Takeoff tools, on the DKSI tab so the two products present
    /// one tab rather than two. Does nothing when that product is absent.
    ///
    /// THE LABELS ARE THE ONES THAT PRODUCT USES, deliberately. Anyone who has been running
    /// its own "Paint Takeoff" panel should find the same three names doing the same three
    /// things, in a new place. Renaming them to fit DKSI's house style would make the move
    /// look like a rewrite.
    ///
    /// The icons are DKSI's own: this assembly can only load PNGs embedded in itself, and the
    /// takeoff product's icons are embedded in the takeoff product.
    ///
    /// THE AVAILABILITY CLASS IS THEIRS, NOT OURS, and it has to be - Revit resolves it out of
    /// the button's own assembly. See the remarks on the string-based AddButton overload for
    /// the dialog that appears when this is got wrong.
    /// </summary>
    private static void AddPaintTakeoffButtons(RibbonPanel panel)
    {
        if (PaintTakeoffPath is null) return;

        // PaintedMaterialTakeoff's equivalent of ProjectDocumentAvailability, living where
        // Revit will actually look for it. Verified against the assembly's metadata: it is
        // public and implements IExternalCommandAvailability.
        const string availability = "PaintedMaterialTakeoff.DocumentAvailability";

        AddButton(panel,
            name: "CdaPaintedSurfaceArea",
            text: "Painted\nSurface Area",
            assemblyPath: PaintTakeoffPath,
            className: "PaintedMaterialTakeoff.Command",
            tooltip: "START HERE. Room-bounded painted areas for walls, floors, ceilings and roofs - " +
                     "and it rebuilds the three surface schedules for you.",
            longDescription: "The room-bounded takeoff, in one click, and the only button most " +
                             "models ever need. Measures each painted face against the room it " +
                             "fronts, so a wall painted on both sides counts towards both rooms. " +
                             "\n\nIT ALSO BUILDS THE SCHEDULES. DKSI watches for this run and " +
                             "rebuilds the three '@V03' surface schedules straight afterwards, in " +
                             "the same undo step - so 'Repair Surface Schedules' is a fallback for " +
                             "a damaged view, not a second step you have to remember. \n\nNeeds " +
                             "rooms placed; if the model has none yet, use 'Paint Area (no rooms)'. " +
                             "From the standalone Painted Material Takeoff product, not from DKSI " +
                             "Revit Tools.",
            icon: "takeoff",
            availabilityClassName: availability);

        // NO DKSI EQUIVALENT, and that is the main reason these buttons point at the other
        // product's assembly rather than being replaced by this one's own paint commands.
        // DKSI's takeoff is room-centric throughout; nothing in it writes a per-element figure
        // that stands on its own when a model has no rooms placed.
        AddButton(panel,
            name: "CdaPaintedAreaProjectWide",
            // TEXT NAMES THE CONDITION, NOT THE SCOPE. "(project wide)" described how it
            // measures and told nobody when to press it, so it read as a bigger version of
            // 'Painted Surface Area' and invited the question of why both exist. The thing that
            // actually separates them is the prerequisite: this one is the ONLY paint tool that
            // works before a room plan exists.
            text: "Paint Area\n(no rooms)",
            assemblyPath: PaintTakeoffPath,
            className: "PaintedMaterialTakeoff.ElementPaintAreaCommand",
            tooltip: "For a model with NO rooms placed. Writes the \"Painted Area\" parameter on " +
                     "each wall, floor, ceiling and roof - per element, not per room.",
            longDescription: "NOT a project-wide version of 'Painted Surface Area' - a different " +
                             "measurement with a different prerequisite. This one is element-centric " +
                             "and needs no rooms, so it is what you reach for on a model with no " +
                             "room plan yet. \n\nIt produces NO room breakdown and places no " +
                             "carriers, so it feeds none of the surface schedules. Once rooms exist, " +
                             "'Painted Surface Area' is the one you want.",
            icon: "finish",
            availabilityClassName: availability);

        AddButton(panel,
            name: "CdaShowHidePaintAreas",
            text: "Show / Hide\nPaint Areas",
            assemblyPath: PaintTakeoffPath,
            className: "PaintedMaterialTakeoff.ToggleCarriersCommand",
            tooltip: "Shows or hides the calculation geometry the takeoff leaves in the view.",
            longDescription: "The takeoff places carrier geometry to hold its results. This toggles " +
                             "that geometry's visibility in the active view - useful for checking " +
                             "what was measured, and for getting it out of the way afterwards.",
            icon: "highlight",
            availabilityClassName: availability);
    }

    /// <summary>
    /// Locates the Painted Material Takeoff assembly, or returns null if it is not installed.
    ///
    /// THREE FOLDERS ARE PROBED, and the order matters.
    ///
    ///   1. Program Files - where the machine-wide MSI puts it, and what Revit 2027 treats as
    ///      the all-users add-in location. First because an IT-managed install should win over
    ///      anything a single user has lying around.
    ///
    ///   2. The user's own add-ins folder, BESIDE THIS ASSEMBLY. The per-user installer ships
    ///      PaintedMaterialTakeoff.dll here so the three paint buttons work on a workstation
    ///      where nobody had administrator rights to install the separate product. Nothing
    ///      about that copy needs a manifest: these buttons name the assembly by path, and
    ///      Revit only needs a manifest to build a product's OWN ribbon.
    ///
    ///   3. ProgramData, last. Revit 2027 moved the all-users folder away from there and
    ///      refuses manifests found in it - which is what PaintTakeoff 1.0.1 shipped against,
    ///      and why its ribbon never appeared. The path stays in the list anyway because THESE
    ///      buttons are not affected by that rule: Revit rejects the stale MANIFEST, while the
    ///      DLL beside it is still perfectly loadable by path. So on a machine still running
    ///      1.0.1 these three buttons work here even though the product's own tab is missing.
    ///
    /// See "installer/PaintTakeoff/README.md" for the whole account of the folder move.
    /// </summary>
    /// <summary>
    /// A takeoff build to use INSTEAD of the installed one, if a file is sitting here.
    ///
    /// WHY THIS EXISTS
    ///   The product installs to Program Files, and Program Files is first in the probe order
    ///   below - correctly, because an IT-managed install should beat anything a single user
    ///   has lying around. The consequence is that testing a rebuilt takeoff requires
    ///   administrator rights to overwrite the installed DLL, on a machine where the person
    ///   doing the testing often does not have them. That is a bad place to be talked into
    ///   shipping an untested assembly.
    ///
    ///   This folder is the way out: drop a build here and the ribbon runs it, no elevation
    ///   and nothing installed touched. Delete the file and the installed product takes over
    ///   again on the next Revit start.
    ///
    /// DELIBERATELY NOT A FOLDER ANYTHING ELSE WRITES TO. It has to be somewhere a copy can
    /// only arrive on purpose, so a stale DLL can never silently outrank the real install.
    /// The path is logged whenever it wins, so "which build am I running" is answerable from
    /// the log rather than by guesswork.
    /// </summary>
    private static string OverridePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "takeoff-override", "PaintedMaterialTakeoff.dll");

    private static string? FindPaintTakeoff()
    {
        const string relative = @"Autodesk\Revit\Addins\2027\PaintedMaterialTakeoff\PaintedMaterialTakeoff.dll";

        string[] candidates =
        [
            OverridePath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), relative),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relative),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), relative),
        ];

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;

            // WARN, not Info, when the override wins. Running a build that is not the
            // installed one is a temporary state by definition, and the way it goes wrong is
            // that somebody forgets - then a quantity is questioned months later and nobody
            // can say which assembly produced it. A warning in the log is the cheapest
            // possible answer to that question.
            if (string.Equals(path, OverridePath, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"Painted Material Takeoff OVERRIDE in use: '{path}'. This is NOT the " +
                         "installed product. Delete that file to go back to the installed build.");
            }
            else
            {
                Log.Info($"Painted Material Takeoff found at '{path}'; adding its three tools to the ribbon.");
            }

            return path;
        }

        Log.Info("Painted Material Takeoff is not installed; its three buttons are omitted from the ribbon.");
        return null;
    }

    private static void CreateTab(UIControlledApplication app, string name)
    {
        try
        {
            app.CreateRibbonTab(name);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // The tab already exists - another add-in of ours created it, or Revit
            // restored it from a previous session. Reusing it is exactly what we want.
            Log.Info($"Ribbon tab '{name}' already exists; reusing it.");
        }
    }

    /// <summary>
    /// One button on a panel, for a command in THIS assembly.
    ///
    /// Text is deliberately two-line on most of these. A panel button balances its label under
    /// the icon and will otherwise truncate a long name to fit, which defeats the whole reason
    /// for leaving the pull-down: the names have to be readable at a glance.
    /// </summary>
    private static void AddButton(
        RibbonPanel panel,
        string name,
        string text,
        Type command,
        string tooltip,
        string longDescription,
        string icon,
        Type? availability)
        => AddButton(panel, name, text, AssemblyPath, command.FullName!,
                     tooltip, longDescription, icon, availability?.FullName);

    /// <summary>
    /// One button on a panel, naming its command by assembly path and class name rather than
    /// by <see cref="Type"/>.
    ///
    /// This is the overload for commands that live in ANOTHER assembly, which cannot be
    /// referenced as a Type from here. It is deliberately the primitive one - the Type-based
    /// overload above delegates to it - so there is a single place where a button is built.
    ///
    /// The class names are STRINGS and nothing checks them at compile time. A typo produces a
    /// button that looks right and fails when clicked, so these names are worth reading twice
    /// against the other product's assembly.
    ///
    /// THE AVAILABILITY CLASS MUST LIVE IN THE SAME ASSEMBLY AS THE COMMAND, and this is the
    /// trap that cost a round trip. Revit resolves AvailabilityClassName out of the button's
    /// OWN assembly - not out of the add-in that built the ribbon - so handing a foreign button
    /// one of this project's availability types produces, at Revit startup:
    ///
    ///     Revit cannot run availability command
    ///     "Cda.Revit.Addin.Infrastructure.ProjectDocumentAvailability"
    ///     ... Could not resolve type ... in assembly 'PaintedMaterialTakeoff'
    ///
    /// which names DKSI as the culprit and offers nothing else. That is why this parameter is a
    /// string rather than a Type: a Type here can only ever come from THIS assembly, so the
    /// signature would quietly invite the exact mistake it cannot express the fix for.
    /// </summary>
    private static void AddButton(
        RibbonPanel panel,
        string name,
        string text,
        string assemblyPath,
        string className,
        string tooltip,
        string longDescription,
        string icon,
        string? availabilityClassName)
    {
        var data = new PushButtonData(name, text, assemblyPath, className)
        {
            ToolTip = tooltip,
            LongDescription = longDescription,
            LargeImage = Icons.Load(icon + "32"),
            Image = Icons.Load(icon + "16"),
        };

        var button = (PushButton)panel.AddItem(data);

        // null means "always enabled", including on the Revit start screen.
        if (availabilityClassName is not null)
            button.AvailabilityClassName = availabilityClassName;
    }
}
