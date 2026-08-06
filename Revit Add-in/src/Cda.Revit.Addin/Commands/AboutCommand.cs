using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Diagnostics. Deliberately does not use <see cref="CommandBase"/>'s document context,
/// because it has to work with no document open.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var asm = typeof(AboutCommand).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString()
                      ?? "unknown";

        var app = commandData.Application.Application;

        var details =
            $"Add-in version:  {version}\n" +
            $"Assembly:        {asm.Location}\n" +
            $"Revit:           {app.VersionName} ({app.VersionNumber}, build {app.VersionBuild})\n" +
            $"Runtime:         {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
            // Note the singular "User" - CurrentUserAddinsLocation, not CurrentUsers...
            // In Revit 2027 AllUsersAddinsLocation points under Program Files, not
            // ProgramData as it did through 2026.
            $"User add-ins:     {app.CurrentUserAddinsLocation}\n" +
            $"All-user add-ins: {app.AllUsersAddinsLocation}\n" +
            $"Log folder:      {Log.Directory}";

        var dialog = new TaskDialog("DKSI Revit Tools")
        {
            MainInstruction = "DKSI Revit Tools",
            MainContent = details,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open log folder");

        if (dialog.Show() == TaskDialogResult.CommandLink1)
        {
            Directory.CreateDirectory(Log.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Log.Directory,
                UseShellExecute = true,
            });
        }

        return Result.Succeeded;
    }
}
