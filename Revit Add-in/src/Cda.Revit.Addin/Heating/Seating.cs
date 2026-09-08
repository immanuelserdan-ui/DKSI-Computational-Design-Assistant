namespace Cda.Revit.Addin.Heating;

/// <summary>
/// Every sign convention in the radiator tool, in one place, as arithmetic on plain numbers.
///
/// WHY THIS FILE EXISTS
///   Three bugs shipped from this tool and all three were the same bug: a sign decided by
///   reasoning rather than measurement, scattered across three files where no two could be
///   compared side by side.
///
///     1. Which way the panel faces, taken from FacingOrientation - inverted, put 39 panels
///        outside the building.
///     2. Which side the room is on, taken from Wall.Orientation - a property of how the wall
///        was drawn.
///     3. Whether the panel is in the room, taken from Room.IsPointInRoom - which disagreed
///        with GetRoomAtPoint on the same point and refused 37 of 51 windows.
///
///   Each was a one-line inference buried in a method doing something else. Gathered here they
///   are four functions of at most two lines, they share one definition of the v axis, and -
///   the point of the exercise - they touch no Revit type, so they can be run and asserted
///   against real measured numbers without opening Revit.
///
/// THE FRAME
///   u runs along the wall, v runs out of it, z is up. All three are measured from the wall
///   axis origin, so every element on one wall is measured on one ruler. v has no inherent
///   inside or outside: which end of the wall's own v extent faces the room is
///   <paramref name="roomSide"/>, +1 or -1, and it is established once from the room's
///   boundary and then carried everywhere rather than re-derived.
/// </summary>
internal static class Seating
{
    /// <summary>
    /// Which side of the wall the room is on, from a point known to lie on the room's
    /// boundary.
    ///
    /// A Finish-location boundary segment lies ON the face the room presents, so the question
    /// is only which end of the wall's v extent that point is nearer. Returns null when the
    /// point sits on the centreline, where it carries no information - the caller drops the
    /// face rather than defaulting, because a defaulted side is indistinguishable from a
    /// measured one and sends the panel through the envelope.
    /// </summary>
    public static double? SideFromBoundary(double boundaryV, double wallVLo, double wallVHi,
        double tolerance)
    {
        var centre = (wallVLo + wallVHi) / 2.0;

        if (Math.Abs(boundaryV - centre) <= tolerance) return null;

        return boundaryV > centre ? 1.0 : -1.0;
    }

    /// <summary>Where the room-side face of the wall sits on the v axis.</summary>
    public static double FaceV(double wallVLo, double wallVHi, double roomSide) =>
        roomSide > 0 ? wallVHi : wallVLo;

    /// <summary>The face away from the room.</summary>
    public static double FarV(double wallVLo, double wallVHi, double roomSide) =>
        roomSide > 0 ? wallVLo : wallVHi;

    /// <summary>The wall's centre plane, on the v axis.</summary>
    public static double CentreV(double wallVLo, double wallVHi) => (wallVLo + wallVHi) / 2.0;

    /// <summary>
    /// Reflects a v coordinate through a plane.
    ///
    /// The arithmetic behind the mirror lever, exposed so the lever's central claim can be
    /// asserted rather than argued: that mirroring about the wall's CENTRE plane takes a panel
    /// flush on the far face to flush on the near one, while mirroring about the room-side
    /// FACE - the more obvious choice - leaves it correct-side-but-floating a full wall
    /// thickness out into the room.
    /// </summary>
    public static double MirrorThrough(double v, double plane) => 2.0 * plane - v;

    /// <summary>
    /// How far a body's nearest solid stands out from the room-side face, into the room.
    /// Negative means it is behind that face: inside the wall, or out the far side of it.
    ///
    /// This is the whole seating test. A panel is correctly placed when this is zero or
    /// positive; the 290 mm wall that put the first build's panels on the pavement shows up
    /// here as roughly minus the wall thickness.
    /// </summary>
    public static double Standoff(double vLo, double vHi, double faceV, double roomSide) =>
        roomSide > 0 ? vLo - faceV : faceV - vHi;

    /// <summary>
    /// Re-expresses a v extent as distance out from the face, so an obstruction can be tested
    /// without caring which way round the wall was drawn. Lo is the near edge, Hi the far one.
    /// </summary>
    public static (double Lo, double Hi) OutFromFace(
        double vLo, double vHi, double faceV, double roomSide) =>
        roomSide > 0
            ? (vLo - faceV, vHi - faceV)
            : (faceV - vHi, faceV - vLo);

    /// <summary>
    /// Does something standing at this distance out from the face obstruct a panel of this
    /// depth?
    ///
    /// Two rejections, and they are not symmetric. Something entirely beyond the panel's depth
    /// plus the reach allowance is furniture standing in the room, and a panel can sit behind
    /// it. Something entirely behind the face is in the wall or in the next room, and cannot
    /// touch the panel at all - that second test is what keeps a cupboard in the neighbouring
    /// flat from blocking a wall it is nowhere near.
    ///
    /// TIES GO TO BLOCKING, and the epsilon is why. These distances are differences of
    /// coordinates tens of feet from the project origin, so a blocker sitting exactly on the
    /// limit lands a few parts in 10^15 either side of it depending on rounding. Without the
    /// epsilon that coin toss decides whether an obstruction is seen at all - and the two
    /// outcomes are not equally bad. A blocker wrongly kept costs a shorter panel, named in
    /// the report. A blocker wrongly dropped puts a radiator through a kitchen unit.
    /// </summary>
    public static bool Obstructs(double outLo, double outHi, double depth, double reach)
    {
        const double epsilon = 1e-9;

        return outLo <= depth + reach + epsilon && outHi >= -reach - epsilon;
    }
}
