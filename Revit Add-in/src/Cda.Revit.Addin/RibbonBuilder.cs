using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin;

/// <summary>
/// Builds the ribbon tab. Keeping this separate from <see cref="CdaApplication"/> means
/// adding a tool is a two-line change here plus one command class.
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

        var modelPanel = app.CreateRibbonPanel(CdaApplication.TabName, "Model Data");
        var reportPanel = app.CreateRibbonPanel(CdaApplication.TabName, "Reports");
        var timePanel = app.CreateRibbonPanel(CdaApplication.TabName, "Time");
        var helpPanel = app.CreateRibbonPanel(CdaApplication.TabName, "Help");

        // RETIRED FROM THE RIBBON — Stamp Review Date.
        //
        // It was the reference implementation for the write path: a code sample, not a
        // business tool. A hand-stamped issue date is also precisely the metadata that goes
        // stale the first time someone forgets, which is the opposite of what a Digital Twin
        // needs. StampReviewDateCommand stays in Commands/ as the worked example of how a
        // write command is structured; it is simply no longer reachable from the ribbon.

        // RETIRED FROM THE RIBBON — Resolve Lining Clashes and Resolve Udvendig.
        //
        // Both are now driven by OpeningAutomation off the same DocumentChanged pipeline as
        // the finish areas: they run when a door or window changes, when a schedule is
        // opened, and on save. Both engines were always whole-model full recomputes, which
        // is what made them safe to run unattended — there is no accumulated state to get
        // out of step, and a pass over an unchanged model writes nothing.
        //
        // Retiring the buttons is the point rather than a side effect. A correction you have
        // to remember to run is a correction that is wrong between runs, and a Digital Twin
        // that is only true just after someone clicked something is not one.
        //
        // The commands themselves stay in Commands/ and can be put back with one AddButton
        // call each — worth keeping, because they are the only way to get a dry run.

        AddButton(modelPanel,
            name: "CdaSyncMaterialParams",
            text: "Sync Material\nParameters",
            command: typeof(Commands.SyncMaterialParamsCommand),
            tooltip: "Copies material Manufacturer/Comments to FK Kode and FM Bygningsdel, type and instance.",
            longDescription: "Runs across every model category. Offers a preview before writing; " +
                             "the write is a single undo step.",
            icon: "material",
            availability: typeof(ProjectDocumentAvailability));

        // Sits BEFORE Finish Surface Area on the panel because that is the order they are
        // used in: a model whose parameters are not bound produces a schedule full of
        // blanks, which reads as a broken tool rather than an unconfigured model.
        AddButton(modelPanel,
            name: "CdaPlaceSkirting",
            text: "Place\nSkirting",
            command: typeof(Commands.PlaceSkirtingCommand),
            tooltip: "Places skirting boards as native wall sweeps in every room that is not a wet room.",
            longDescription: "Rooms whose Name or Department contains 'Bad' or 'Toilet' are skipped. " +
                             "Boards break at doors, windows and openings through Revit's own wall " +
                             "sweep behaviour, and are cut where casework stands against them. Safe " +
                             "to re-run: faces that already have a board are left alone.",
            icon: "lining",
            availability: typeof(ProjectDocumentAvailability));

        AddButton(reportPanel,
            name: "CdaSetUpFinishSchedules",
            text: "Set Up Finish\nSchedules",
            command: typeof(Commands.SetUpFinishSchedulesCommand),
            tooltip: "Binds the finish parameters and builds the multi-category ceiling takeoff.",
            longDescription: "Run once per model. Binds Wall/Floor/Ceiling Finish Area, Wall Paint " +
                             "Area, Net Floor Area, Ceiling Area Source and Finish Area Stale as " +
                             "shared parameters on fixed GUIDs, with Ceiling Finish Area reaching " +
                             "Floors and Roofs so a ceiling measured off a slab or roof has " +
                             "somewhere to land. Existing parameters are widened, never replaced.",
            icon: "diagnose",
            availability: typeof(ProjectDocumentAvailability));

        AddButton(reportPanel,
            name: "CdaFinishSurfaceArea",
            text: "Finish\nSurface Area",
            command: typeof(Commands.FinishSurfaceAreaCommand),
            tooltip: "Measures room wall/floor/ceiling finish areas from real finish-layer geometry.",
            longDescription: "Writes Wall/Floor/Ceiling Finish Area, Wall Paint Area and Net Floor Area " +
                             "to rooms and elements, and exports a per-room per-material CSV.",
            icon: "finish",
            availability: typeof(ProjectDocumentAvailability));

        // RETIRED FROM THE RIBBON — Auto-Update Finish Areas.
        //
        // It drove the same engine as Finish Surface Area, so once it gained a "run now"
        // action the two buttons did the same thing. Its genuinely useful parts — the
        // automation status and the on/off switch — are now folded into Finish Surface Area,
        // which is the button people already know and the only one that also writes the CSV.

        AddButton(reportPanel,
            name: "CdaExportSchedules",
            text: "Export\nSchedules",
            command: typeof(Commands.ExportSchedulesCommand),
            tooltip: "Exports every schedule in the model to one Excel workbook, one worksheet each.",
            longDescription: "Read-only. Writes the .xlsx directly - no Excel install and no " +
                             "third-party library required.",
            icon: "excel",
            availability: typeof(ProjectDocumentAvailability));

        // NO AVAILABILITY CLASS, unlike every other button here. Logging a client meeting or
        // an hour of coordination is something you do with no model open — often the Revit
        // start screen is exactly where you are when you remember to do it. Greying this out
        // without a document would remove the feature at the moment it is most needed.
        AddButton(timePanel,
            name: "CdaTimeTracking",
            text: "Time\nTracking",
            command: typeof(Commands.TimeTrackingCommand),
            tooltip: "Session time per project and view, manual entries, and the CSV export.",
            longDescription: "Time is recorded automatically from the active document and view; the " +
                             "clock pauses by itself after five minutes without input, or when Revit " +
                             "stops being the front window, and asks what to do with the gap when you " +
                             "come back. Off-model work is added by hand into the same log. Read-only " +
                             "as far as the model is concerned — nothing is written to the .rvt.",
            icon: "timer",
            availability: null);

        // PARKED — SMB Checklist. Built 2026-08-05, removed the same day at the user's
        // request: not the right time to take it on. The source is intact under
        // "Revit Add-in\parked\smb-checklist\"; restoring it is moving four files back into
        // src, re-adding the EmbeddedResource entry to the csproj, and one AddButton call
        // here. Nothing about it was wrong — it was the wrong week for it.

        AddButton(helpPanel,
            name: "CdaDiagnoseParams",
            text: "Diagnose\nParameters",
            command: typeof(Commands.DiagnoseParamsCommand),
            tooltip: "Dumps every parameter Revit exposes on a material, a type, and an instance.",
            longDescription: "Read-only. Run this first when a sync tool reports 'missing', " +
                             "'read-only', or writes nothing. Select an element first to pre-fill the search.",
            // The DKSI mark, freed up by retiring Stamp Review Date. It suits the Help panel
            // in a way it never suited a single command: this is the button someone reaches
            // for when they need to know who to ask, which is what a company mark says.
            icon: "dksi",
            availability: typeof(ProjectDocumentAvailability));

        // AboutCommand is deliberately NOT on the ribbon. What it reported that mattered
        // — the log file location — is already in the footer of every failure dialog,
        // which is when anyone goes looking for it. The class is kept so restoring the
        // button is one AddButton call.
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

    private static void AddButton(
        RibbonPanel panel,
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

        var button = (PushButton)panel.AddItem(data);

        // null means "always enabled", including on the Revit start screen.
        if (availability is not null)
            button.AvailabilityClassName = availability.FullName;
    }
}
