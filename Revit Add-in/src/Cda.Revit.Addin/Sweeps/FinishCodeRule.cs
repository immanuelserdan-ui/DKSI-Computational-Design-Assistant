namespace Cda.Revit.Addin.Sweeps;

/// <summary>
/// Which painted wall finishes take no skirting board. NO REVIT TYPES, DELIBERATELY:
/// compiled straight into Cda.Skirting.Tests.
///
/// THE OFFICE CODES. The last letter of a finish code says what the finish IS: VBF and GBF are
/// tile, VBM is paint, VBP is panel. A tiled wall has no skirting - the tile runs to the floor -
/// so a board is skipped wherever the paint at board height is coded ...F.
///
/// WHY THE NAME COUNTS, NOT ONLY THE CODE PARAMETER. Measured in T01-T05 on 2026-09-23: the
/// finish materials are NAMED '&lt;room&gt;-&lt;code&gt;' - 'Bad-VBF', 'Køkken-VBF', 'Gang-VBM' - and
/// carry no code parameter at all. A rule reading only the parameter would find nothing to
/// skip. So either one qualifies: the Code/Mark/Keynote value, or the token after the name's
/// last hyphen (the whole name when it has none).
///
/// CASE-SENSITIVE ON PURPOSE. Codes are upper case. A name without a hyphen is tested whole, and
/// ordinary words ending in a lower-case 'f' ('Kalkstof') must not read as a tile code.
/// </summary>
internal static class FinishCodeRule
{
    /// <summary>The code carried in a material name: the part after the last hyphen.</summary>
    public static string CodeInName(string? materialName)
    {
        var name = materialName?.Trim() ?? string.Empty;
        var dash = name.LastIndexOf('-');

        return dash >= 0 ? name[(dash + 1)..].Trim() : name;
    }

    /// <param name="codeParameter">The material's Code / Mark / Keynote value, if any.</param>
    /// <param name="suffix">The ending that means "no skirting". Empty switches the rule off.</param>
    public static bool TakesNoSkirting(string? codeParameter, string? materialName, string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return false;

        return EndsWith(codeParameter?.Trim(), suffix) || EndsWith(CodeInName(materialName), suffix);
    }

    private static bool EndsWith(string? code, string suffix) =>
        !string.IsNullOrEmpty(code) && code.EndsWith(suffix, StringComparison.Ordinal);
}
