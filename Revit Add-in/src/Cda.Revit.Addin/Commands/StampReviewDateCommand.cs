using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Reference implementation of a command that writes to the model.
///
/// TransactionMode.Manual is required for any command that opens its own transaction.
/// TransactionMode.Automatic was removed years ago; ReadOnly means Revit will throw the
/// moment you try to start a transaction.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class StampReviewDateCommand : CommandBase
{
    private const string TargetParameter = "CDA Review Date";

    protected override string CommandName => "Stamp Review Date";

    protected override Result Run(CommandContext ctx)
    {
        var projectInfo = ctx.Document.ProjectInformation;

        // LookupParameter matches by display name and returns null when absent. Checking
        // this up front lets us fail with an instruction instead of a NullReferenceException.
        var parameter = projectInfo.LookupParameter(TargetParameter);

        if (parameter is null)
        {
            TaskDialog.Show(CommandName,
                $"This project has no '{TargetParameter}' parameter.\n\n" +
                "Add it as a text project parameter bound to Project Information, then run this again.");
            return Result.Cancelled;
        }

        if (parameter.IsReadOnly)
        {
            TaskDialog.Show(CommandName,
                $"'{TargetParameter}' is read-only in this project and cannot be stamped.");
            return Result.Cancelled;
        }

        if (parameter.StorageType != StorageType.String)
        {
            TaskDialog.Show(CommandName,
                $"'{TargetParameter}' is stored as {parameter.StorageType}; this command expects Text.");
            return Result.Cancelled;
        }

        var stamp = DateTime.Now.ToString("yyyy-MM-dd");

        Transactions.Run(ctx.Document, CommandName, () => parameter.Set(stamp));

        Log.Info($"Stamped '{TargetParameter}' = {stamp}");
        TaskDialog.Show(CommandName, $"{TargetParameter} set to {stamp}.");

        return Result.Succeeded;
    }
}
