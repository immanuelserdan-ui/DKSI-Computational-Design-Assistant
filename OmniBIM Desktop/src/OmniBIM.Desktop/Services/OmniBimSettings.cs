using System.IO;
using System.Text.Json;

namespace OmniBIM.Desktop.Services;

/// <summary>
/// OmniBIM's own small settings file - deliberately separate from the Revit add-in's, since
/// this app can run on machines that never install the add-in.
///   %LOCALAPPDATA%\Cda\OmniBIMDesktop\settings.json
/// </summary>
public sealed class OmniBimSettings
{
    /// <summary>Overrides the add-in's own SharedFolder setting when set. See TimeTrackingLocations.</summary>
    public string SharedTimeTrackingFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gemini (Google AI Studio) API key for the chat panel, ONLY as a fallback - this file
    /// lives under %LOCALAPPDATA%, never inside the git repo, so it's a safe place to keep
    /// one, but the GEMINI_API_KEY environment variable is checked first and is the
    /// recommended path. Never set this from source code or a value that came through chat
    /// with an assistant - type it directly into a settings UI, or set the environment
    /// variable instead. Get a free key at https://aistudio.google.com/apikey.
    /// </summary>
    public string GeminiApiKey { get; set; } = string.Empty;

    /// <summary>Model id for the chat panel. See GeminiChatService.</summary>
    public string GeminiModel { get; set; } = "gemini-3.5-flash-lite";

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "OmniBIMDesktop", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static OmniBimSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<OmniBimSettings>(File.ReadAllText(Path)) ?? new OmniBimSettings()
                : new OmniBimSettings();
        }
        catch
        {
            return new OmniBimSettings();
        }
    }

    public void Save()
    {
        var folder = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
    }
}
