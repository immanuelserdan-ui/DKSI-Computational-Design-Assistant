namespace OmniBIM.Desktop.Models;

/// <summary>One snapshot read from the Revit add-in's LiveStatusServer - see that class for the contract.</summary>
public sealed class LiveStatus
{
    public required bool HasOpenSegment { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public string ProjectNumber { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ViewName { get; init; } = string.Empty;
    public string TaskPhase { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public double ElapsedSeconds { get; init; }
    public bool IsIdle { get; init; }
}
