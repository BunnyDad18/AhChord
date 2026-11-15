using Microsoft.EntityFrameworkCore;
using WebApi.Data;

namespace WebApi.Services;

/// <summary>
/// ST-58: startup reconciliation of import drafts left behind by a previous process.
/// A redeploy mid-conversion (the 300 s LLM call outlives the graceful-shutdown window)
/// used to leave a draft at "running" forever — and DELETE answers 409 for running drafts,
/// so the row was undeletable and the queue showed a zombie. Marked failed with a
/// retryable reason; the user can retry (ST-35 E2) or delete.
/// Runs at boot BEFORE the Hangfire server starts, so no live job can be mid-flight.
/// </summary>
public static class ImportDraftReconciler
{
    public const string InterruptedReason =
        "Interrupted — the server restarted mid-conversion. Use Retry, or delete this draft.";

    /// <summary>Returns the number of drafts reconciled (logged by the caller).</summary>
    public static async Task<int> ReconcileStuckDraftsAsync(ApplicationDbContext db)
    {
        // "queued" rows are fine: Hangfire persists jobs in Postgres, the restarted server
        // picks them up. Only "running" rows can have lost their job. Plain fetch+mutate
        // (not ExecuteUpdateAsync) so the pass is unit-testable on the InMemory provider.
        var stuck = await db.ImportDrafts.Where(d => d.Status == "running").ToListAsync();
        foreach (var d in stuck)
        {
            d.Status = "failed";
            d.Error = InterruptedReason;
            d.FinishedAt = DateTime.UtcNow;
        }
        if (stuck.Count > 0)
            await db.SaveChangesAsync();
        return stuck.Count;
    }
}
