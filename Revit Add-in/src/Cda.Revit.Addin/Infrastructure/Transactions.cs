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
    public static void Run(Document doc, string name, Action action) =>
        Run(doc, name, action, swallowWarnings: false);

    /// <summary>
    /// Runs <paramref name="action"/> inside a single committed transaction, optionally
    /// resolving Revit's own warnings instead of showing them.
    /// </summary>
    /// <param name="swallowWarnings">
    /// TRUE FOR BATCH WORK, and it is not a way of hiding problems - it is the difference
    /// between a tool that finishes and one that stops on the eleventh of fifty-one elements
    /// waiting for somebody to click OK.
    ///
    /// Revit posts warnings during element creation - "element is slightly off axis", "the
    /// element is outside its host" and dozens more - and the default handler shows each in a
    /// modal dialog. Unattended, a long run blocks forever; attended, the user clicks through
    /// forty identical boxes and stops reading them.
    ///
    /// WARNINGS ONLY. Errors are left alone, so anything Revit considers genuinely invalid
    /// still surfaces and still rolls the transaction back. And nothing here excuses a tool
    /// from reporting: this add-in verifies its own work by re-measuring what it created, so a
    /// warning Revit resolved silently still shows up as a fault in the report if it changed
    /// the result.
    /// </param>
    public static void Run(Document doc, string name, Action action, bool swallowWarnings)
    {
        using var tx = new Transaction(doc, name);
        tx.Start();

        if (swallowWarnings)
        {
            var options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new WarningSwallower());
            options.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(options);
        }

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
    /// Deletes Revit's warnings and lets everything else through.
    ///
    /// DeleteWarning is the only resolution taken. The tempting next step - calling
    /// ResolveFailure on errors so the run never stops - hands Revit permission to fix a
    /// problem by deleting the element it is complaining about, and a batch tool that quietly
    /// destroys geometry to keep going is worse than one that stops.
    /// </summary>
    private sealed class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var resolved = false;

            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() != FailureSeverity.Warning) continue;

                accessor.DeleteWarning(failure);
                resolved = true;
            }

            return resolved
                ? FailureProcessingResult.ProceedWithCommit
                : FailureProcessingResult.Continue;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside a transaction that is ALWAYS rolled back.
    ///
    /// This is how a dry run reports what would happen without guessing at it. Some questions
    /// only Revit can answer, and it will only answer them by being asked to do the work —
    /// whether a family void actually reaches a wall is one (see CaseworkVoidCutter). Doing
    /// it and undoing it is exact where a re-derived test is an approximation, and it costs
    /// the user nothing: a rolled-back transaction leaves no model change and no undo entry.
    ///
    /// Exceptions propagate, after the rollback. Nothing here swallows a failure, because a
    /// dry run that reports success on work that threw is worse than no dry run.
    /// </summary>
    public static void Probe(Document doc, string name, Action action)
    {
        using var tx = new Transaction(doc, name);
        tx.Start();
        try
        {
            action();
        }
        finally
        {
            // finally, not catch: the rollback has to happen on the way out whether the
            // action succeeded or threw. Disposing an open transaction rolls back too, so
            // this is belt as well as braces - and the explicit call is what makes it
            // readable as intent rather than as a forgotten Commit.
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
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
