using System.Globalization;
using System.IO;
using System.Text;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Minimal append-only file log. Revit swallows most add-in output, so a log file on
/// disk is usually the only way to find out what happened on a colleague's machine.
///
/// KEEPS ONE FILE HANDLE OPEN FOR THE WHOLE DAY, rather than opening, writing and closing
/// it on every single line the way the original File.AppendAllText version did.
///
/// MEASURED CAUSE OF "the add-in lags badly for some users, and stops the instant it is
/// uninstalled": every DocumentChanged the automation reacts to logged at least one line
/// (VerboseLogging defaults on), and every one of those lines paid for a
/// Directory.CreateDirectory existence check plus a fresh File.Open/Write/Close. On a
/// machine where %LocalAppData% is redirected to a network profile, synced by OneDrive, or
/// scanned on every file-open by real-time antivirus/EDR - all common on managed AEC
/// workstations - each of those round trips can cost single-digit-to-tens of milliseconds,
/// and during active modelling this fires dozens of times a minute. A developer's own
/// machine, with a fast local disk and the add-in's own log folder excluded from AV
/// scanning, never sees it - which is exactly the "fine for me, unusable for everyone else"
/// pattern reported.
///
/// The fix keeps the exact durability guarantee File.AppendAllText gave: AutoFlush pushes
/// every line to the OS immediately, so a Revit crash mid-session still loses nothing
/// already logged. What changes is that the CreateDirectory check now runs once per
/// process instead of once per line, and the file handle is opened once per day instead of
/// once per line - FileShare.ReadWrite keeps it open for tailing (Notepad, Get-Content
/// -Wait) exactly as before, since a closed-after-every-write file was never required for
/// that; readers just open with FileShare.ReadWrite of their own.
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

    private static bool _directoryEnsured;
    private static StreamWriter? _writer;
    private static string? _writerDate;

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
                var writer = WriterForToday();
                writer.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}",
                    DateTime.Now, level, message));
            }
        }
        catch
        {
            // Logging must never be the reason a command fails.
            //
            // A writer left in a bad state (the file locked out from under us, the disk
            // briefly unavailable) must not keep failing forever - drop it so the NEXT call
            // reopens fresh instead of the rest of the session silently losing every line to
            // the same exception this one just swallowed.
            try { _writer?.Dispose(); } catch { /* already broken; nothing to save */ }
            _writer = null;
        }
    }

    /// <summary>
    /// The open handle for today's file, creating or rotating it as needed. Callers already
    /// hold <see cref="Gate"/>.
    /// </summary>
    private static StreamWriter WriterForToday()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (_writer is not null && _writerDate == today) return _writer;

        _writer?.Dispose();

        // ONCE PER PROCESS, NOT ONCE PER LINE. The directory does not get deleted out from
        // under a running session in any case worth guarding against, so re-checking it on
        // every single log call - the original behaviour - bought nothing but the cost of
        // asking.
        if (!_directoryEnsured)
        {
            System.IO.Directory.CreateDirectory(Directory);
            _directoryEnsured = true;
        }

        // FileShare.ReadWrite, same reason the file was ever readable while Revit holds it
        // open before this change: a colleague or the developer tailing the log with Notepad
        // or `Get-Content -Wait` opens their own read handle independently of this one.
        var stream = new FileStream(
            Path.Combine(Directory, $"cda-{today}.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        _writerDate = today;
        return _writer;
    }

    /// <summary>
    /// Releases the open file handle. Called once from <c>OnShutdown</c>. Not required for
    /// correctness - AutoFlush already means nothing buffered is ever lost, and the OS closes
    /// the handle at process exit regardless - but it is cheap and leaves nothing open longer
    /// than the add-in is actually running.
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _writer?.Dispose(); } catch { /* best effort */ }
            _writer = null;
            _writerDate = null;
        }
    }
}
