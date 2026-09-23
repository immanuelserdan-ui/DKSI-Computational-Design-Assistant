using Cda.Revit.Addin.Sweeps;

namespace Cda.Skirting.Tests;

/// <summary>
/// Runnable checks over <see cref="FinishCodeRule"/> - which painted finishes take no skirting.
///
/// The material names are the real ones from T01-T05 ('Bad-VBF', 'Gang-VBM', ...), which carry
/// their code at the end of the name and no code parameter. A failure here is either a tiled
/// wall with a board glued to its tile, or a painted wall silently left without one.
/// </summary>
internal static class Program
{
    private static int _run;
    private static int _failed;

    private const string F = "F";

    private static int Main()
    {
        Console.WriteLine("Skirting: finishes that take no board\n");

        Section("Tile codes in material names, as the model has them");
        Check("'Bad-VBF' is tile", Skip(null, "Bad-VBF"));
        Check("'Køkken-VBF' is tile", Skip(null, "Køkken-VBF"));
        Check("'Bad-GBF' is tile", Skip(null, "Bad-GBF"));
        Check("'Gang-VBM' (paint) is not", !Skip(null, "Gang-VBM"));
        Check("'Bad-VBP' (panel) is not", !Skip(null, "Bad-VBP"));
        Check("a bare code name 'VBF' is tile", Skip(null, "VBF"));
        Check("spaces round the hyphen are tolerated", Skip(null, "Bad - VBF "));

        Section("The code parameter counts too");
        Check("Code 'VBF' on a plainly named material", Skip("VBF", "Vægflise hvid"));
        Check("Code 'VBM' does not", !Skip("VBM", "Maling hvid"));
        Check("either one is enough: unrelated Keynote, tile in the name", Skip("4.2.1", "Bad-VBF"));

        Section("Things that must not read as tile");
        Check("lower-case 'f' at the end of a word", !Skip(null, "Kalkstof"));
        Check("code token only, not the room part: 'Stuef-VBM'", !Skip(null, "Stuef-VBM"));
        Check("no name, no code", !Skip(null, null));
        Check("empty suffix switches the rule off", !FinishCodeRule.TakesNoSkirting("VBF", "Bad-VBF", ""));

        Section("Reading the code out of a name");
        Check("after the LAST hyphen", FinishCodeRule.CodeInName("Bad-Nord-VBF") == "VBF");
        Check("whole name when there is no hyphen", FinishCodeRule.CodeInName("VBF") == "VBF");

        Console.WriteLine($"\n{_run - _failed}/{_run} passed.");
        return _failed == 0 ? 0 : 1;
    }

    private static bool Skip(string? code, string? name) => FinishCodeRule.TakesNoSkirting(code, name, F);

    private static void Section(string title) => Console.WriteLine($"\n{title}");

    private static void Check(string what, bool passed)
    {
        _run++;

        if (passed)
        {
            Console.WriteLine($"    ok   {what}");
            return;
        }

        _failed++;
        Console.WriteLine($"    FAIL {what}");
    }
}
