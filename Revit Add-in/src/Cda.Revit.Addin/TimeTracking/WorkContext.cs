using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.TimeTracking;

/// <summary>
/// What the user is working on: the model, and the view inside it.
///
/// A RECORD on purpose. Value equality is the whole mechanism that decides when a new
/// segment starts — flipping to a view and straight back is one continuous stretch of work
/// on the same thing, and comparing contexts by value gets that right without any
/// "did anything actually change?" bookkeeping.
/// </summary>
public sealed record WorkContext(
    string ProjectName,
    string ProjectNumber,
    string FileName,
    string ViewName,
    string ViewTemplate,
    string Afdeling = "",
    string ClientNumber = "",
    string Operator = "",
    string Selskab = "")
{
    /// <summary>
    /// Reads the context off a live document and view.
    ///
    /// Everything is defensive: this runs from an event handler during view activation,
    /// where a document can be in the middle of opening, and a failure to read a project
    /// number must not cost the user their tracking.
    /// </summary>
    public static WorkContext From(Document? doc, View? view) =>
        From(doc, view, new TimeTrackingSettings());

    public static WorkContext From(Document? doc, View? view, TimeTrackingSettings settings)
    {
        if (doc is null)
            return new WorkContext("(no document)", string.Empty, string.Empty, string.Empty, string.Empty);

        var fileName = Safe(() => doc.Title);
        var projectName = fileName;
        var projectNumber = string.Empty;
        string afdeling = string.Empty, clientNumber = string.Empty,
               @operator = string.Empty, selskab = string.Empty;

        // ProjectInformation belongs to project documents. A family editor session is still
        // work worth logging, so it is tracked — under the family's own name.
        if (!doc.IsFamilyDocument)
        {
            var info = Safe(() => doc.ProjectInformation?.Name) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(info) && info != "Project Name") projectName = info;

            projectNumber = Safe(() => doc.ProjectInformation?.Number) ?? string.Empty;
            if (projectNumber == "Project Number") projectNumber = string.Empty;

            // Read by NAME, through ParameterHelper, because these are the office's own
            // shared parameters rather than Revit built-ins - and because a definition
            // created as "Selskab " with a trailing space looks identical in the UI and is
            // invisible to LookupParameter.
            afdeling = ProjectText(doc, settings.AfdelingParameter);
            clientNumber = ProjectText(doc, settings.ClientNumberParameter);
            @operator = ProjectText(doc, settings.OperatorParameter);
            selskab = ProjectText(doc, settings.SelskabParameter);
        }

        var viewName = view is null ? string.Empty : Safe(() => view.Name);
        var template = string.Empty;

        if (view is not null)
        {
            template = Safe(() =>
            {
                var id = view.ViewTemplateId;
                return id is null || id == ElementId.InvalidElementId
                    ? string.Empty
                    : doc.GetElement(id)?.Name ?? string.Empty;
            });
        }

        return new WorkContext(
            projectName, projectNumber, fileName, viewName, template ?? string.Empty,
            afdeling, clientNumber, @operator, selskab);
    }

    /// <summary>One Project Information parameter, by name, never throwing.</summary>
    private static string ProjectText(Document doc, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        try
        {
            var info = doc.ProjectInformation;
            if (info is null) return string.Empty;

            var parameter = Infrastructure.ParameterHelper.Find(info, name);
            if (parameter is null) return string.Empty;

            // AsString covers Text; AsValueString covers the rest without guessing at units.
            var value = parameter.StorageType == StorageType.String
                ? parameter.AsString()
                : parameter.AsValueString();

            return value?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>One line for a status bar or a dialog.</summary>
    public string Describe() =>
        string.IsNullOrWhiteSpace(ViewName) ? ProjectName : $"{ProjectName} — {ViewName}";

    private static string Safe(Func<string?> read)
    {
        try { return read() ?? string.Empty; }
        catch { return string.Empty; }
    }
}
