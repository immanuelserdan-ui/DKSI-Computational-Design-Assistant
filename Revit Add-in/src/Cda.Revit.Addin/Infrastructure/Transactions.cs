using Autodesk.Revit.DB;

namespace Cda.Revit.Addin.Infrastructure;

/// <summary>
/// Transaction helpers.
///
/// Rules that are not optional in the Revit API:
///   * Any write to the model must happen inside a Transaction.
///   * A Transaction can only be started when no other transaction is open on that
///     document, and only from a command declared [Transaction(TransactionMode.Manual)].
///   * If you do not Commit, you must RollBack. A disposed-but-uncommitted transaction
///     rolls back, which is why every path here goes through <c>using</c>.
///   * One transaction per logical operation, not one per element. Thousands of tiny
///     transactions is the single most common cause of "the add-in takes 20 minutes".
/// </summary>
internal static class Transactions
{
    /// <summary>Runs <paramref name="action"/> inside a single committed transaction.</summary>
    public static void Run(Document doc, string name, Action action)
    {
        using var tx = new Transaction(doc, name);
        tx.Start();
        try
        {
            action();
            tx.Commit();
        }
        catch
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside a transaction group so the whole batch
    /// shows up as one undo step, and can be abandoned wholesale if anything fails.
    /// Use this when a tool performs several distinct transactions in sequence.
    /// </summary>
    public static void RunGrouped(Document doc, string name, Action action)
    {
        using var group = new TransactionGroup(doc, name);
        group.Start();
        try
        {
            action();
            group.Assimilate();   // collapses the inner transactions into one undo entry
        }
        catch
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            throw;
        }
    }
}
