using Autodesk.Revit.DB;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Linings;

/// <summary>
/// One door or window, reduced to the lining rectangle it occupies in wall-local
/// coordinates: u = distance along the wall, z = absolute elevation.
/// </summary>
public sealed class Opening
{
    private readonly LiningSettings _settings;

    public FamilyInstance Instance { get; }
    public long Id { get; }
    public WallAxis Axis { get; private set; }
    public List<string> Warnings { get; } = [];

    public string Family { get; }
    public string TypeName { get; }
    public long CategoryId { get; }
    public string Category { get; }
    public string Mark { get; }

    public double UCenter { get; private set; }

    /// <summary>Sign of family +X along +u. Decides which world edge is "Left".</summary>
    public double Sx { get; private set; }

    public double SLeft { get; private set; }

    public double Width { get; }
    public string WidthSource { get; }
    public double Height { get; }
    public string HeightSource { get; }

    public double ZBottom { get; }
    public double ZTop { get; }

    public string DoorMaterial { get; }
    public string WindowMaterial { get; }

    public int Priority { get; }
    public int? MasterOn { get; }

    /// <summary>
    /// "Lining YN" unchecked means this opening has no lining at all - it is filtered
    /// straight out of the lining schedule. A missing parameter counts as on.
    /// </summary>
    public bool HasLining => MasterOn != 0;

    public bool Skip { get; }

    /// <summary>
    /// False when the family carries no "Lining Change" / "Lining ... YN" parameters.
    ///
    /// Such an opening is still loaded: it physically occupies its share of the wall
    /// reveal, so it must still block its neighbours, and the Window Material / Lining YN
    /// rules are about door-to-window contact and have nothing to do with lining
    /// parameters. It simply never has lining values written to it.
    /// </summary>
    public bool HasLiningParameters { get; }

    public Opening(FamilyInstance instance, WallAxis axis, double levelElevation, LiningSettings settings)
    {
        _settings = settings;
        Instance = instance;
        Id = instance.Id.Value;
        Axis = axis;

        var symbol = instance.Symbol;
        Family = symbol?.Family.Name ?? "?";
        TypeName = symbol?.Name ?? "?";
        CategoryId = instance.Category?.Id.Value ?? 0;
        Category = instance.Category?.Name ?? "?";
        Mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty;

        var point = LocationPoint(instance);
        UCenter = axis.UOf(point);
        var tangent = axis.TangentAt(point);

        Sx = HandSign(instance, tangent, settings);
        SLeft = settings.LeftIsFamilyPlusX * Sx;

        if (settings.HandednessSource == LiningSettings.Handedness.InstanceHand)
        {
            try
            {
                var basisX = instance.GetTransform().BasisX;
                var alternate = basisX.DotProduct(tangent) > 0 ? 1.0 : -1.0;
                if (Math.Abs(alternate - Sx) > 1e-9)
                    Warnings.Add("HandOrientation and instance transform disagree on handing");
            }
            catch
            {
                // No transform available; HandOrientation stands alone.
            }
        }

        var (width, widthSource) = NumberOfAny(instance, settings.Width);
        if (width is null)
        {
            width = Number(instance, settings.SideLength[LiningSide.Top]);
            widthSource = settings.SideLength[LiningSide.Top];
        }
        Width = width ?? 0.0;
        WidthSource = widthSource ?? "?";

        var sill = BuiltIn(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) ?? 0.0;
        var head = BuiltIn(instance, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM);

        var (height, heightSource) = NumberOfAny(instance, settings.Height);
        if (height is null && head is not null)
        {
            height = head - sill;
            heightSource = "Head - Sill";
        }
        Height = height ?? 0.0;
        HeightSource = heightSource ?? "?";

        ZBottom = levelElevation + sill;
        ZTop = ZBottom + Height;

        if (head is not null && Math.Abs(sill + Height - head.Value) > Units.ToFeet(1.0))
        {
            Warnings.Add($"Head Height {Units.ToMm(levelElevation + head.Value):0} does not equal " +
                         $"Sill + {HeightSource} ({Units.ToMm(ZTop):0})");
        }

        // Reported lining lengths should agree with the rectangle we derived.
        var topLength = Number(instance, settings.SideLength[LiningSide.Top]);
        if (topLength is not null && topLength != 0.0 && Math.Abs(topLength.Value - Width) > Units.ToFeet(1.0))
        {
            Warnings.Add($"'Lining Top' {Units.ToMm(topLength.Value):0} != " +
                         $"{WidthSource} {Units.ToMm(Width):0}");
        }

        foreach (var side in new[] { LiningSide.Left, LiningSide.Right })
        {
            var sideLength = Number(instance, settings.SideLength[side]);
            if (sideLength is not null && sideLength != 0.0 &&
                Math.Abs(sideLength.Value - Height) > Units.ToFeet(1.0))
            {
                Warnings.Add($"'Lining {side}' {Units.ToMm(sideLength.Value):0} != " +
                             $"{HeightSource} {Units.ToMm(Height):0}");
            }
        }

        DoorMaterial = Text(instance, settings.DoorMaterial);
        WindowMaterial = Text(instance, settings.WindowMaterial);

        Priority = settings.CategoryPriority.GetValueOrDefault(CategoryId, 0);
        MasterOn = Integer(instance, settings.Master);

        HasLiningParameters =
            ParameterHelper.Find(instance, settings.Change) is not null &&
            ParameterHelper.Find(instance, settings.SideYn[LiningSide.Top]) is not null;

        var comments = instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
        Skip = !string.IsNullOrEmpty(comments) &&
               comments.Contains(settings.SkipMarker, StringComparison.OrdinalIgnoreCase);
    }

