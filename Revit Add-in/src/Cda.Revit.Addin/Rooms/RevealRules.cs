namespace Cda.Revit.Addin.Rooms;

/// <summary>
/// The conventions in a reveal measurement: which ray means "into the wall", when two rays
/// have settled anything, and when a measured face is believable.
///
/// NO REVIT TYPE IN THIS FILE, DELIBERATELY - same reasoning as <see cref="ClipRules"/>.
/// Casting the rays needs Revit; reading them is three predicates, and all three are sign
/// conventions or thresholds. A reversed sign here does not throw: it builds every reveal
/// board on the wrong side of the wall, which looks deliberate in a view.
/// </summary>
public static class RevealRules
{
    /// <summary>
    /// Whether two ray lengths have actually decided which side the room is on.
    ///
    /// Equal-ish lengths mean the rays settled nothing - the jamb is not on this room's face
    /// at all, which is what happens on a Center-boundary fallback where the curve runs
    /// inside the wall and both directions leave the room at once. Guessing from noise is
    /// how a board ends up through a wall, so the caller falls back rather than pick.
    /// </summary>
    public static bool RaySettles(double forward, double backward, double tolerance)
        => Math.Abs(forward - backward) > tolerance;

    /// <summary>
    /// Whether the reveal runs along +perpendicular, given how far each ray ran INTO the room.
    ///
    /// THE SIGN CONVENTION. A reveal runs into the WALL, which is away from the room - so it
    /// follows the SHORTER ray. Getting this backwards puts every reveal board in open room
    /// space instead of in the opening.
    /// </summary>
    public static bool RevealRunsForward(double forward, double backward)
        => forward <= backward;

    /// <summary>
    /// Whether a measured far face is believable as this wall's far side.
    ///
    /// <paramref name="reach"/> is the distance from the jamb to the measured face, along the
    /// reveal. Behind the jamb is not a reveal at all; improbably deep means the ray found
    /// something the wall is not - a room beyond a cavity, a mis-resolved far room - and the
    /// wall's own Width is the better answer than a confident wrong measurement.
    /// </summary>
    public static bool DepthIsBelievable(
        double reach, double thickness, double minimumRun, double maxFactor)
        => reach >= minimumRun && reach <= thickness * maxFactor;
}
