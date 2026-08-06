using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Qa;
using Cda.Revit.Addin.UI;

namespace Cda.Revit.Addin.Commands;

/// <summary>
/// Opens the QA checklist.
///
/// WHY THIS COMMAND IS FOUR LINES
///   Every other command in this add-in does its work inside <c>Run</c> and returns when the
///   work is done. This one cannot: the window it opens is MODELESS and outlives the command
///   by design, so that you can look at the model while the results are still on screen.
///
///   What <c>Run</c> is actually for here is the one thing that can only happen in a valid
///   API context - creating the ExternalEvent the window will use for everything afterwards.
///   <see cref="QaToolsWindow.Show"/> does that first, then returns immediately.
///
/// TRANSACTION MODE
///   Manual, even though the command itself writes nothing. Temporary isolation in the finish
///   check is a document modification, and it opens its transaction from inside the external
///   event rather than from here - but the declaration has to be Manual either way, because
///   ReadOnly would forbid it.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class QaToolsCommand : CommandBase
{
    protected override string CommandName => "QA Tools";

    protected override Result Run(CommandContext ctx)
    {
        QaToolsWindow.Show(ctx.UiApplication, new QaSettings());

        Log.Info($"{CommandName}: checklist opened for {ctx.Document.Title}");

        // Succeeded, not a wait. The window is now driving itself through the external event
        // queue; Revit is free, which is the entire point of the check being modeless.
        return Result.Succeeded;
    }
}
