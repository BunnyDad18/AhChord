using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;
using WebApi.Endpoints;

namespace WebApi.Tests;

/// <summary>
/// ST-73: the conversion queue's "Import all" — POST /api/import/convert/import-all turns every
/// succeeded, not-yet-imported draft of the caller into a song in one pass. The endpoint is a
/// thin wrapper around ImportAllAsync (Hangfire scan-enqueue injected), so the tests drive
/// the helper against an in-memory DB.
/// </summary>
public class St73ImportAllTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ImportDraft AddDraft(
        ApplicationDbContext db, string userId, string title, string status = "succeeded",
        string? chordPro = null, Guid? savedSongId = null)
    {
        var d = new ImportDraft
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = status,
            InputUrl = $"https://tabs.example/{title.Replace(' ', '-')}",
            InputText = "raw chord page",
            InputPreview = title,
            ExtractedTitle = title,
            ExtractedArtist = "The Artist",
            ChordProText = chordPro ?? $"{{title: {title}}}\n{{artist: The Artist}}\n[C] lyric [G] line",
            SavedSongId = savedSongId,
            CreatedAt = DateTime.UtcNow
        };
        db.ImportDrafts.Add(d);
        return d;
    }

    [Fact]
    public async Task ImportAll_ImportsEveryConvertedDraft_AndLinksTheDraft()
    {
        using var db = NewDb();
        var a = AddDraft(db, "u1", "Alpha");
        var b = AddDraft(db, "u1", "Beta");
        await db.SaveChangesAsync();
        var newSongs = new List<Song>();

        var result = await ImportConvertEndpoints.ImportAllAsync(db, "u1", newSongs.Add);

        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Duplicates);
        Assert.Empty(result.Failures);
        Assert.Equal(2, await db.Songs.CountAsync());
        // Each draft links to its song — the row flips to "imported" (and re-import is idempotent).
        foreach (var d in new[] { a, b })
            Assert.NotNull((await db.ImportDrafts.FirstAsync(x => x.Id == d.Id)).SavedSongId);
        // The metadata scan fires for every genuinely new song.
        Assert.Equal(2, newSongs.Count);
    }

    [Fact]
    public async Task ImportAll_OnlyTheCallers_Drafts()
    {
        using var db = NewDb();
        AddDraft(db, "u1", "Mine");
        AddDraft(db, "u2", "Theirs");
        await db.SaveChangesAsync();

        var result = await ImportConvertEndpoints.ImportAllAsync(db, "u1");

        Assert.Equal(1, result.Imported);
        var song = await db.Songs.SingleAsync();
        Assert.Equal("Mine", song.Title);
    }

    [Fact]
    public async Task ImportAll_SkipsImported_AndNotYetConverted()
    {
        using var db = NewDb();
        var existing = new Song { Id = Guid.NewGuid(), Title = "Old", Artist = "A", ChordProText = "x", CreatedAt = DateTime.UtcNow };
        db.Songs.Add(existing);
        AddDraft(db, "u1", "Already imported", savedSongId: existing.Id);
        AddDraft(db, "u1", "Still running", status: "running");
        AddDraft(db, "u1", "Failed", status: "failed", chordPro: null);
        AddDraft(db, "u1", "Queued", status: "queued", chordPro: null);
        await db.SaveChangesAsync();

        var result = await ImportConvertEndpoints.ImportAllAsync(db, "u1");

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, await db.Songs.CountAsync()); // only the pre-existing one
    }

    [Fact]
    public async Task ImportAll_DuplicateConversion_LinksExistingSong()
    {
        using var db = NewDb();
        var first = AddDraft(db, "u1", "Same Song");
        AddDraft(db, "u1", "Same Song"); // identical title+artist+chart → ST-49 duplicate
        await db.SaveChangesAsync();

        var result = await ImportConvertEndpoints.ImportAllAsync(db, "u1");

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(1, await db.Songs.CountAsync());
        var drafts = await db.ImportDrafts.ToListAsync();
        Assert.All(drafts, d => Assert.Equal(drafts[0].SavedSongId, d.SavedSongId));
    }

    [Fact]
    public async Task ImportAll_BrokenDraft_FailsAlone_TheBatchContinues()
    {
        using var db = NewDb();
        var good = AddDraft(db, "u1", "Good");
        var broken = AddDraft(db, "u1", "Broken", chordPro: "   ");
        await db.SaveChangesAsync();

        var result = await ImportConvertEndpoints.ImportAllAsync(db, "u1");

        Assert.Equal(1, result.Imported);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(broken.Id, failure.Id);
        Assert.Equal("Broken", failure.Title);
        // The good draft is imported; the broken one stays in the queue (no SavedSongId).
        Assert.NotNull((await db.ImportDrafts.FirstAsync(d => d.Id == good.Id)).SavedSongId);
        Assert.Null((await db.ImportDrafts.FirstAsync(d => d.Id == broken.Id)).SavedSongId);
    }

    [Fact]
    public async Task ImportAll_NothingConverted_ReportsNothing()
    {
        using var db = NewDb();
        AddDraft(db, "u1", "Queued", status: "queued", chordPro: null);
        await db.SaveChangesAsync();

        var result = await ImportConvertEndpoints.ImportAllAsync(db, "u1");

        Assert.Equal(0, result.Imported);
        Assert.Equal(0, result.Duplicates);
        Assert.Empty(result.Saved);
        Assert.Empty(result.Failures);
    }
}
