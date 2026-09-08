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
