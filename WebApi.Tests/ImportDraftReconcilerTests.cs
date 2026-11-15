using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-58: a redeploy mid-conversion used to leave an ImportDraft at "running" forever —
/// DELETE 409s on running drafts, so the zombie row was undeletable. The startup pass
/// reconciles running rows to failed("interrupted"); queued rows are left alone because
/// Hangfire persists jobs and the restarted server picks them up.
/// </summary>
public class ImportDraftReconcilerTests
{
    private static ApplicationDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    [Fact]
    public async Task RunningDrafts_BecomeFailedInterrupted()
    {
        using var db = NewDb(nameof(RunningDrafts_BecomeFailedInterrupted));
        db.ImportDrafts.AddRange(
            new ImportDraft { Id = Guid.NewGuid(), Status = "running", InputText = "x", CreatedAt = DateTime.UtcNow },
            new ImportDraft { Id = Guid.NewGuid(), Status = "queued", InputText = "x", CreatedAt = DateTime.UtcNow },
            new ImportDraft { Id = Guid.NewGuid(), Status = "succeeded", InputText = "x", CreatedAt = DateTime.UtcNow, ChordProText = "[C] ok" });
        await db.SaveChangesAsync();

        var reconciled = await ImportDraftReconciler.ReconcileStuckDraftsAsync(db);

        Assert.Equal(1, reconciled);
        var rows = await db.ImportDrafts.OrderBy(d => d.Status).ToListAsync();
        var failed = rows.Single(d => d.Status == "failed");
        Assert.Contains("interrupted", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(failed.FinishedAt);
        Assert.NotNull(rows.Single(d => d.Status == "queued"));    // untouched — Hangfire re-runs it
        Assert.NotNull(rows.Single(d => d.Status == "succeeded")); // untouched
    }

    [Fact]
    public async Task NoRunningDrafts_ReconcilesNothing()
    {
        using var db = NewDb(nameof(NoRunningDrafts_ReconcilesNothing));
        db.ImportDrafts.Add(new ImportDraft { Id = Guid.NewGuid(), Status = "queued", InputText = "x", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        Assert.Equal(0, await ImportDraftReconciler.ReconcileStuckDraftsAsync(db));
    }
}
