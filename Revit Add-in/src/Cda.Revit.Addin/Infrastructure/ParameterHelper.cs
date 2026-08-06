using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Whitespace-tolerant parameter lookup, shared by every tool that writes named
/// parameters.
///
/// The problem it solves: a shared parameter created as "FK Kode " with a trailing space
/// looks identical in the UI, but <see cref="Element.LookupParameter"/> matches the
/// display name exactly and returns null. That single failure accounts for most of the
/// "the script reports missing but I can see the parameter" reports.
/// </summary>
public static class ParameterHelper
{
    /// <summary>Collapses runs of whitespace, trims, and lowercases.</summary>
    public static string Normalize(string? value) =>
        value is null
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>Exact lookup first, then a normalised sweep of every parameter.</summary>
    public static Parameter? Find(Element element, string name)
    {
        try
        {
            var exact = element.LookupParameter(name);
            if (exact is not null) return exact;
        }
        catch
        {
            // Some element types throw rather than returning null. Fall through.
        }

        var target = Normalize(name);

        try
        {
            foreach (var parameter in element.Parameters.Cast<Parameter>())
            {
                try
                {
                    if (Normalize(parameter.Definition.Name) == target) return parameter;
                }
                catch
                {
                    // A parameter with no readable definition cannot be the one we want.
                }
            }
        }
        catch
        {
            // Element.Parameters itself failed - nothing more to try.
        }

        return null;
    }
}
