using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin;

/// <summary>
/// Builds the ribbon tab: ONE panel carrying ONE pull-down, with the six tools inside it.
///
/// WHY A PULL-DOWN RATHER THAN A ROW OF BUTTONS
///   The tab had grown to nine buttons across four panels, which is a menu pretending to be a
///   toolbar - nobody scans nine icons, they hunt for the one they came for. A pull-down puts
///   the names in a list where they can be read, and costs one extra click for tools that are
///   run once per model rather than once per minute.
///
/// THE ONE TRAP IN THIS API, and it is worth stating because it is silent
///   A PulldownButton with an AvailabilityClassName greys out the PARENT, which makes every
///   child unreachable regardless of the child's own availability. Time Tracking must work with
///   no document open - logging a client meeting is exactly the thing you do from the Revit
///   start screen - so the parent below deliberately has NO availability class, and each child
///   carries its own. Setting one on the parent would remove that feature at the moment it is
///   most needed, and nothing would report the loss.
///
/// STACKED vs PULL-DOWN: RibbonPanel.AddStackedItems takes two or three SMALL buttons and
/// stacks them vertically. It is not a parent button and holds at most three items, so it
/// cannot carry six. PulldownButtonData is the right shape here; SplitButtonData is the other
/// option and was not used because it promotes one child to a default action, and none of
/// these six is the obvious default.
/// </summary>
internal static class RibbonBuilder
{
    /// <summary>
    /// Revit resolves a button's command by (assembly path, class name), so it needs the
    /// path of the DLL that is actually running - not a hardcoded string that goes stale
    /// the moment someone moves the deployment folder.
    /// </summary>
    private static readonly string AssemblyPath = typeof(RibbonBuilder).Assembly.Location;