    // -- lining rectangle ------------------------------------------------------

    public double ULo => UCenter - Width / 2.0;
    public double UHi => UCenter + Width / 2.0;

    public Span USpan => new(ULo, UHi);
    public Span ZSpan => new(ZBottom, ZTop);

    /// <summary>(position, outward normal, span) for one lining edge.</summary>
    public (double Position, double Normal, Span Span) Edge(LiningSide side)
    {
        if (side == LiningSide.Top) return (ZTop, 1.0, USpan);

        var sign = side == LiningSide.Left ? SLeft : -SLeft;
        return (UCenter + sign * Width / 2.0, sign, ZSpan);
    }

    public string Label() => $"{Id} [{(Mark.Length > 0 ? Mark : "-")}] {Family} : {TypeName}";

    /// <summary>
    /// Re-measure against a different axis after collinear walls are merged.
    ///
    /// <see cref="Axis"/> deliberately keeps pointing at the opening's OWN host wall, as
    /// the Dynamo graph does - only the measurement moves to the shared axis. Overwriting
    /// it would make the report's HostWallId column name the merged run's root wall
    /// instead of the wall the opening is actually in.
    /// </summary>
    public void Reproject(WallAxis reference)
    {
        var point = LocationPoint(Instance);
        UCenter = reference.UOf(point);

        Sx = HandSign(Instance, reference.TangentAt(point), _settings);
        SLeft = _settings.LeftIsFamilyPlusX * Sx;
    }

    /// <summary>
    /// +1 or -1: which way along the wall the family's +X points, for the purpose of
    /// naming the Left and Right lining edges.
    /// </summary>
    private static double HandSign(FamilyInstance instance, XYZ tangent, LiningSettings settings) =>
        settings.HandednessSource == LiningSettings.Handedness.WallAxis
            ? 1.0                                          // "Left" is one fixed direction along the wall
            : instance.HandOrientation.DotProduct(tangent) > 0 ? 1.0 : -1.0;

    /// <summary>True when two lining rectangles share a face within the tolerance.</summary>
    public static bool Touches(Opening a, Opening b, double toleranceFeet)
    {
        var du = Span.Overlap(a.USpan, b.USpan);
        var dz = Span.Overlap(a.ZSpan, b.ZSpan);

        if (du is not null && dz is not null) return true;   // genuinely overlapping

        // Side by side needs a vertical overlap and a small horizontal gap; stacked needs
        // the reverse. Two rectangles meeting only at a corner are NOT touching - there is
        // no shared face there, so no lining is affected.
        if (dz is not null)
        {
            var gap = Math.Max(a.ULo - b.UHi, b.ULo - a.UHi);
            if (gap >= -toleranceFeet && gap <= toleranceFeet) return true;
        }

        if (du is not null)
        {
            var gap = Math.Max(a.ZBottom - b.ZTop, b.ZBottom - a.ZTop);
            if (gap >= -toleranceFeet && gap <= toleranceFeet) return true;
        }

        return false;
    }

    /// <summary>True if <paramref name="a"/> keeps the contested reveal.</summary>
    public static bool Wins(Opening a, Opening b) =>
        a.Priority != b.Priority ? a.Priority > b.Priority : a.Id < b.Id;

    // -- parameter helpers -----------------------------------------------------

    /// <summary>
    /// World XYZ of a hosted family instance. LocationPoint exposes .Point - there is no
    /// .Position on it. Falls back to a curve midpoint for curve-driven families.
    /// </summary>
    public static XYZ LocationPoint(FamilyInstance instance) => instance.Location switch
    {
        Autodesk.Revit.DB.LocationPoint p => p.Point,
        LocationCurve { Curve: { } curve } => curve.Evaluate(0.5, true),
        _ => throw new InvalidOperationException($"instance {instance.Id.Value} has no usable location"),
    };

    internal static double? Number(Element element, string name)
    {
        var parameter = ParameterHelper.Find(element, name);
        if (parameter is null || !parameter.HasValue) return null;

        return parameter.StorageType switch
        {
            StorageType.Double => parameter.AsDouble(),
            StorageType.Integer => parameter.AsInteger(),
            _ => null,
        };
    }

    internal static (double? Value, string? Source) NumberOfAny(Element element, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var value = Number(element, name);
            if (value is not null) return (value, name);
        }

        return (null, null);
    }

    internal static int? Integer(Element element, string name)
    {
        var parameter = ParameterHelper.Find(element, name);
        return parameter is null || !parameter.HasValue || parameter.StorageType != StorageType.Integer
            ? null
            : parameter.AsInteger();
    }

    internal static string Text(Element element, string name)
    {
        var parameter = ParameterHelper.Find(element, name);
        return parameter is null || !parameter.HasValue || parameter.StorageType != StorageType.String
            ? string.Empty
            : parameter.AsString() ?? string.Empty;
    }

    private static double? BuiltIn(Element element, BuiltInParameter bip)
    {
        try
        {
            var parameter = element.get_Parameter(bip);
            if (parameter is null || !parameter.HasValue) return null;

            return parameter.StorageType switch
            {
                StorageType.Double => parameter.AsDouble(),
                StorageType.Integer => parameter.AsInteger(),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
