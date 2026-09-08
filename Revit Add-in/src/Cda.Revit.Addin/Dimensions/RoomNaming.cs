using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Cda.Revit.Addin.Dimensions;

/// <summary>
/// Reads a room's name without its number stuck on the end.
///
/// WHY THIS IS NOT JUST room.Name
///   Room overrides Element.Name to return NAME PLUS NUMBER, concatenated with a space. The
///   FM template's rooms read back as "Entre 6", "Vaer. 1 10", "Kaelderrum 4 40" - the number
///   is not a separate field there, it is part of the string. Confirmed against the live
///   model, not assumed.
///
///   The damage is quiet. A report column prints "Entre 6" beside a Number column that already
///   says 6; a note reads "Room 6 'Entre 6'"; and any exclusion rule written as an EQUALS test
///   silently matches nothing, because the room a user calls "Udvendig" is "Udvendig 99" to
///   this property and the two are never equal.
///
///   ROOM_NAME is the parameter holding what a person means by the room's name. Element.Name
///   is a display convenience, and using it as data is the mistake.
/// </summary>
internal static class RoomNaming
{
    public static string NameOf(Room room)
    {
        var parameter = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();

        // Falling back to Element.Name is right even knowing it carries the number: a room
        // whose ROOM_NAME is empty still has to be described somehow, and "Entre 6" beats
        // an empty string in a report.
        return string.IsNullOrWhiteSpace(parameter) ? room.Name ?? string.Empty : parameter;
    }

    public static string NumberOf(Room room) =>
        room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? room.Number ?? string.Empty;

    /// <summary>"Room 6 'Entre'", for notes and log lines.</summary>
    public static string Describe(Room room) => $"Room {NumberOf(room)} '{NameOf(room)}'";
}