    public static void Build(UIControlledApplication app)
    {
        CreateTab(app, CdaApplication.TabName);

        var panel = app.CreateRibbonPanel(CdaApplication.TabName, "DKSI Tools");

        // NO AVAILABILITY ON THE PARENT. See the class remarks - it would strand Time Tracking.
        var tools = AddPulldown(
            panel,
            name: "CdaTools",
            text: "DKSI\nTools",
            icon: "dksi",
            tooltip: "Door linings, exterior rooms, finish areas, skirting, schedule export and time tracking.",
            longDescription: "Every DKSI tool, grouped. Model tools first, then reporting, then " +
                             "time. Each entry says what it writes before it writes anything.");

        // ---- model ---------------------------------------------------------------

        // BACK ON THE RIBBON, having been retired when OpeningAutomation started running it off
        // the DocumentChanged pipeline. The automation is unchanged and still runs: this is the
        // manual entry point, which is the only way to get a DRY RUN and the only way to run it
        // on demand rather than on a trigger.
        AddPulldownItem(tools,
            name: "CdaResolveLiningClashes",
            text: "Door Lining & Door Material",
            command: typeof(Commands.ResolveLiningClashesCommand),
            tooltip: "Resolves door and window lining clashes and writes Door/Window Material.",
            longDescription: "Whole model by default, or the current selection. Offers a dry run " +
                             "before writing; the write is a single undo step.",
            icon: "lining",
            availability: typeof(ProjectDocumentAvailability));

        // Also back for the same reason as the lining resolver.
        AddPulldownItem(tools,
            name: "CdaResolveUdvendig",
            text: "Door Udvendig",
            command: typeof(Commands.ResolveUdvendigRoomsCommand),
            tooltip: "Replaces 'Udvendig' placeholder rooms with the room on the door's other side.",
            longDescription: "Writes the SCRP from/to parameters so a door schedule can name a real " +
                             "room on both sides instead of an exterior placeholder.",
            icon: "door",
            availability: typeof(ProjectDocumentAvailability));

        AddPulldownItem(tools,
            name: "CdaPlaceSkirting",
            text: "Place Skirting (Wall Sweep)",
            command: typeof(Commands.PlaceSkirtingCommand),
            tooltip: "Places skirting boards as native wall sweeps in every room that is not a wet room.",
            longDescription: "Rooms whose Name or Department contains 'Bad' or 'Toilet' are skipped. " +
                             "Boards break at doors, windows and openings through Revit's own wall " +
                             "sweep behaviour, and are cut where casework stands against them. Safe " +
                             "to re-run: faces that already have a board are left alone.",
            icon: "material",
            availability: typeof(ProjectDocumentAvailability));

        tools.AddSeparator();

        // ---- reporting -----------------------------------------------------------

        // BOUNDARIES ONLY. This used to point at FinishSurfaceAreaCommand, which did the
        // boundary correction and then measured everything, bound seven shared parameters and
        // wrote a CSV. The measurement half is gone from the ribbon at the user's request, so
        // the entry now points at the command that does the boundary work and stops.
        AddPulldownItem(tools,
            name: "CdaAdjustRoomBoundaries",
            text: "Adjust Room Boundaries",
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

        AddPulldownItem(tools,
            name: "CdaExportSchedules",
            text: "Export Schedules",
            command: typeof(Commands.ExportSchedulesCommand),
            tooltip: "Exports every schedule in the model to one Excel workbook, one worksheet each.",
            longDescription: "Read-only. Writes the .xlsx directly - no Excel install and no " +
                             "third-party library required.",
            icon: "excel",
            availability: typeof(ProjectDocumentAvailability));

        tools.AddSeparator();

        // ---- time ----------------------------------------------------------------

        // NO AVAILABILITY CLASS, unlike every other item here, and the reason the PARENT has
        // none either. Logging a client meeting or an hour of coordination is something you do
        // with no model open - often the Revit start screen is exactly where you are when you
        // remember to do it.
        AddPulldownItem(tools,
            name: "CdaTimeTracking",
            text: "Time Tracking",
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
        // REMOVED FROM THE RIBBON in this revision, at the user's request. Every command class
        // is untouched in Commands/ - restoring any of them is one AddPulldownItem call.
        //
        //   Sync Material Parameters  (SyncMaterialParamsCommand)
        //   Set Up Finish Schedules   (SetUpFinishSchedulesCommand)
        //   Finish Surface Area       (FinishSurfaceAreaCommand)
        //   Paint Takeoff by Room     (PaintTakeoffCommand)
        //   Paint Highlight           (PaintHighlightCommand)
        //   Diagnose Parameters       (DiagnoseParamsCommand)
        //
        // NO FINISH OR PAINT QUANTITY CAN NOW BE PRODUCED FROM THE RIBBON. That is the intended
        // result and not an oversight, but it is worth stating plainly, because the three
        // commands involved are the only route to each of these and nothing above replaces them:
        //
        //   FINISH SURFACE AREA is the only thing that measures Wall/Floor/Ceiling Finish Area,
        //   Wall Paint Area and Net Floor Area, writes them to rooms and elements, and exports
        //   the per-room per-material CSV. Adjust Room Boundaries above does the boundary half
        //   of what it used to do and deliberately none of the measuring.
        //
        //   PAINT TAKEOFF is the only thing that builds 'DKSI Paint Takeoff by Room', so that
        //   schedule cannot be regenerated after the model changes.
        //
        //   PAINT HIGHLIGHT is the only way to see WHICH surface a takeoff row measured. The
        //   'Paint Host Id' column still names the element, so Select by ID remains as a manual
        //   substitute.
        //
        // The engines behind all three are untouched in Finishes/ and Schedules/, and
        // FinishAutomation still drives the finish pass off DocumentChanged - so room and
        // element parameters continue to update on their own. What is gone is the manual
        // trigger, the CSV export and the ability to rebuild the takeoff schedule.
        //
        // Sync Material Parameters and Diagnose Parameters lose least: both are setup and
        // diagnostic tools rather than production ones.
        //
        // Also still parked, unchanged by this revision: Stamp Review Date, About, and the SMB
        // Checklist under "Revit Add-in\parked\smb-checklist\".
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
    /// The parent button. Carries the icon the panel shows; the children supply the names.
    /// </summary>
    private static PulldownButton AddPulldown(
        RibbonPanel panel,
        string name,
        string text,
        string icon,
        string tooltip,
        string longDescription)
    {
        var data = new PulldownButtonData(name, text)
        {
            ToolTip = tooltip,
            LongDescription = longDescription,
            LargeImage = Icons.Load(icon + "32"),
            Image = Icons.Load(icon + "16"),
        };

        return (PulldownButton)panel.AddItem(data);
    }

    /// <summary>
    /// One entry in the pull-down.
    ///
    /// Text is single-line here, unlike a panel button: a pull-down renders its children as a
    /// list, so an embedded newline splits the label across two rows of the menu rather than
    /// balancing it under an icon.
    ///
    /// Availability is set on the CHILD, never on the parent - see the class remarks.
    /// </summary>
    private static void AddPulldownItem(
        PulldownButton parent,
        string name,
        string text,
        Type command,
        string tooltip,
        string longDescription,
        string icon,
        Type? availability)
    {
        var data = new PushButtonData(name, text, AssemblyPath, command.FullName)
        {
            ToolTip = tooltip,
            LongDescription = longDescription,
            LargeImage = Icons.Load(icon + "32"),
            Image = Icons.Load(icon + "16"),
        };

        var button = parent.AddPushButton(data);

        // null means "always enabled", including on the Revit start screen.
        if (availability is not null)
            button.AvailabilityClassName = availability.FullName;
    }
}
