using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Conversion out of Revit's internal units, for display and reporting only.
///
/// WHY THIS EXISTS
///   The codebase carried the conversion factors as literals - 0.3048, 304.8, 0.09290304 -
///   in twenty-two places across six files. Each is correct, and that is the problem: a
///   correct magic number is indistinguishable from a wrong one at the call site, and
///   nothing links '0.09290304' to "square feet to square metres" except the reader
///   recognising it.
///
///   UnitUtils names the intent, is checked by the compiler through ForgeTypeId, and cannot
///   drift. It is also the only correct answer if this ever has to report in a unit system
///   other than the one the constants assume.
///
/// WHAT IS DELIBERATELY NOT HERE
///   Nothing converts on the way IN. Revit stores lengths in decimal feet and areas in
///   square feet internally, and Parameter.Set expects internal units. Every write in this
///   add-in already passes internal values straight through, which is correct - a
///   "helpfully" converted write is the classic way to put a value 3.28x wrong into a model.
/// </summary>
internal static class Measure
{
    /// <summary>Internal length (feet) to millimetres.</summary>
    public static double ToMillimetres(double internalLength) =>
        UnitUtils.ConvertFromInternalUnits(internalLength, UnitTypeId.Millimeters);

    /// <summary>Internal length (feet) to metres.</summary>
    public static double ToMetres(double internalLength) =>
        UnitUtils.ConvertFromInternalUnits(internalLength, UnitTypeId.Meters);

    /// <summary>Internal area (square feet) to square metres.</summary>
    public static double ToSquareMetres(double internalArea) =>
        UnitUtils.ConvertFromInternalUnits(internalArea, UnitTypeId.SquareMeters);

    /// <summary>Millimetres to internal length, for settings expressed in real units.</summary>
    public static double FromMillimetres(double millimetres) =>
        UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);

    /// <summary>
    /// Square metres back to internal area. The inverse of <see cref="ToSquareMetres"/>, for
    /// writing a figure that was reported in m² into an Area parameter - which Revit always
    /// stores in square feet regardless of what the project displays.
    /// </summary>
    public static double FromSquareMetres(double squareMetres) =>
        UnitUtils.ConvertToInternalUnits(squareMetres, UnitTypeId.SquareMeters);
}
