namespace OmniBIM.Desktop.Models;

/// <summary>
/// The sidebar sections from the OmniBIM mock. Only Home, TimeManagement and ProjectStatus
/// have a real data source wired up (the Revit add-in's CSV ledgers and project-status.json);
/// the rest render as an honest "not wired up yet" placeholder rather than fabricated content.
/// </summary>
public enum AppPage
{
    Home,
    Projects,
    ScanToBim,
    Workflow,
    TimeManagement,
    ProjectStatus,
    TrendNews,
    Teams,
    Reports,
    Settings,
}
