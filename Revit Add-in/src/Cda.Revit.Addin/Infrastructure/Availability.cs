using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Greys the button out unless a project document is open. Without an availability
/// class, buttons stay clickable on the Revit start screen and the command crashes on
/// <c>ActiveUIDocument</c> being null.
/// </summary>
public sealed class ProjectDocumentAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
    {
        var uiDoc = applicationData.ActiveUIDocument;
        return uiDoc?.Document is { IsFamilyDocument: false };
    }
}
