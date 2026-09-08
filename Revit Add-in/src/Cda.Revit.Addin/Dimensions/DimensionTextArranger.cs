using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Dimensions;

/// <summary>
/// Puts every dimension text inside the room, clear of the wall it measures from, and moves
/// it further where it still will not fit or would print on top of a neighbour.
///
/// WHY EVERY TEXT IS REPOSITIONED AND NOT JUST THE CRAMPED ONES
///   Revit hangs dimension text to ONE side of its line - the left of the line's direction -
///   and nothing in the API asks which side you wanted. A string that hugs a wall has open
///   room on one side and brickwork on the other, and Revit picks by the direction the line
///   happens to run. For these runs it picks the wall, every time: the along-the-room string
///   runs +u and the wall it hugs is to the left of that, and the across-the-room string runs
///   +v with its wall left of that too. Both put the number on the masonry.
///
///   That is not a collision to be nudged out of; it is the default being wrong on the very
///   first placement. So the position is COMPUTED from the wall face outward - the room's
///   inward vector comes with each dimension from the generator - and the band search below
///   starts from that corrected position rather than from Revit's.
///
/// WHAT THE API DOES NOT GIVE YOU, AND WHAT THIS DOES INSTEAD
///   Revit exposes no text extents. There is no "how wide will '1950' print" anywhere in the
///   API, and Dimension.get_BoundingBox returns the extent of the whole dimension including
///   its witness lines - useless for deciding whether one segment's number fits between two
///   ticks. So the width here is ESTIMATED from the type's text size and width factor, and
///   the estimate is deliberately pessimistic: a digit is treated as 0.6 of the text height,
///   which is wider than most dimension fonts actually set them.
///
///   Being pessimistic is the right way round to be wrong. A too-generous estimate moves a
///   number that would have fitted, and someone drags it back in ten seconds. A too-tight one
///   leaves overlapping text on a drawing that has already been issued.
///
/// PAPER, NOT MODEL. Everything here is a paper measurement scaled up by the view scale.
/// Whether '1950' fits between two ticks 300 mm apart is a question about millimetres on a
/// sheet, and the same room is legible at 1:50 and unreadable at 1:200.
/// </summary>
internal static class DimensionTextArranger
{
    /// <summary>
    /// Give up after this many nudges and leave the text where Revit put it. A number that
    /// has been pushed six text heights off its own dimension line is no longer obviously
    /// attached to it, and an overlap a human can see and fix beats a leader into open space.
    /// </summary>
    private const int MaximumBands = 6;

    /// <summary>
    /// Tidies the given dimensions. THE DOCUMENT MUST HAVE BEEN REGENERATED since they were
    /// created - TextPosition on a dimension Revit has not yet evaluated throws, and the
    /// value it reports before regeneration is not the one that will print.
    /// Caller owns the transaction. Returns how many texts were moved.
    /// </summary>
    public static int Arrange(
        Document doc,
        IReadOnlyList<RoomDimensionGenerator.PlacedDimension> placed,
        RoomDimensionSettings settings)
    {
        var moved = 0;

        foreach (var group in placed
                     .Where(p => p.Dimension.IsValidObject)
                     .GroupBy(p => p.Dimension.OwnerViewId))
        {
            if (doc.GetElement(group.Key) is not View view) continue;

            // Occupied rectangles for THIS view only. A dimension in one view cannot collide
            // with one in another, and carrying the boxes across views would push text apart
            // to avoid a clash that does not exist.
            var occupied = new List<DimensionMath.Box>();

            foreach (var one in group)
                moved += ArrangeOne(doc, view, one, settings, occupied);
        }

        return moved;
    }

