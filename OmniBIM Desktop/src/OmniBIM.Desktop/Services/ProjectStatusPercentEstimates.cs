namespace OmniBIM.Desktop.Services;

/// <summary>
/// A fixed status-to-percentage lookup, ONLY because the dashboard mock calls for a progress
/// bar and project-status.json has no percentage field to show one from - see
/// Models.ProjectStatus's remarks. These numbers are not measured from anything: they are a
/// rough ordering of the nine status values by how far through the pipeline they typically
/// sit. Every place this is used must label it "Est." - never presented as a real completion
/// measurement, because it is not one.
/// </summary>
public static class ProjectStatusPercentEstimates
{
    private static readonly Dictionary<string, int> Values = new()
    {
        ["Pending"] = 5,
        ["On-going"] = 45,
        ["For Classification"] = 60,
        ["EM done"] = 65,
        ["Ready for DDG"] = 75,
        ["For QA DDG"] = 85,
        ["For QA EM"] = 88,
        ["On-hold"] = 40,
        ["Done"] = 100,
    };

    /// <summary>25 for an unrecognised or empty status - a hand-edited value should not crash this, just look unremarkable.</summary>
    public static int For(string status) => Values.GetValueOrDefault(status, 25);
}
