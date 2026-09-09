using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Finishes;

/// <summary>
/// Pre-flight check for "Painted Surface Area": does a room already carry a material whose
/// office code ends in a given letter - "F" for Fliser (tile), by the same convention
/// <see cref="MaterialKey.Describe"/> already reads for the finish engine (the material's own
/// Code / Mark / Keynote parameter, first non-empty).
///
/// DELIBERATELY LIGHTER THAN RoomFinishCalculator. That engine intersects every boundary
/// subface against the host's real solid to get NET areas - the right tool for a takeoff, and
/// far more than a yes/no gate needs. This walks the same SpatialElementGeometryCalculator
/// boundary (the one PaintSurfaceExtractor and RoomFinishCalculator both use) only far enough
/// to name which elements bound the room, then asks each one - via the Revit API's own
/// Element.GetMaterialIds - what materials it carries, type layers and paint both. No boolean
/// intersections, no areas, no transaction.
/// </summary>
public static class RoomMaterialCodeGate
{
    /// <summary>
    /// Alrum, Køkken - and the ASCII spelling, since a room named without the Danish "ø"
    /// should still match. Same dual-spelling convention as SkirtingSettings.CasedOpeningHints.
    /// </summary>
    public static readonly string[] AlrumKokkenKeywords = ["Alrum", "Køkken", "Kokken"];

    public static readonly string[] BadToiletKeywords = ["Bad", "Toilet"];

    public sealed record FailedRoom(string Label, long RoomId);

    /// <summary>
    /// Every room whose Name or Department contains one of <paramref name="roomKeywords"/>
    /// (case-insensitive substring, matching SkirtingSettings' own convention) and carries no
    /// material whose Code/Mark/Keynote ends in <paramref name="codeSuffix"/>.
    ///
    /// A room whose geometry cannot be calculated is skipped rather than failed - this gate
    /// answers "was the required code found", and "could not look" is not the same claim as
    /// "did not find it".
    /// </summary>
    public static IReadOnlyList<FailedRoom> FindRoomsMissingCode(
        Document doc, IReadOnlyList<string> roomKeywords, string codeSuffix)
    {
        var failures = new List<FailedRoom>();

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
        };
        var calculator = new SpatialElementGeometryCalculator(doc, options);

        var rooms = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Autodesk.Revit.DB.Architecture.Room>();

        foreach (var room in rooms)
        {
            if (room.Area <= 0) continue; // unplaced
            if (!MatchesKeywords(room, roomKeywords)) continue;
            if (!SpatialElementGeometryCalculator.CanCalculateGeometry(room)) continue;

            bool found;
            try
            {
                found = RoomHasMaterialCodeEndingIn(doc, room, calculator, codeSuffix);
            }
            catch
            {
                // Same reasoning as CanCalculateGeometry above: a room this failed for was
                // never actually checked, so it is not reported as missing the code.
                continue;
            }

            if (!found) failures.Add(new FailedRoom(RoomLabel(room), room.Id.Value));
        }

        return failures;
    }

    private static bool RoomHasMaterialCodeEndingIn(
        Document doc, Autodesk.Revit.DB.Architecture.Room room,
        SpatialElementGeometryCalculator calculator, string codeSuffix)
    {
        var results = calculator.CalculateSpatialElementGeometry(room);
        var checkedMaterialIds = new HashSet<long>();
        var checkedHostIds = new HashSet<long>();

        foreach (Face roomFace in results.GetGeometry().Faces)
        {
            IList<SpatialElementBoundarySubface> subfaces;
            try { subfaces = results.GetBoundaryFaceInfo(roomFace); }
            catch { continue; }

            if (subfaces is null) continue;

            foreach (var subface in subfaces)
            {
                Element? owner;
                try
                {
                    var boundary = subface.SpatialBoundaryElement;

                    // A linked host cannot be queried for materials from this document, and
                    // an invalid host bounds nothing real - see PaintSurfaceExtractor.Extract
                    // for the same two guards over the same API.
                    if (boundary.LinkInstanceId != ElementId.InvalidElementId) continue;
                    if (boundary.HostElementId == ElementId.InvalidElementId) continue;

                    owner = doc.GetElement(boundary.HostElementId);
                }
                catch
                {
                    continue;
                }

                if (owner is null || !checkedHostIds.Add(owner.Id.Value)) continue;

                foreach (var materialId in HostMaterialIds(owner))
                {
                    if (!checkedMaterialIds.Add(materialId.Value)) continue;

                    var code = MaterialKey.Of(materialId, painted: false).Describe(doc).Code;
                    if (code.EndsWith(codeSuffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Both halves of what a host can carry: its type's own layer materials, and whatever has
    /// been painted onto it - either can be where a tile code is set.
    /// </summary>
    private static IEnumerable<ElementId> HostMaterialIds(Element owner)
    {
        IEnumerable<ElementId> layerIds;
        IEnumerable<ElementId> paintIds;

        try { layerIds = owner.GetMaterialIds(returnPaintMaterials: false); }
        catch { layerIds = []; }

        try { paintIds = owner.GetMaterialIds(returnPaintMaterials: true); }
        catch { paintIds = []; }

        return layerIds.Concat(paintIds);
    }

    private static bool MatchesKeywords(Autodesk.Revit.DB.Architecture.Room room, IReadOnlyList<string> keywords)
    {
        string name, department;
        try
        {
            name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            department = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? string.Empty;
        }
        catch
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
            if (department.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string RoomLabel(Autodesk.Revit.DB.Architecture.Room room)
    {
        try
        {
            var number = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? string.Empty;
            var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            var label = $"{number} {name}".Trim();
            return label.Length > 0 ? label : room.Id.Value.ToString();
        }
        catch
        {
            return room.Id.Value.ToString();
        }
    }
}
