using System.IO;
using System.Reflection;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Which build of this add-in Revit actually has loaded.
///
/// WHY THIS EXISTS
///   Revit locks the DLL while it runs, so the post-build copy into the add-ins folder
///   fails silently and the session keeps running whatever was deployed last time. The
///   build log still says "Deployed to ..." because the copy step is set to warn rather
///   than fail. The result is a fix that compiles, appears to deploy, and does not run -
///   and the only symptom is that the behaviour did not change, which reads as a bad fix
///   rather than a stale one.
///
///   Comparing file timestamps by hand answers it, but only if you think to ask. Putting
///   the stamp in the footer of every dialog means the answer is already on screen at the
///   moment of doubt.
///
/// The timestamp is the assembly FILE's last-write time, not a compile-time constant.
/// Deterministic builds are on, so the PE header timestamp is a content hash rather than a
/// clock reading - it would be stable across rebuilds and useless here. The file's own
/// mtime is what the copy sets, which is exactly the question being asked.
/// </summary>
internal static class BuildInfo
{
    private static string? _cached;

    /// <summary>Where Revit loaded this assembly from. The deployed copy, not the build output.</summary>
    public static string Location
    {
        get
        {
            try { return Assembly.GetExecutingAssembly().Location; }
            catch { return string.Empty; }
        }
    }

    public static DateTime? BuiltAt
    {
        get
        {
            try
            {
                var path = Location;
                return string.IsNullOrEmpty(path) || !File.Exists(path)
                    ? null
                    : File.GetLastWriteTime(path);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>One line for a dialog footer.</summary>
    public static string Describe()
    {
        if (_cached is not null) return _cached;

        var built = BuiltAt;

        _cached = built is null
            ? "Build: unknown"
            : $"Build {built:yyyy-MM-dd HH:mm:ss}";

        return _cached;
    }

    /// <summary>The same, plus where it came from. For the expanded section of a dialog.</summary>
    public static string DescribeFull()
    {
        var path = Location;
        return string.IsNullOrEmpty(path) ? Describe() : $"{Describe()}\n{path}";
    }
}
