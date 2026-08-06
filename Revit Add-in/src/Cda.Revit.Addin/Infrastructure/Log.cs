using System.Globalization;
using System.IO;
using System.Text;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Minimal append-only file log. Revit swallows most add-in output, so a log file on
/// disk is usually the only way to find out what happened on a colleague's machine.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cda", "RevitAddin", "logs");

    public static string CurrentFile =>
        Path.Combine(Directory, $"cda-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>
    /// Turns DEBUG lines on. Off by default because the automation logs one line per
    /// committed transaction, which on a busy model is a lot of writing for nothing.
    ///
    /// Worth turning on for exactly one situation: an edit appears to do nothing and you
    /// need to tell "the event never fired" apart from "the event fired and was filtered
    /// out". Without these lines both look identical — an empty log — and that ambiguity
    /// is expensive.
    /// </summary>
    public static bool Verbose { get; set; }

    public static void Debug(string message)
    {
        if (Verbose) Write("DEBUG", message);
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex is not null)
        {
            sb.AppendLine().Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
            if (ex.StackTrace is not null) sb.AppendLine().Append(ex.StackTrace);
        }
        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                    DateTime.Now, level, message, Environment.NewLine);
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never be the reason a command fails.
        }
    }
}
