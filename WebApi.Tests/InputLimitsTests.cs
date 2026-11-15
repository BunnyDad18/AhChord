using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Endpoints;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-63 (finding 5): bounded user-supplied text. InputLimits is the shared cap set; the
/// song endpoints, CreateSongAsync (the import chokepoint) and DisplayName all consult it.
/// Unbounded text is storage DoS with a runtime tail — ChordProText is re-parsed on every
/// view and embedded in every metadata-scan prompt.
/// </summary>
public class InputLimitsTests
{
    // ---------- AddFieldLimits ----------

    [Fact]
    public void AddFieldLimits_Oversized_FlagsEachField()
    {
        var fields = new Dictionary<string, string?>();
        InputLimits.AddFieldLimits(fields,
            title: new string('t', InputLimits.MaxMetaFieldLength + 1),
            artist: new string('a', InputLimits.MaxMetaFieldLength + 1),
            album: null,
            key: new string('k', InputLimits.MaxMetaFieldLength + 1),
            genre: new string('g', InputLimits.MaxMetaFieldLength + 1),
            chordProText: new string('c', InputLimits.MaxChordProLength + 1),
            sourceUrl: new string('u', InputLimits.MaxUrlLength + 1));

        // album was sent as null (not sent) — the six oversized fields are flagged.
        Assert.Equal(
            new[] { "artist", "chordProText", "genre", "key", "sourceUrl", "title" },
            fields.Keys.OrderBy(x => x));
    }

    [Fact]
    public void AddFieldLimits_AtLimitAndNull_Pass()
    {
        var fields = new Dictionary<string, string?>();
        InputLimits.AddFieldLimits(fields,
            title: new string('t', InputLimits.MaxMetaFieldLength),  // exactly at the cap is fine
            artist: null,                                            // null = not sent (PATCH)
            album: "",
            key: "C",
            genre: null,
            chordProText: "{title: X}\n[C] hello",
            sourceUrl: "https://example.com/tab");

        Assert.Empty(fields);
    }

    [Fact]
    public void AddFieldLimits_PaddingCountsAfterTrim()
    {
        var fields = new Dictionary<string, string?>();
        InputLimits.AddFieldLimits(fields,
            title: "  " + new string('t', InputLimits.MaxMetaFieldLength + 1) + "  ",
            artist: "  C  ", album: null, key: null, genre: null, chordProText: null, sourceUrl: null);

        Assert.Equal(new[] { "title" }, fields.Keys);
    }

    // ---------- CreateSongAsync (the import chokepoint) ----------

    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task CreateSongAsync_OversizedChordPro_Throws400BeforeParsing()
    {
        var db = NewDb();
        var ex = await Assert.ThrowsAsync<ImportException>(() =>
            ImportService.CreateSongAsync(db, "[C] " + new string('x', InputLimits.MaxChordProLength), null, null));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("validation-failed", ex.Code);
        Assert.True(ex.Fields!.ContainsKey("chordProText"));
    }

    [Fact]
    public async Task CreateSongAsync_OversizedTitle_Throws400()
    {
        var db = NewDb();
        var ex = await Assert.ThrowsAsync<ImportException>(() =>
            ImportService.CreateSongAsync(db, "{artist: A}\n[C] hello", new string('t', InputLimits.MaxMetaFieldLength + 1), null));

        Assert.Equal(400, ex.StatusCode);
        Assert.True(ex.Fields!.ContainsKey("title"));
    }

    [Fact]
    public async Task CreateSongAsync_OversizedSourceUrl_Throws400()
    {
        var db = NewDb();
        var ex = await Assert.ThrowsAsync<ImportException>(() =>
            ImportService.CreateSongAsync(db, "[C] hello", "T", "A", null, null, null, null,
                "https://x.test/" + new string('u', InputLimits.MaxUrlLength)));

        Assert.Equal(400, ex.StatusCode);
        Assert.True(ex.Fields!.ContainsKey("sourceUrl"));
    }

    [Fact]
    public async Task CreateSongAsync_NormalSizes_Unaffected()
    {
        var db = NewDb();
        var (song, duplicate) = await ImportService.CreateSongAsync(db,
            "{title: Bounded}\n{artist: A}\n[C] hello [G] world", null, null);

        Assert.False(duplicate);
        Assert.Equal("Bounded", song.Title);
    }

    // ---------- DisplayName (register + profile) ----------

    [Fact]
    public void CleanDisplayName_Trims_BlanksToNull_ClipsToCap()
    {
        Assert.Null(AuthEndpoints.CleanDisplayName(null));
        Assert.Null(AuthEndpoints.CleanDisplayName("   "));
        Assert.Equal("James", AuthEndpoints.CleanDisplayName("  James  "));
        Assert.Equal(new string('x', AuthEndpoints.MaxDisplayNameLength),
            AuthEndpoints.CleanDisplayName(new string('x', 500)));
    }
}
