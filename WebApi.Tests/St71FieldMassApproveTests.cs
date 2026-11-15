using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;
using WebApi.Endpoints;

namespace WebApi.Tests;

/// <summary>
/// ST-71 (in-app report 2026-11-01 15:49): field-wide mass-approve — "accept all year
/// changes", not just one year→value category. The endpoints are thin wrappers around
/// ApplyProposalsAsync (shared with ST-69's accept-category), so the tests drive the
/// helper directly against an in-memory DB.
/// </summary>
public class St71FieldMassApproveTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Song AddSong(ApplicationDbContext db, string title, int? year = null, string? genre = null)
    {
        var song = new Song
        {
            Id = Guid.NewGuid(),
            Title = title,
            Artist = "A",
            ChordProText = "{title: t}\n[C] lyric",
            Year = year,
            Genre = genre,
            CreatedAt = DateTime.UtcNow
        };
        db.Songs.Add(song);
        return song;
    }

    private static MetadataProposal AddProposal(
        ApplicationDbContext db, Song song, string field, string proposed, string status = "pending")
    {
        var p = new MetadataProposal
        {
            Id = Guid.NewGuid(),
            SongId = song.Id,
            Field = field,
            ProposedValue = proposed,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        db.MetadataProposals.Add(p);
        return p;
    }

    [Fact]
    public async Task AcceptField_AppliesEveryValueOfTheField_AcrossTheLibrary()
    {
        using var db = NewDb();
        var a = AddSong(db, "A");
        var b = AddSong(db, "B");
        var c = AddSong(db, "C", genre: "rock");
        AddProposal(db, a, "year", "1985");
        AddProposal(db, b, "year", "2001");
        AddProposal(db, c, "genre", "worship");
        await db.SaveChangesAsync();

        var result = await MetadataReviewEndpoints.ApplyProposalsAsync(db, "year", value: null);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Applied);
        Assert.Equal(0, result.Value.Skipped);
        Assert.Equal(2, result.Value.Songs);
        Assert.Equal(1985, (await db.Songs.FirstAsync(s => s.Id == a.Id)).Year);
        Assert.Equal(2001, (await db.Songs.FirstAsync(s => s.Id == b.Id)).Year);

        // The other field's proposal is untouched.
        var genre = await db.MetadataProposals.SingleAsync(p => p.Field == "genre");
        Assert.Equal("pending", genre.Status);
    }

    [Fact]
    public async Task AcceptField_UnapplyableProposals_StayPending()
    {
        using var db = NewDb();
        var a = AddSong(db, "A");
        var good = AddProposal(db, a, "year", "1985");
        var bad = AddProposal(db, a, "year", "999"); // TryApply rejects: out of range
        await db.SaveChangesAsync();

        var result = await MetadataReviewEndpoints.ApplyProposalsAsync(db, "year", value: null);

        Assert.Equal((1, 1, 1), result);
        Assert.Equal("accepted", (await db.MetadataProposals.FirstAsync(p => p.Id == good.Id)).Status);
        var stillPending = await db.MetadataProposals.FirstAsync(p => p.Id == bad.Id);
        Assert.Equal("pending", stillPending.Status);
        Assert.Null(stillPending.ResolvedAt);
    }

    [Fact]
    public async Task AcceptField_ProposalOfDeletedSong_CountsAsSkipped()
    {
        using var db = NewDb();
        AddProposal(db, new Song { Id = Guid.NewGuid(), Title = "Gone", ChordProText = "" }, "year", "1985");
        // The song itself is NOT in the DB — the proposal references a song deleted under the job.
        await db.SaveChangesAsync();

        var result = await MetadataReviewEndpoints.ApplyProposalsAsync(db, "year", value: null);

        Assert.Equal((0, 1, 0), result);
    }

    [Fact]
    public async Task AcceptField_NothingPendingForTheField_ReturnsNull()
    {
        using var db = NewDb();
        var a = AddSong(db, "A");
        AddProposal(db, a, "genre", "worship");
        AddProposal(db, a, "year", "1985", status: "accepted");
        await db.SaveChangesAsync();

        Assert.Null(await MetadataReviewEndpoints.ApplyProposalsAsync(db, "year", value: null));
    }

    [Fact]
    public async Task AcceptCategory_StillScopedToOneValue()
    {
        using var db = NewDb();
        var a = AddSong(db, "A");
        var b = AddSong(db, "B");
        AddProposal(db, a, "year", "1985");
        AddProposal(db, b, "year", "2001");
        await db.SaveChangesAsync();

        var result = await MetadataReviewEndpoints.ApplyProposalsAsync(db, "year", "1985");

        Assert.Equal((1, 0, 1), result);
        Assert.All(
            await db.MetadataProposals.Where(p => p.ProposedValue == "2001").ToListAsync(),
            p => Assert.Equal("pending", p.Status));
    }

    [Fact]
    public async Task AcceptField_GospelGenres_ApplyIntoTheSingleWorshipBucket()
    {
        // ST-71 × ST-72: a whole-field genre accept applies legacy "gospel" proposals as "worship".
        using var db = NewDb();
        var a = AddSong(db, "A");
        var b = AddSong(db, "B");
        AddProposal(db, a, "genre", "gospel");
        AddProposal(db, b, "genre", "hymn");
        await db.SaveChangesAsync();

        var result = await MetadataReviewEndpoints.ApplyProposalsAsync(db, "genre", value: null);

        Assert.Equal(2, result!.Value.Applied);
        Assert.Equal("worship", (await db.Songs.FirstAsync(s => s.Id == a.Id)).Genre);
        Assert.Equal("worship", (await db.Songs.FirstAsync(s => s.Id == b.Id)).Genre);
    }
}
