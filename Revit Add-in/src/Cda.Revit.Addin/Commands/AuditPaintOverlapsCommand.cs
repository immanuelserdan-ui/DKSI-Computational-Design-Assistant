using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Proves the paint takeoff does not bill the same square metre twice - or names every pair
/// that does.
///
/// WHY A SEPARATE COMMAND RATHER THAN A STEP IN THE TAKEOFF
///   Because the interesting failures are the ones a takeoff run cannot see. An engine
///   validates the records it just produced; this reads what is actually PLACED, which is
///   where stale rows from an earlier run, hand copies, and a second product's carriers all
///   live. Running it after the takeoff would also miss the model somebody hands you.
///
/// TransactionMode.ReadOnly, and that is the safety argument. This command cannot write to
/// the model even if its geometry is wrong, which is the right property for a tool whose only
/// job is to be trusted about paint quantities.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class AuditPaintOverlapsCommand : CommandBase
{
    protected override string CommandName => "Paint Overlap Audit";

    protected override Result Run(CommandContext ctx)
    {
        var doc = ctx.Document;

        var result = new PaintOverlapAudit(doc).Run();

        if (result.Carriers == 0)
        {
            TaskDialog.Show(CommandName,
                "No paint takeoff carriers in this model, so there is nothing to audit.\n\n" +
                "Run Paint Takeoff, or the Painted Material Takeoff, first.");

            return Result.Cancelled;
        }

        var csv = WriteReport(doc, result, out var reportError);

        var lines = new List<string>
        {
            result.Errors == 0
                ? $"No double counting found. {result.Carriers} carrier(s) checked."
                : $"{result.Errors} overlap(s) found, {result.OverlapSqM:0.00} m² counted twice.",
            string.Empty,
            $"{result.Carriers} carrier(s): " + string.Join(", ",
                result.ByProduct.Select(p => $"{p.Value} {Describe(p.Key)}")),
            $"{result.SolidsTested} solid(s) tested geometrically, {result.PairsTested} pair(s) " +
            $"intersected, in {result.Elapsed.TotalSeconds:0.0}s.",
        };

        if (result.Reviews > 0)
            lines.Add($"{result.Reviews} item(s) marked for review rather than as errors.");

        if (result.Errors > 0)
        {
            lines.Add(string.Empty);
            lines.Add("WORST FIRST:");

            foreach (var finding in result.Findings.Where(f => f.Verdict == OverlapVerdict.Error).Take(5))
            {
                lines.Add(
                    $"  [{finding.Check}] {Measure.ToSquareMetres(finding.OverlapSqFt):0.###} m² " +
                    $"- ids {finding.LeftId} and {finding.RightId}");
                lines.Add($"      {finding.Detail}");
            }

            if (result.Errors > 5) lines.Add($"  ... and {result.Errors - 5} more in the report.");
        }

        if (result.Notes.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(result.Notes);
        }

        lines.Add(string.Empty);

        lines.Add(csv is not null
            ? $"Full report: {csv}"
            : $"The report could not be written ({reportError}). The findings above are complete " +
              "for the worst five only.");

        // NOTHING WAS CHANGED, and it is worth saying so on a tool people will run on a model
        // they are mid-way through. There is no transaction here, so there is nothing on the
        // undo stack and nothing to save.
        lines.Add(string.Empty);
        lines.Add("Read-only: nothing in the model was changed.");

        var dialog = new TaskDialog(CommandName)
        {
            MainInstruction = result.Errors == 0
                ? "No double counting found."
                : $"{result.Errors} overlap(s) found.",
            MainContent = string.Join("\n", lines),
            FooterText = $"{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}",
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        // Selecting the worst pair hands off to CarrierRevealService, which already knows how
        // to un-filter a carrier and frame it. Cheaper than telling someone an element id.
        if (result.Errors > 0)
        {
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Select the worst pair",
                "Selects both carriers so you can see the surface they share.");
        }

        var choice = dialog.Show();

        if (choice == TaskDialogResult.CommandLink1)
        {
            var worst = result.Findings.First(f => f.Verdict == OverlapVerdict.Error);

            try
            {
                ctx.UiDocument.Selection.SetElementIds(
                    [new ElementId(worst.LeftId), new ElementId(worst.RightId)]);
            }
            catch (Exception ex)
            {
                Log.Warn($"{CommandName}: could not select {worst.LeftId}/{worst.RightId}: {ex.Message}");
            }
        }

        return Result.Succeeded;
    }

    private static string Describe(CarrierProduct product) => product switch
    {
        CarrierProduct.Dksi => "DKSI",
        CarrierProduct.PaintedMaterialTakeoff => "PaintedMaterialTakeoff",
        _ => "untagged",
    };

    /// <summary>
    /// The CSV is the artefact. A dialog holds five rows; the model owner needs all of them,
    /// with both element ids on every row so each one can be walked back to a surface.
    /// </summary>
    private static string? WriteReport(Document doc, PaintOverlapResult result, out string error)
    {
        error = string.Empty;

        try
        {
            var path = ReportWriter.DefaultPath(doc, "paint-overlap-audit");

            var rows = new List<IReadOnlyList<string>>
            {
                new[]
                {
                    "Verdict", "Check", "Overlap m2", "Estimated",
                    "Left id", "Left room", "Left material", "Left segment", "Left area m2",
                    "Right id", "Right room", "Right material", "Right segment", "Right area m2",
                    "Overlap % of smaller", "Detail",
                },
            };

            foreach (var f in result.Findings)
            {
                var smaller = Math.Min(f.LeftAreaSqFt, f.RightAreaSqFt);

                rows.Add(new[]
                {
                    f.Verdict.ToString(),
                    f.Check,
                    Measure.ToSquareMetres(f.OverlapSqFt).ToString("0.####"),
                    f.AreaEstimated ? "yes" : "no",
                    f.LeftId.ToString(),
                    f.LeftRoom,
                    f.LeftMaterial,
                    f.LeftSegment,
                    Measure.ToSquareMetres(f.LeftAreaSqFt).ToString("0.####"),
                    f.RightId.ToString(),
                    f.RightRoom,
                    f.RightMaterial,
                    f.RightSegment,
                    Measure.ToSquareMetres(f.RightAreaSqFt).ToString("0.####"),
                    smaller > 0 ? (100.0 * f.OverlapSqFt / smaller).ToString("0.#") : string.Empty,
                    f.Detail,
                });
            }

            ReportWriter.WriteCsv(path, rows);

            ReportWriter.WriteSidecarLog(path,
            [
                $"Paint overlap audit - {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Model: {doc.Title}",
                string.Empty,
                $"Carriers found:      {result.Carriers}",
                $"  by product:        " + string.Join(", ",
                    result.ByProduct.Select(p => $"{Describe(p.Key)} {p.Value}")),
                $"Solids tested:       {result.SolidsTested}",
                $"Without geometry:    {result.WithoutGeometry} (checked by key only)",
                $"Pairs intersected:   {result.PairsTested}",
                $"Boolean failures:    {result.BooleanFailures}",
                $"Errors:              {result.Errors}",
                $"Review:              {result.Reviews}",
                $"Double-counted area: {result.OverlapSqM:0.00} m2",
                $"Elapsed:             {result.Elapsed.TotalSeconds:0.0}s",
                string.Empty,
                ..result.Notes,
            ]);

            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"Paint overlap audit: report not written: {ex.Message}");
            return null;
        }
    }
}
