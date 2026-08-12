using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Casework;

/// <summary>
/// Everything the side-void cutter is allowed to decide differently, in one place.
///
/// The defaults describe the twelve "Fitting" casework families: a primary void that Revit
/// already carves out of the host wall when the instance is placed, plus side voids that
/// reach sideways into whatever wall the fitting butts up against and which Revit does
/// nothing about on its own.
/// </summary>
public sealed class CaseworkSettings
{
    /// <summary>
    /// A fitting is any casework whose FAMILY or TYPE name contains one of these.
    ///
    /// Both names are tested for the same reason the office's own finder script tests both:
    /// the Project Browser shows Family : Type, and the word "Fitting" lands on either half
    /// depending on who authored the family. "Base Corner Fitting V10" is a family name;
    /// "W ADJ. H 900 D 330" under "No Base Fitting V4" is not. Matching one name only misses
    /// about half of what a human would call a hit.
    /// </summary>
    public string[] NameTerms { get; init; } = ["Fitting"];

    /// <summary>
    /// Drop the name test and treat every casework instance as a candidate.
    ///
    /// Off by default, and the name filter is the cheap half of the whole design: a model
    /// with 2,000 cabinets and 200 fittings does a tenth of the spatial queries. Turn it on
    /// only if a void-carrying family gets named without the word.
    /// </summary>
    public bool AllCasework { get; init; }

    /// <summary>
    /// Where the cutting instances live. Casework as authored, but void cutters get filed
    /// into Generic Model and Specialty Equipment often enough that widening this is a
    /// settings edit rather than a rebuild.
    /// </summary>
    public BuiltInCategory[] FittingCategories { get; init; } = [BuiltInCategory.OST_Casework];

    /// <summary>
    /// What may be cut. Walls only, because that is the manual step being replaced.
    ///
    /// Floors and ceilings are deliberately absent: a side void that clips a slab is
    /// normally a modelling accident rather than an intended cut, and cutting one is a
    /// visible change to a drawing that nobody asked for.
    /// </summary>
    public BuiltInCategory[] TargetCategories { get; init; } = [BuiltInCategory.OST_Walls];

    /// <summary>
    /// How far past the instance's own bounding box to look for walls, in millimetres.
    ///
    /// This is the one number that has to be generous, and it is worth being clear about
    /// why. An instance's bounding box in the project covers its SOLID geometry — voids are
    /// consumed at regeneration and contribute nothing to it. So a side void sticking 60 mm
    /// out of the carcass to reach the return wall is outside the box the model reports, and
    /// a tight net would never offer that wall as a candidate.
    ///
    /// 300 mm covers any side void these families carry with room to spare. Over-reaching
    /// costs nothing but a rejected attempt — see <see cref="CaseworkVoidCutter"/> for why
    /// the attempt itself is the test.
    /// </summary>
    public double ReachMm { get; init; } = 300;

    /// <summary>Reach in Revit's internal units.</summary>
    public double Reach => Measure.FromMillimetres(ReachMm);

    /// <summary>
    /// Ceiling on the walls offered to any one fitting, nearest first.
    ///
    /// A fitting standing in a corner touches two or three walls; a fitting standing in the
    /// middle of a crowded plan can have a dozen inside the net, none of which its voids
    /// reach. The cap bounds the worst case without affecting any real one — twelve is
    /// already four times what a corner unit needs.
    /// </summary>
    public int MaxCandidatesPerInstance { get; init; } = 12;

    /// <summary>
    /// Also offer the fitting's own host wall as a candidate.
    ///
    /// OFF, and it should stay off. A family flagged "Cut with Voids When Loaded" already
    /// cuts its host when it is placed, so the host is either cut (and skipped as already
    /// cut) or deliberately not cut. Re-offering it just spends an attempt per instance.
    /// </summary>
    public bool CutHost { get; init; }

    /// <summary>
    /// Put this in an instance's Comments to have the automation leave it alone entirely.
    /// The same escape hatch the opening automation uses, spelled for this tool.
    /// </summary>
    public string SkipComment { get; init; } = "#nocut-auto";
}
