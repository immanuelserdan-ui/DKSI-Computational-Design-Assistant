using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Everything a command normally needs, resolved once so individual commands do not
/// repeat the same four lines of unwrapping.
/// </summary>
public sealed class CommandContext
{
    public CommandContext(ExternalCommandData data)
    {
        UiApplication = data.Application;
        UiDocument = data.Application.ActiveUIDocument;
        Document = UiDocument.Document;
    }

    public UIApplication UiApplication { get; }
    public UIDocument UiDocument { get; }
    public Document Document { get; }
}

/// <summary>
/// Base class for every command in this add-in.
///
/// An unhandled exception thrown out of <see cref="IExternalCommand.Execute"/> reaches
/// Revit's own error handler, which shows a generic crash dialog and can leave the
/// document in a bad state. Catching here means users get a readable message, the
/// stack trace lands in the log, and Revit stays healthy.
/// </summary>
public abstract class CommandBase : IExternalCommand
{
    /// <summary>Title used for user-facing dialogs.</summary>
    protected abstract string CommandName { get; }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            Log.Info($"{CommandName}: started");
            var result = Run(new CommandContext(commandData));
            Log.Info($"{CommandName}: finished with {result}");
            return result;
        }
        catch (OperationCanceledException)
        {
            // User backed out of a picker or a file dialog. Not an error.
            Log.Info($"{CommandName}: cancelled by user");
            return Result.Cancelled;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            Log.Info($"{CommandName}: cancelled by user");
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error($"{CommandName}: failed", ex);
            message = ex.Message;

            var dialog = new TaskDialog(CommandName)
            {
                MainInstruction = "The command could not complete.",
                MainContent = ex.Message,
                ExpandedContent = ex.ToString(),
                FooterText = $"Details written to {Log.CurrentFile}",
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dialog.Show();

            return Result.Failed;
        }
    }

    /// <summary>Command body. Throw freely; the base class turns it into a clean dialog.</summary>
    protected abstract Result Run(CommandContext ctx);
}
