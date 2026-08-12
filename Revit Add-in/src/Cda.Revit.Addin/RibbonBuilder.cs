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

        // Also back for the same reason as the lining resolver.
        AddButton(model,
            name: "CdaResolveUdvendig",
            text: "Door\nUdvendig",
            command: typeof(Commands.ResolveUdvendigRoomsCommand),
            tooltip: "Replaces 'Udvendig' placeholder rooms with the room on the door's other side.",
            longDescription: "Writes the SCRP from/to parameters so a door schedule can name a real " +
                             "room on both sides instead of an exterior placeholder.",
            icon: "door",
            availability: typeof(ProjectDocumentAvailability));

        AddButton(model,
            name: "CdaPlaceSkirting",
            text: "Place Skirting\n(Wall Sweep)",
            command: typeof(Commands.PlaceSkirtingCommand),
            tooltip: "Places skirting boards as native wall sweeps in every room that is not a wet room.",
            longDescription: "Rooms whose Name or Department contains 'Bad' or 'Toilet' are skipped. " +
                             "Boards break at doors, windows and openings through Revit's own wall " +
                             "sweep behaviour, and are cut where casework stands against them. Safe " +
                             "to re-run: faces that already have a board are left alone.",
            icon: "material",
            availability: typeof(ProjectDocumentAvailability));

        // Same shape as the two resolvers above: the automation runs this engine off
        // DocumentChanged, and the button is the manual entry point - the only way to get a
        // dry run, and the only way to sweep a model that was drawn before the tool existed.
        AddButton(model,
            name: "CdaCutCaseworkVoids",
            text: "Cut Walls with\nCasework Voids",
            command: typeof(Commands.CutCaseworkVoidsCommand),
            tooltip: "Cuts every wall a casework fitting's side voids reach into, replacing the manual " +
                     "Cut Geometry step.",
            longDescription: "Revit cuts a fitting's HOST wall by itself; the side voids that reach " +
                             "into adjacent and intersecting walls are the ones nobody gets for free. " +
                             "Each nearby wall is offered to Revit and only the ones the voids " +
                             "genuinely reach are cut, so a dry run and the real run can never " +
                             "disagree. Never removes a cut. Safe to re-run: walls already cut by " +
                             "that fitting are left alone, and the whole run is one Ctrl+Z.",
            icon: "material",
            availability: typeof(ProjectDocumentAvailability));

        // ---- finishes and paint --------------------------------------------------

        var finishes = app.CreateRibbonPanel(CdaApplication.TabName, "Finishes & Paint");

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
            text: "Surface\nSchedules",
            command: typeof(Commands.SurfaceSchedulesCommand),
            tooltip: "Rebuilds the three Surface Area by Room and Face schedules and opens them.",
            longDescription: "Creates or repairs the wall, floor and ceiling takeoff views: adds " +
                             "any column they are missing, sets the Danish headings, and filters " +
                             "each to its own surface group. Measures nothing - these are the " +
                             "views over the rows Painted Surface Area places, so a model where " +
                             "that has never run gets three correctly-shaped empty schedules. " +
                             "Existing columns are never removed. One Ctrl+Z reverts all three.",
            icon: "excel",
            availability: typeof(ProjectDocumentAvailability));

        // ---- views ---------------------------------------------------------------

        var views = app.CreateRibbonPanel(CdaApplication.TabName, "Views");

        // Its own panel rather than a fifth button on Model: this is the only tool here that
        // produces DRAWINGS rather than editing the model, and grouping it with the geometry
        // tools would misfile it for anyone scanning the tab.
        //
        // The icon is 'highlight' because no view icon is embedded. A missing name renders a
        // text-only button, which looks broken next to iconned neighbours - reuse beats blank.
        AddButton(views,
            name: "CdaUnitPlanViews",
            text: "Unit Plan\nViews",
            command: typeof(Commands.CreateUnitPlanViewsCommand),
            tooltip: "One cropped floor plan per apartment unit, grouped by a room parameter.",
            longDescription: "Groups placed rooms by the unit number on 'Department', duplicates the " +
                             "ACTIVE plan for each unit, crops it to that unit's rooms plus a 500 mm " +
                             "margin and names it to the document-ID convention. Open the storey plan " +
                             "you want copied first - its filters, overrides and detailing come with " +
                             "it. Offers a dry run that lists every view name before anything is " +
                             "created; the whole batch is one Ctrl+Z.",
            icon: "highlight",
            availability: typeof(ProjectDocumentAvailability));

        // ---- reporting -----------------------------------------------------------

        var reporting = app.CreateRibbonPanel(CdaApplication.TabName, "Reporting");

        AddButton(reporting,
            name: "CdaExportSchedules",
            text: "Export\nSchedules",
            command: typeof(Commands.ExportSchedulesCommand),
            tooltip: "Exports every schedule in the model to one Excel workbook, one worksheet each.",
            longDescription: "Read-only. Writes the .xlsx directly - no Excel install and no " +
                             "third-party library required.",
            icon: "excel",
            availability: typeof(ProjectDocumentAvailability));

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
        // STILL OFF THE RIBBON. Every command class is untouched in Commands/ - restoring any
        // of them is one AddButton call.
        //
        //   Sync Material Parameters  (SyncMaterialParamsCommand)
        //   Set Up Finish Schedules   (SetUpFinishSchedulesCommand)
        //   Diagnose Parameters       (DiagnoseParamsCommand)
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
            tooltip: "Calculates room-bounded painted surface areas for walls, floors, ceilings and roofs.",
            longDescription: "The room-bounded takeoff, in one click. Measures each painted face " +
                             "against the room it fronts, so a wall painted on both sides counts " +
                             "towards both rooms. From the standalone Painted Material Takeoff " +
                             "product, not from DKSI Revit Tools.",
            icon: "takeoff",
            availabilityClassName: availability);

        // NO DKSI EQUIVALENT, and that is the main reason these buttons point at the other
        // product's assembly rather than being replaced by this one's own paint commands.
        // DKSI's takeoff is room-centric throughout; nothing in it writes a per-element figure
        // that stands on its own when a model has no rooms placed.
        AddButton(panel,
            name: "CdaPaintedAreaProjectWide",
            text: "Painted Area\n(project wide)",
            assemblyPath: PaintTakeoffPath,
            className: "PaintedMaterialTakeoff.ElementPaintAreaCommand",
            tooltip: "Writes the \"Painted Area\" shared parameter on every wall, floor, ceiling and roof.",
            longDescription: "Element-centric and independent of rooms - it needs no rooms placed " +
                             "and reports per element rather than per room, which is what makes it " +
                             "the one to reach for on a model that has no room plan yet.",
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
    private static string? FindPaintTakeoff()
    {
        const string relative = @"Autodesk\Revit\Addins\2027\PaintedMaterialTakeoff\PaintedMaterialTakeoff.dll";

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), relative),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relative),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), relative),
        ];

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;

            Log.Info($"Painted Material Takeoff found at '{path}'; adding its three tools to the ribbon.");
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
