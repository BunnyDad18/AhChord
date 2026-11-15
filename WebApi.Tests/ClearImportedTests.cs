using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;
using WebApi.Endpoints;

namespace WebApi.Tests;

/// <summary>
/// Report (2026-11-12, /queue): DELETE /api/import/convert/imported — "a clear all imported
/// button in the conversion queue". The endpoint is a thin wrapper around ClearImportedAsync,
/// so the tests drive the helper against an in-memory DB (same shape as St73ImportAllTests).
/// </summary>
public class ClearImportedTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ImportDraft AddDraft(
        ApplicationDbContext db, string? userId, string status, Guid? savedSongId)
    {
        var d = new ImportDraft
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = status,
            InputText = "raw chord page",
            InputPreview = "A song",
            SavedSongId = savedSongId,
            CreatedAt = DateTime.UtcNow
        };
        db.ImportDrafts.Add(d);
        return d;
    }

    [Fact]
    public async Task ClearImported_RemovesOnlyImportedDraftsOfTheCaller()
    {
        using var db = NewDb();
        var imported = AddDraft(db, "u1", "succeeded", Guid.NewGuid());
        var converted = AddDraft(db, "u1", "succeeded", null); // Review is a live action — stays
        var failed = AddDraft(db, "u1", "failed", null); // Retry is a live action — stays
        var queued = AddDraft(db, "u1", "queued", null);
        var theirs = AddDraft(db, "u2", "succeeded", Guid.NewGuid()); // other users untouched
        var orphan = AddDraft(db, null, "succeeded", Guid.NewGuid()); // the list filter drops these
        await db.SaveChangesAsync();

        var removed = await ImportConvertEndpoints.ClearImportedAsync(db, "u1");

        Assert.Equal(1, removed);
        Assert.Empty(await db.ImportDrafts.Where(d => d.Id == imported.Id).ToListAsync());
        // The null-UserId row stays: EF translates the comparison SQL-style (NULL never
        // matches) — the same "nobody's" rule the list endpoint documents.
        Assert.True(await db.ImportDrafts.AnyAsync(d => d.Id == orphan.Id));
        Assert.Equal(5, await db.ImportDrafts.CountAsync());
        Assert.True(await db.ImportDrafts.AnyAsync(d => d.Id == converted.Id));
        Assert.True(await db.ImportDrafts.AnyAsync(d => d.Id == failed.Id));
        Assert.True(await db.ImportDrafts.AnyAsync(d => d.Id == queued.Id));
        Assert.True(await db.ImportDrafts.AnyAsync(d => d.Id == theirs.Id));
    }

    [Fact]
    public async Task ClearImported_AnonymousCallerRemovesNothing()
    {
        using var db = NewDb();
        AddDraft(db, "u1", "succeeded", Guid.NewGuid());
        await db.SaveChangesAsync();

        Assert.Equal(0, await ImportConvertEndpoints.ClearImportedAsync(db, null));
        Assert.Equal(1, await db.ImportDrafts.CountAsync());
    }

    [Fact]
    public async Task ClearImported_EmptyQueue_IsANoOp()
    {
        using var db = NewDb();
        Assert.Equal(0, await ImportConvertEndpoints.ClearImportedAsync(db, "u1"));
    }
}
