namespace Cda.Revit.Addin.Dimensions;

/// <summary>
/// The numbers this tool ships with, in millimetres, with NO REVIT TYPE ANYWHERE.
///
/// WHY THESE LEFT RoomDimensionSettings
///   Twice now a stated requirement has been implemented in the formula and missed in the
///   value - the dimension offset stayed at 50 after being changed to 100, and the whole
///   suite went on passing 81/81, because every assertion took the offset as a PARAMETER and
///   so tested the arithmetic rather than the drawing. A tool whose tests cannot see the
///   numbers it actually uses is a tool whose tests cannot fail for the reason that matters.
///
///   RoomDimensionSettings touches Document and FilteredElementCollector, so it cannot be
///   compiled into the Revit-free harness. These constants can, and are - the tests assert on
///   THIS file, and RoomDimensionSettings reads its defaults from it, so the shipped value and
///   the asserted value are the same symbol and cannot drift apart.
///
/// CHANGING A NUMBER HERE WILL FAIL A TEST. That is the point: the test names the office
/// standard, so a change to the standard is a deliberate edit in two places rather than a
/// silent edit in one.
/// </summary>
internal static class RoomDimensionDefaults
{
    /// <summary>Dimension LINE, off the wall's finish face, into the room.</summary>
    public const double OffsetMillimetres = 150.0;

    /// <summary>Clear space demanded either side of a text, on paper.</summary>
    public const double TextGapMillimetresOnPaper = 1.0;

    /// <summary>How far off parallel a face may be and still count as square to a run.</summary>
    public const double SquarenessToleranceDegrees = 15.0;

    /// <summary>Two faces closer than this along a run are treated as one.</summary>
    public const double CoincidentFaceToleranceMillimetres = 2.0;

    /// <summary>Rooms smaller than this are skipped.</summary>
    public const double MinimumRoomAreaSquareMetres = 1.0;

    /// <summary>
    /// How far a wall face's end may sit from the face that terminates it and still be called
    /// the same corner. Finish-face geometry meets at a corner exactly in theory and within a
    /// few millimetres in a real model.
    /// </summary>
    public const double CornerMatchToleranceMillimetres = 25.0;

    /// <summary>The office's linear dimension style.</summary>
    public const string DimensionTypeName = "_EM white 1.5mm";

    /// <summary>The exterior placeholder room, which must never be dimensioned.</summary>
    public const string ExteriorPlaceholderRoom = "Udvendig";
}
