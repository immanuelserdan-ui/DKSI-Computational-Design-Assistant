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

    /// <summary>
    /// Letters and digits only, lowercased. Punctuation and spacing are discarded entirely.
    ///
    /// This is the pass that reconciles "02-SCRP Num fr" with "02 - SCRP Num fr". Whitespace
    /// normalisation alone does not: collapsing runs of spaces leaves the hyphen with spaces
    /// around it in one and not the other, so the two never compare equal and a parameter
    /// sitting in plain sight reports as missing.
    /// </summary>
    public static string Squash(string? value)
    {
        if (value is null) return string.Empty;

        var buffer = new System.Text.StringBuilder(value.Length);

        foreach (var c in value)
            if (char.IsLetterOrDigit(c)) buffer.Append(char.ToLowerInvariant(c));

        return buffer.ToString();
    }

    /// <summary>
    /// Exact lookup, then a whitespace-normalised sweep, then a punctuation-insensitive one.
    ///
    /// THE THIRD PASS RUNS LAST AND ONLY ON FAILURE, deliberately. It is the most permissive
    /// and therefore the most capable of matching the wrong thing, so it never gets to answer
    /// a question either stricter pass could. For names as distinctive as the SCRP set there
    /// is nothing else in a door's parameters it could collide with.
    /// </summary>
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

        var normalized = Normalize(name);
        var squashed = Squash(name);

        Parameter? loose = null;

        try
        {
            foreach (var parameter in element.Parameters.Cast<Parameter>())
            {
                try
                {
                    var actual = parameter.Definition.Name;

                    if (Normalize(actual) == normalized) return parameter;
                    if (loose is null && Squash(actual) == squashed) loose = parameter;
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

        return loose;
    }

    /// <summary>
    /// Every parameter name on an element whose squashed form contains <paramref name="fragment"/>.
    ///
    /// Purely diagnostic. When a tool cannot find the parameters it needs, the useful thing to
    /// report is not "missing" but the names that ARE there and look close - which turns
    /// "the command does nothing" into an answer without another round trip.
    /// </summary>
    public static IReadOnlyList<string> NamesContaining(Element element, string fragment)
    {
        var needle = Squash(fragment);
        var found = new List<string>();

        if (needle.Length == 0) return found;

        try
        {
            foreach (var parameter in element.Parameters.Cast<Parameter>())
            {
                try
                {
                    var actual = parameter.Definition.Name;
                    if (Squash(actual).Contains(needle)) found.Add(actual);
                }
                catch
                {
                    // Unreadable definition - nothing to report for it.
                }
            }
        }
        catch
        {
            // Element.Parameters itself failed.
        }

        return found;
    }
}
