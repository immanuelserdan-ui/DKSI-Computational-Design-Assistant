namespace OmniBIM.Desktop.Models;

public enum ChatRole
{
    User,
    Assistant,

    /// <summary>Local-only: an error or status line, never sent to the API as history.</summary>
    System,
}

public sealed class ChatMessage
{
    public required ChatRole Role { get; init; }
    public required string Text { get; init; }
}
