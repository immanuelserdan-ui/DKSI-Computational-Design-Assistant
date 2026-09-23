using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Linings;

/// <summary>
/// The value of "Lining YN" and of the material code that the lining tool last settled on,
/// stored invisibly on each door and window. It is what lets <see cref="LiningSync"/> tell
/// which side of a door/window pair the user edited.
///
/// PASTED COPIES START FRESH. Extensible Storage travels with copy/paste, so a copy would
/// inherit its original's record and any later edit on either could be misread. The record
/// names the element it was written for by UniqueId; a mismatch reads as "never recorded".
/// </summary>
internal static class LiningSyncStore
{
    /// <summary>Fixed for the life of the add-in. Changing it forgets every record.</summary>
    private static readonly Guid SchemaId = new("7c3e9a51-2f84-4b6d-a0e7-5d19c8b3f26a");

    private const string SchemaName = "DksiLiningSync";
    private const string VendorId = "DKSI";
    private const string OwnerField = "OwnerUniqueId";
    private const string LiningField = "LiningYN";
    private const string MaterialField = "Material";

    /// <summary>
    /// Every stored value is written behind this marker, so an empty field means "never
    /// recorded" while a recorded blank material is still distinguishable from it.
    /// </summary>
    private const string Recorded = "=";

    private static Schema? _schema;

    private static Schema? Resolve()
    {
        if (_schema is not null && _schema.IsValidObject) return _schema;

        try
        {
            _schema = Schema.Lookup(SchemaId);
            if (_schema is not null) return _schema;

            var builder = new SchemaBuilder(SchemaId);
            builder.SetSchemaName(SchemaName);
            builder.SetVendorId(VendorId);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(OwnerField, typeof(string));
            builder.AddSimpleField(LiningField, typeof(string));
            builder.AddSimpleField(MaterialField, typeof(string));

            _schema = builder.Finish();
            return _schema;
        }
        catch (Exception ex)
        {
            Log.Warn($"Lining sync schema unavailable; door/window edits cannot be told apart: {ex.Message}");
            return null;
        }
    }

    public static bool Available => Resolve() is not null;

    public static (string? Lining, string? Material) Read(Element element)
    {
        var schema = Resolve();
        if (schema is null) return (null, null);

        try
        {
            using var entity = element.GetEntity(schema);
            if (!entity.IsValid()) return (null, null);
            if (entity.Get<string>(OwnerField) != element.UniqueId) return (null, null);

            return (Unwrap(entity.Get<string>(LiningField)), Unwrap(entity.Get<string>(MaterialField)));
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Records either or both values; null leaves that field's record as it was. Writes only
    /// when something differs, so a steady-state run touches no element. Caller owns the
    /// transaction.
    /// </summary>
    public static bool Write(Element element, string? lining, string? material)
    {
        var schema = Resolve();
        if (schema is null) return false;

        var (oldLining, oldMaterial) = Read(element);
        var newLining = lining ?? oldLining;
        var newMaterial = material ?? oldMaterial;

        if (newLining == oldLining && newMaterial == oldMaterial) return true;

        using var entity = new Entity(schema);
        entity.Set(OwnerField, element.UniqueId);
        entity.Set(LiningField, Wrap(newLining));
        entity.Set(MaterialField, Wrap(newMaterial));
        element.SetEntity(entity);
        return true;
    }

    private static string Wrap(string? value) => value is null ? string.Empty : Recorded + value;

    private static string? Unwrap(string? raw) =>
        string.IsNullOrEmpty(raw) || !raw.StartsWith(Recorded, StringComparison.Ordinal)
            ? null
            : raw[Recorded.Length..];
}
