using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Diagnostics;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.UI;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Read-only diagnostic. Run this first when a sync tool reports "missing", "read-only",
/// or writes nothing at all.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class DiagnoseParamsCommand : CommandBase
{
    // The values the Dynamo graph defaulted to, kept so the tool behaves familiarly.
    private const string DefaultMaterial = "EM Fundament Wall";
    private const string DefaultType = "FV-Ikke Specificeret-300mm";

    protected override string CommandName => "Diagnose Parameters";

    protected override Result Run(CommandContext ctx)
    {
        var (material, type, hint) = Prefill(ctx);

        var window = new DiagnoseParamsWindow(material, type, hint);
        if (RevitWindow.ShowDialog(window, ctx.UiApplication) != true)
            throw new OperationCanceledException();

        var report = new ParameterReport(ctx.Document).Build(window.MaterialName, window.TypeName);

        var path = WriteReport(ctx.Document, report);
        Log.Info($"Diagnostics written to {path}");

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

        return Result.Succeeded;
    }

    /// <summary>
    /// If the user has one element selected, that is almost certainly what they want
    /// diagnosed - so seed the dialog from it instead of making them type the name.
    /// </summary>
    private static (string Material, string Type, string? Hint) Prefill(CommandContext ctx)
    {
        var ids = ctx.UiDocument.Selection.GetElementIds();
        if (ids.Count != 1)
            return (DefaultMaterial, DefaultType, null);

        var element = ctx.Document.GetElement(ids.First());
        if (ctx.Document.GetElement(element.GetTypeId()) is not ElementType elementType)
            return (DefaultMaterial, DefaultType, null);

        string typeName;
        try { typeName = elementType.Name; }
        catch { return (DefaultMaterial, DefaultType, null); }

        // Seed the material box from the thickest layer that has a material assigned -
        // for a wall that is the core, which is the layer people name first.
        var material = string.Empty;
        if (elementType is HostObjAttributes host)
        {
            try
            {
                var layers = host.GetCompoundStructure()?.GetLayers();
                var thickest = layers?
                    .Where(l => l.MaterialId != ElementId.InvalidElementId)
                    .OrderByDescending(l => l.Width)
                    .FirstOrDefault();

                if (thickest is not null &&
                    ctx.Document.GetElement(thickest.MaterialId) is Material m)
                {
                    material = m.Name;
                }
            }
            catch
            {
                // No compound structure. The user can type a name instead.
            }
        }

        return (material, typeName, $"Pre-filled from the selected element (Id {element.Id.Value}).");
    }

    private static string WriteReport(Document doc, string report)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cda", "RevitAddin", "reports");

        Directory.CreateDirectory(folder);

        var safeTitle = string.Concat(
            Path.GetFileNameWithoutExtension(doc.Title)
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        var path = Path.Combine(folder, $"diagnose-params-{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        File.WriteAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }
}
