using System.IO;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Detects the same add-in installed twice - once per user and once machine-wide - and says so.
///
/// THE FAILURE THIS EXISTS TO MAKE VISIBLE
///   DKSI Revit Tools ships in two packages. The per-user MSI writes to
///   %AppData%\Autodesk\Revit\Addins\2027 and needs no admin; the machine-wide one writes to
///   C:\Program Files\Autodesk\Revit\Addins\2027 and is what IT deploys. Both carry the SAME
///   ClientId, because they are the same add-in.
///
///   Revit does not support reading one add-in from two manifests. Given both, it loads one and
///   ignores the other, and does not say which. So a user who installed it themselves and then
///   received the IT rollout can be running MONTHS-OLD code with a current version sitting on
///   disk beside it, and every symptom of that looks like "the fix did not work".
///
/// WHY THE CHECK LIVES HERE AND NOT IN THE INSTALLER
///   Because the installer cannot do it. A machine-wide MSI deployed by SCCM or Intune runs as
///   SYSTEM, so a launch condition testing %AppData% inspects SYSTEM's profile - which is empty,
///   passes, and tells you nothing about the twenty people who will actually open Revit. The
///   only process that can see the real user's profile is the add-in itself, running in the real
///   user's session. That is here.
///
/// IT WARNS AND CONTINUES. It never deletes anything. Removing the copy Revit did NOT load
/// would do nothing; removing the one it DID load would unload the add-in mid-session. Deleting
/// files out of Program Files also needs rights the user may not have. The user is told what to
/// remove and where, and the log carries the same detail for whoever supports them.
/// </summary>
internal static class DuplicateInstallCheck
{
    private const string ManifestName = "Cda.Revit.Addin.addin";

    /// <summary>
    /// Logs and returns a warning when the manifest is present in more than one add-in folder.
    /// Null when the install is clean, which is the normal case.
    /// </summary>
    /// <param name="revitVersion">Revit's version number, e.g. "2027" - the add-ins folder name.</param>
    public static string? Check(string revitVersion)
    {
        var found = new List<string>();

        foreach (var folder in Folders(revitVersion))
        {
            try
            {
                if (File.Exists(Path.Combine(folder, ManifestName))) found.Add(folder);
            }
            catch
            {
                // An unreadable folder is not evidence of anything. Ignore it rather than
                // report a duplicate that may not exist.
            }
        }

        if (found.Count < 2) return null;

        // Which one Revit actually loaded, rather than guessing: the running assembly's own
        // folder is the only authoritative answer, and it is the thing the user most needs.
        string? loadedFrom = null;
        try
        {
            loadedFrom = Path.GetDirectoryName(typeof(DuplicateInstallCheck).Assembly.Location);
        }
        catch
        {
            // Location is not always available; the warning is still worth showing without it.
        }

        var message =
            "DKSI Revit Tools is installed TWICE on this machine:\n\n" +
            string.Join("\n", found.Select(f => $"    {f}")) +
            "\n\nRevit loads only one of them and does not say which, so a fix can appear " +
            "not to work when it simply is not the copy being run.\n";

        if (!string.IsNullOrEmpty(loadedFrom))
            message += $"\nThis session is running from:\n    {loadedFrom}\n";

        message +=
            "\nKeep the one your office deploys - normally the Program Files copy - and remove " +
            "the other. tools\\Remove-PerUserInstall.ps1 removes the per-user copy, or " +
            "uninstall 'DKSI Revit Tools' from Apps & features.";

        Log.Warn($"Duplicate install: manifest found in {found.Count} folders " +
                 $"({string.Join("; ", found)}); running from '{loadedFrom ?? "unknown"}'.");

        return message;
    }

    /// <summary>
    /// The two folders Revit reads add-in manifests from on 2027, in the order it reads them.
    ///
    /// NOT ProgramData. That was the all-users folder up to Revit 2026; 2027 refuses manifests
    /// found there, silently. Including it here would report a "duplicate" that Revit is
    /// already ignoring, which is noise rather than a warning.
    /// </summary>
    private static IEnumerable<string> Folders(string revitVersion)
    {
        var relative = Path.Combine("Autodesk", "Revit", "Addins", revitVersion);

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relative);

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), relative);
    }
}
