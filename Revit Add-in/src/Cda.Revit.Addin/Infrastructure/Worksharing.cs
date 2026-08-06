using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>Which elements a pass may write to, and which it must leave alone.</summary>
public sealed class OwnershipResult
{
    public required IReadOnlyList<ElementId> Writable { get; init; }
    public required IReadOnlyList<ElementId> OwnedByOthers { get; init; }
    public required bool Workshared { get; init; }

    public bool AllWritable => OwnedByOthers.Count == 0;
}

/// <summary>
/// Element ownership in a workshared model.
///
/// THE GAP THIS CLOSES
///   Nothing in this add-in checked ownership. In a single-user file that is harmless -
///   every element is yours. On a central model it is not: writing to an element another
///   user has checked out throws mid-transaction, the whole transaction rolls back, and a
///   pass that had already written two hundred correct values loses all of them because of
///   one element belonging to someone else. The user sees "the tool did nothing" with no
///   indication that the cause was a colleague having a door open.
///
///   Checking out up front converts that into a partial success with a named list: write
///   what we own, report what we do not, and never lose completed work to a foreseeable
///   permission failure.
///
/// WHY CHECKOUT RATHER THAN TRY-AND-CATCH
///   Revit grants ownership per element, and a failed write is only detectable after the
///   fact. <see cref="WorksharingUtils.CheckoutElements(Document, ICollection{ElementId})"/>
///   asks the central model once for the whole set and returns exactly what was granted, so
///   the decision is made before any geometry is written rather than discovered halfway
///   through.
/// </summary>
internal static class Worksharing
{
    /// <summary>
    /// Requests ownership of everything the caller intends to modify.
    ///
    /// MUST be called OUTSIDE a transaction: checkout talks to the central file, and Revit
    /// forbids that from inside an open transaction.
    /// </summary>
    public static OwnershipResult Claim(Document doc, ICollection<ElementId> intended)
    {
        if (!doc.IsWorkshared || intended.Count == 0)
        {
            return new OwnershipResult
            {
                Writable = [.. intended],
                OwnedByOthers = [],
                Workshared = doc.IsWorkshared,
            };
        }

        try
        {
            var granted = WorksharingUtils.CheckoutElements(doc, intended);
            var writable = new HashSet<long>(granted.Select(id => id.Value));

            var refused = intended.Where(id => !writable.Contains(id.Value)).ToList();

            if (refused.Count > 0)
            {
                Log.Warn($"Worksharing: {refused.Count} of {intended.Count} element(s) are owned " +
                         "by other users and will be skipped.");
            }

            return new OwnershipResult
            {
                Writable = [.. granted],
                OwnedByOthers = refused,
                Workshared = true,
            };
        }
        catch (Exception ex)
        {
            // A checkout that cannot even be attempted - central unreachable, model detached -
            // must not stop the pass. Fall through and let individual writes fail safely.
            Log.Warn($"Worksharing checkout could not be performed: {ex.Message}");

            return new OwnershipResult
            {
                Writable = [.. intended],
                OwnedByOthers = [],
                Workshared = true,
            };
        }
    }

    /// <summary>
    /// Whether this element can be written right now, without asking the central model.
    /// Cheap enough for a per-element guard inside a loop.
    /// </summary>
    public static bool CanWrite(Document doc, ElementId id)
    {
        if (!doc.IsWorkshared) return true;

        try
        {
            return WorksharingUtils.GetCheckoutStatus(doc, id) != CheckoutStatus.OwnedByOtherUser;
        }
        catch
        {
            return true;   // undecidable: let the write attempt decide
        }
    }

    /// <summary>Who holds an element, for a report line that names the person to ask.</summary>
    public static string? Owner(Document doc, ElementId id)
    {
        if (!doc.IsWorkshared) return null;

        try
        {
            var info = WorksharingUtils.GetWorksharingTooltipInfo(doc, id);
            return string.IsNullOrWhiteSpace(info?.Owner) ? null : info!.Owner;
        }
        catch
        {
            return null;
        }
    }
}
