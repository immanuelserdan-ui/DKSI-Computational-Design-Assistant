using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Dimensions;

/// <summary>
/// Inputs for <see cref="RoomDimensionGenerator"/>, kept in one place so the numbers that
/// decide what a drawing looks like are readable without going through the engine.
/// </summary>
public sealed class RoomDimensionSettings
{
    /// <summary>
    /// The dimension type to place with, matched by name.
    ///
    /// NAMED, NOT DEFAULTED. Revit's Create.NewDimension has an overload that takes no type
    /// and uses the document's current default, which is whatever the last person to draw a
    /// dimension happened to leave selected. A production run that silently placed four
    /// hundred dimensions in the wrong style would have to be deleted and re-run, so a
    /// missing type is a hard stop here and the command prints the names that DO exist.
    /// </summary>
    public string DimensionTypeName { get; init; } = RoomDimensionDefaults.DimensionTypeName;

    /// <summary>
    /// Distance from the wall's FINISH FACE to the dimension line, measured INTO the room.
    ///
    /// A MODEL distance, not a paper one: 150 mm is 150 mm at 1:50 and at 1:200, so it is a
    /// fixed standoff from the building rather than a constant gap on the sheet.
    ///
    /// THIS IS NOW THE ONLY SETTING THAT CONTROLS HOW FAR THE TEXT SITS FROM THE WALL. The
    /// text is no longer positioned by a computed clearance of its own - it is reflected
    /// across this line when Revit puts it on the wall side, so it lands wherever the
    /// dimension type's own text offset puts it, on the room side. Every text in the project
    /// therefore sits off its line exactly as any hand-drawn dimension does, and moving this
    /// value moves the text with it.
    /// </summary>
    public double OffsetMillimetres { get; init; } = RoomDimensionDefaults.OffsetMillimetres;

    /// <summary>How far a face's end may sit from its terminating face and still be one corner.</summary>
    public double CornerMatchToleranceMillimetres { get; init; } = RoomDimensionDefaults.CornerMatchToleranceMillimetres;

    public double CornerMatchTolerance => Infrastructure.Measure.FromMillimetres(CornerMatchToleranceMillimetres);

    /// <summary>
    /// Rooms whose Name contains one of these is skipped, case-insensitively.
    ///
    /// 'Udvendig' is the exterior placeholder this office wraps buildings in. It is a placed
    /// room with a real area and it would otherwise be dimensioned like any other - producing
    /// a dimension across the whole site boundary on top of every plan.
    /// </summary>
    public IReadOnlyList<string> ExcludedRoomNames { get; init; } = [RoomDimensionDefaults.ExteriorPlaceholderRoom];

    /// <summary>Rooms smaller than this are skipped - shafts, ducts, leftover slivers.</summary>
    public double MinimumRoomAreaSquareMetres { get; init; } = RoomDimensionDefaults.MinimumRoomAreaSquareMetres;

    /// <summary>
    /// Segments narrower than their own text get the text moved aside with a leader.
    /// Turn off to leave every text where Revit put it.
    /// </summary>
    public bool ArrangeText { get; init; } = true;

    /// <summary>Clear space demanded either side of a dimension text, on paper.</summary>
    public double TextGapMillimetresOnPaper { get; init; } = RoomDimensionDefaults.TextGapMillimetresOnPaper;

    /// <summary>
    /// How far off parallel a face may be and still count as square to a run, in degrees.
    ///
    /// Not zero, because walls drawn by hand are rarely exactly orthogonal and a room that
    /// is 0.02 degrees out would otherwise produce no dimensions at all with nothing on
    /// screen to explain why. Not large either: 15 degrees is comfortably below the 45 that
    /// would let a face be claimed by the wrong axis.
    /// </summary>
    public double SquarenessToleranceDegrees { get; init; } = RoomDimensionDefaults.SquarenessToleranceDegrees;

    /// <summary>
    /// Two faces closer together than this along the run are treated as one.
    ///
    /// Coplanar faces from two different walls are ordinary - a partition meeting a facade
    /// in line with it - and handing Revit both produces a zero-length dimension segment,
    /// which fails the whole string rather than just that segment.
    /// </summary>
    public double CoincidentFaceToleranceMillimetres { get; init; } = RoomDimensionDefaults.CoincidentFaceToleranceMillimetres;

    // ---- derived, in Revit's internal units -----------------------------------

    public double Offset => Infrastructure.Measure.FromMillimetres(OffsetMillimetres);

    public double CoincidentFaceTolerance =>
        Infrastructure.Measure.FromMillimetres(CoincidentFaceToleranceMillimetres);

    /// <summary>cos of the squareness tolerance, so the test is a dot product not an acos.</summary>
    public double SquarenessCosine => Math.Cos(SquarenessToleranceDegrees * Math.PI / 180.0);

    /// <summary>The named type, or null when the project has no such dimension style.</summary>
    public DimensionType? ResolveType(Document doc)
    {
        var linear = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .Where(t => t.StyleType == DimensionStyleType.Linear)
            .ToList();

        // Whitespace- and punctuation-tolerant, for the same reason ParameterHelper is:
        // '_EM white 1.5mm' and '_EM  white 1.5 mm' are the same style to a human and two
        // different strings to Equals, and the office standard is typed by hand.
        var wanted = Infrastructure.ParameterHelper.Squash(DimensionTypeName);

        return linear.FirstOrDefault(t => t.Name == DimensionTypeName)
            ?? linear.FirstOrDefault(t => Infrastructure.ParameterHelper.Squash(t.Name) == wanted);
    }

    /// <summary>Every linear dimension style in the project, for the "not found" message.</summary>
    public static IReadOnlyList<string> LinearTypeNames(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .Where(t => t.StyleType == DimensionStyleType.Linear)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
