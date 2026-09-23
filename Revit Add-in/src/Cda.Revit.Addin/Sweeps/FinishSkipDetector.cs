using Autodesk.Revit.DB;
using Cda.Revit.Addin.Finishes;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Sweeps;

/// <summary>
/// Finds the stretches of a board's line where the host's PAINT at board height is a finish
/// that takes no skirting - see <see cref="FinishCodeRule"/>. Shared by Place Skirting, which
/// never places a board there, and by the live trim, which removes one when the wall is
/// repainted after the fact. One implementation, so the two can never disagree about where
/// tile starts.
/// </summary>
internal sealed class FinishSkipDetector
{
    /// <summary>~25 mm between samples; boundaries are then refined to well under a millimetre.</summary>
    private const double SampleStep = 0.082;

    /// <summary>How far off a painted surface a sample may sit, ~25 mm - clear of the far face of any partition.</summary>
    private const double FaceReach = 0.082;

    private readonly Document _doc;
    private readonly string _suffix;
    private readonly double _minimumRun;

    /// <summary>Painted surfaces whose finish takes no board, per host. Read once per host.</summary>
    private readonly Dictionary<long, List<(Face Surface, string Material)>> _surfaces = [];

    public FinishSkipDetector(Document doc, string suffix, double minimumRun)
    {
        _doc = doc;
        _suffix = suffix;
        _minimumRun = minimumRun;
    }

    public bool IsOn => !string.IsNullOrEmpty(_suffix);

    public string Suffix => _suffix;

    /// <summary>
    /// The stretches of <paramref name="axis"/>, in its own parameters, where the paint at
    /// <paramref name="sampleZ"/> takes no board. <paramref name="materials"/> collects what was found.
    ///
    /// SAMPLED ALONG THE RUN, NOT DECIDED PER WALL. Tile is often only part of a face - a split
    /// region behind a bath, a tiled stretch round a shower - so the answer changes along the
    /// wall. Each change is then bisected down to a fraction of a millimetre, so the board stops
    /// where the tile does rather than somewhere within the sample spacing.
    /// </summary>
    public List<Span> SkipSpans(Element host, Curve axis, double sampleZ, ISet<string> materials)
    {
        var spans = new List<Span>();
        if (!IsOn || !axis.IsBound || axis.Length < 1e-6) return spans;

        var surfaces = SurfacesOf(host);
        if (surfaces.Count == 0) return spans;

        var start = axis.GetEndParameter(0);
        var end = axis.GetEndParameter(1);

        string? At(double t)
        {
            try
            {
                var point = axis.Evaluate(t, false);
                return SurfaceAt(surfaces, new XYZ(point.X, point.Y, sampleZ));
            }
            catch
            {
                return null;
            }
        }

        double Edge(double from, double to, bool skipAtFrom)
        {
            for (var i = 0; i < 12; i++)
            {
                var mid = (from + to) / 2.0;
                if ((At(mid) is not null) == skipAtFrom) from = mid;
                else to = mid;
            }

            return (from + to) / 2.0;
        }

        var steps = Math.Max(2, (int)Math.Ceiling(axis.Length / SampleStep));
        var previousT = start;
        var previous = At(start);
        double? openedAt = previous is not null ? start : null;

        if (previous is not null) materials.Add(previous);

        for (var i = 1; i <= steps; i++)
        {
            var t = start + ((end - start) * i / steps);
            var current = At(t);

            if (current is not null) materials.Add(current);

            if ((current is null) != (previous is null))
            {
                var edge = Edge(previousT, t, previous is not null);

                if (current is not null)
                {
                    openedAt = edge;
                }
                else if (openedAt is { } opened)
                {
                    spans.Add(new Span(opened, edge));
                    openedAt = null;
                }
            }

            previous = current;
            previousT = t;
        }

        if (openedAt is { } stillOpen) spans.Add(new Span(stillOpen, end));

        return spans;
    }

    /// <summary>
    /// What is left of <paramref name="piece"/> once the no-skirting stretches are taken out,
    /// in the same direction as the piece. Pieces shorter than the minimum run are dropped.
    /// </summary>
    public List<Curve> Remaining(Curve piece, Element host, double sampleZ, ISet<string> materials)
    {
        var skip = SkipSpans(host, piece, sampleZ, materials);
        if (skip.Count == 0) return [piece];

        var kept = new List<Curve>();
        var whole = new Span(piece.GetEndParameter(0), piece.GetEndParameter(1));

        foreach (var span in SkirtingRun.Subtract(whole, skip))
        {
            if (span.End - span.Start < _minimumRun) continue;

            try
            {
                var part = piece.Clone();
                part.MakeBound(span.Start, span.End);
                kept.Add(part);
            }
            catch
            {
                // A stretch that cannot be bounded is dropped rather than placed whole.
            }
        }

        return kept;
    }

    private List<(Face Surface, string Material)> SurfacesOf(Element host)
    {
        if (_surfaces.TryGetValue(host.Id.Value, out var cached)) return cached;

        var found = new List<(Face, string)>();

        try
        {
            foreach (var (surface, materialId) in SplitFaceRegions.PaintedSurfaces(_doc, host))
            {
                var (name, code, _) = MaterialKey.Of(materialId, painted: true).Describe(_doc);

                if (FinishCodeRule.TakesNoSkirting(code, name, _suffix)) found.Add((surface, name));
            }
        }
        catch (Exception ex)
        {
            // Unreadable paint skips nothing - a board where tile might be beats a missing one
            // on a wall that was simply hard to read.
            Log.Debug($"Paint unreadable on host {host.Id.Value}: {ex.Message}");
        }

        _surfaces[host.Id.Value] = found;
        return found;
    }

    /// <summary>The nearest surface a point lies on, or null. Region boundaries decide, not the parent plane.</summary>
    private static string? SurfaceAt(List<(Face Surface, string Material)> surfaces, XYZ point)
    {
        string? best = null;
        var nearest = double.MaxValue;

        foreach (var (surface, material) in surfaces)
        {
            try
            {
                var hit = surface.Project(point);
                if (hit is null || hit.Distance > FaceReach || hit.Distance >= nearest) continue;
                if (!surface.IsInside(hit.UVPoint)) continue;

                nearest = hit.Distance;
                best = material;
            }
            catch
            {
                // Degenerate surface; the next may answer.
            }
        }

        return best;
    }
}
