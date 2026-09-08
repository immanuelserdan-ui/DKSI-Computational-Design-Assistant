namespace OmniBIM.Desktop.ViewModels;

/// <summary>
/// Backs a sidebar section that has no live data source yet. Says so plainly rather than
/// showing an empty list that could be mistaken for "there is nothing here" - see the
/// project-status percentage gap noted elsewhere for the same reasoning applied to this app.
/// </summary>
public sealed class PlaceholderPageViewModel(string title, string message)
{
    public string Title { get; } = title;
    public string Message { get; } = message;
}