    private static int ArrangeOne(
        Document doc, View view, RoomDimensionGenerator.PlacedDimension placed,
        RoomDimensionSettings settings, List<DimensionMath.Box> occupied)
    {
        var dimension = placed.Dimension;
        if (doc.GetElement(dimension.GetTypeId()) is not DimensionType type) return 0;

        var scale = Math.Max(view.Scale, 1);

        // Text size is stored as it prints on paper. Multiplying by the view scale is what
        // turns "1.5 mm on a sheet" into the model distance the text actually covers.
        var textHeightOnPaper = ReadLength(type, BuiltInParameter.TEXT_SIZE, fallbackMillimetres: 2.5);
        var textHeight = textHeightOnPaper * scale;
        var widthFactor = ReadNumber(type, BuiltInParameter.TEXT_WIDTH_SCALE, fallback: 1.0);
        var gap = Measure.FromMillimetres(settings.TextGapMillimetresOnPaper) * scale;

        var direction = (dimension.Curve as Line)?.Direction;
        if (direction is null || direction.IsVertical()) return 0;

        var along = direction.ToPlan().Normalized();

        // ACROSS IS THE ROOM'S INWARD VECTOR, not left-of-along. That substitution is the
        // whole fix: left-of-along is where Revit put the text, and it points at the wall.
        var across = placed.Placement.Inward;

        // The dimension LINE's own coordinate along inward - the mirror that a text on the
        // wall side is reflected across.
        var lineAlongInward = placed.Placement.WallAlongInward + settings.Offset;

        var right = view.RightDirection.ToPlan().Normalized();
        var up = view.UpDirection.ToPlan().Normalized();

        var moved = 0;

        foreach (var segment in Segments(dimension))
        {
            var text = segment.ValueString;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var width = DimensionMath.TextWidth(text.Length, textHeightOnPaper, widthFactor, scale);

            XYZ current;
            try { current = segment.TextPosition; }
            catch { continue; }                       // not evaluated yet, or not adjustable

            if (current is null) continue;

            // Keep where Revit put it ALONG the run - that is the segment's midpoint and is
            // correct - and reflect only the across coordinate, and only when it is on the
            // wall side. along and across are orthonormal, so this reconstruction is exact.
            var plan = current.ToPlan();

            // Computed from the wall face alone. Revit's own default position is not consulted
            // at all any more - the side it happens to choose told us nothing useful, and
            // reading it was what made two orientations behave differently.
            var home = DimensionMath.TextCentreOffWall(
                placed.Placement.WallAlongInward, settings.Offset, textHeight);

            var start = along.Scaled(plan.Dot(along)).Plus(across.Scaled(home));

            // Too narrow for its own number: it has to move further whether or not anything
            // is in the way. Otherwise it moves only if it collides with something placed.
            var mustMove = segment.Length is { } length &&
                           !DimensionMath.FitsBetweenTicks(length, width, gap);

            // A colliding text slides ALONG its dimension line, never away from the wall.
            // The step is a text width rather than a text height, because that is the
            // distance that actually clears a neighbour in this direction.
            var step = width + gap;

            var band = DimensionMath.FirstClearBand(
                start, along, across, slide: along, right, up,
                width + gap, textHeight + gap, step,
                mustMove, occupied, MaximumBands);

            // Nowhere clear within reach. The corrected position still beats Revit's, which
            // was on the wall, so it is taken anyway - and it keeps the exact wall distance,
            // which matters more than the overlap it leaves for someone to nudge.
            var centre = band < 0
                ? start
                : start.Plus(along.Scaled(DimensionMath.BandOffset(band, step)));

            occupied.Add(DimensionMath.RotatedBox(
                centre, along, across, right, up, width + gap, textHeight + gap));

            if (!segment.TrySetTextPosition(centre.ToWorld(current.Z))) continue;

            // BAND 0 IS NOT A MOVE, and counting it as one made the report lie. Every text
            // gets repositioned here - that is the whole point of this pass, since Revit's
            // default side is the wall - so a run of 20 dimensions reported "20 texts moved
            // clear with a leader because the segment was narrower than its own number", none
            // of which was true. Only a slide off band 0 is a collision, and only that earns
            // a leader or a mention.
            if (band <= 0) continue;

            segment.TrySetLeader();
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// A dimension's segments, or the dimension itself presented as one segment.
    ///
    /// Revit models these as two different things - a two-reference dimension has NO segments
    /// and carries its value and text position directly, a chained one has segments and its
    /// own TextPosition means something else. This wrapper is what stops that distinction
    /// leaking into the arranging logic, which does not care.
    /// </summary>
    private static IEnumerable<SegmentView> Segments(Dimension dimension)
    {
        DimensionSegmentArray? segments = null;
        try { segments = dimension.Segments; } catch { /* single-segment dimension */ }

        if (segments is not null && segments.Size > 0)
        {
            foreach (DimensionSegment segment in segments)
                yield return new SegmentView(segment, dimension);

            yield break;
        }

        yield return new SegmentView(null, dimension);
    }

    private readonly struct SegmentView(DimensionSegment? segment, Dimension owner)
    {
        public string? ValueString => segment is null ? owner.ValueString : segment.ValueString;

        public double? Length => segment is null ? owner.Value : segment.Value;

        public XYZ TextPosition => segment is null ? owner.TextPosition : segment.TextPosition;

        public bool TrySetTextPosition(XYZ position)
        {
            try
            {
                if (segment is not null)
                {
                    if (!segment.IsTextPositionAdjustable()) return false;
                    segment.TextPosition = position;
                    return true;
                }

                if (!owner.IsTextPositionAdjustable()) return false;
                owner.TextPosition = position;
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug($"Text position not adjustable: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Leaders live on the Dimension, never on the segment, so a chained string gets one
        /// leader setting covering all of its moved texts. That is Revit's model, not a
        /// simplification here - which is also why the owning dimension is carried alongside
        /// every segment rather than being reachable only from the single-segment case.
        /// </summary>
        public void TrySetLeader()
        {
            try { owner.HasLeader = true; }
            catch { /* the type does not offer leaders; the move still stands */ }
        }
    }


    private static double ReadLength(Element element, BuiltInParameter id, double fallbackMillimetres)
    {
        var value = element.get_Parameter(id)?.AsDouble() ?? 0.0;
        return value > 0 ? value : Measure.FromMillimetres(fallbackMillimetres);
    }

    private static double ReadNumber(Element element, BuiltInParameter id, double fallback)
    {
        var value = element.get_Parameter(id)?.AsDouble() ?? 0.0;
        return value > 0 ? value : fallback;
    }
}
