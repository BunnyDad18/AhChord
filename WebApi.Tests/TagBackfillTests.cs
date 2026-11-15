using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-106 S2: the tag backfill — artist-string splitting, genre title-casing,
/// collections retiring into setlist tags, and keys re-derived from chords.
/// Fixtures mirror the real library's shapes (5-artist worship songs, mixed-case
/// genres, dated worship collections). Runs on the InMemory store (AHC-7).
/// </summary>
public class TagBackfillTests
{
    private static ApplicationDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static Song NewSong(string title, string? artist, string? genre, string chords, string? key = "C")
        => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Artist = artist,
            Genre = genre,
            Key = key,
            ChordProText = chords,
        };

    private const string GChart = """
        {start_of_verse}
        [G]A-maz-ing [G]Grace,
        [D]how [G]sweet
        the [G]sound!
        {end_of_verse}
        {start_of_chorus}
        [G] [C] [D] [G]
        {end_of_chorus}
        """;

    // --- ArtistSplitter -------------------------------------------------------

    [Fact]
    public void Split_BuildMyLife_FiveArtists()
    {
        var names = ArtistSplitter.Split("Brett Younker, Karl Martin, Kirby Kaple, Matt Redman, Pat Barrett");

        Assert.Equal(["Brett Younker", "Karl Martin", "Kirby Kaple", "Matt Redman", "Pat Barrett"], names);
    }

    [Fact]
    public void Split_BandNamesWithAmpersand_StayOneChip()
    {
        // " & " is a band name as often as a duo — never auto-split (editor tool covers it).
        Assert.Equal(["Bryan & Katie Torwalt"], ArtistSplitter.Split("Bryan & Katie Torwalt"));
        Assert.Equal(["The Mamas & The Papas"], ArtistSplitter.Split("The Mamas & The Papas"));
        Assert.Equal(["Keith Getty & Stuart Townend"], ArtistSplitter.Split("Keith Getty & Stuart Townend"));
    }

    [Fact]
    public void Split_FeaturingForms_Split()
    {
        Assert.Equal(["Matt Redman", "Charlie Puth"], ArtistSplitter.Split("Matt Redman feat. Charlie Puth"));
        Assert.Equal(["Chris Tomlin", "Generational"], ArtistSplitter.Split("Chris Tomlin featuring Generational"));
        Assert.Equal(["Adele", "Beyoncé"], ArtistSplitter.Split("Adele ft. Beyoncé"));
    }

    [Fact]
    public void Split_CreditPrefixes_Stripped()
    {
        var names = ArtistSplitter.Split("Words by Charles Wesley, Music by Felix Mendelssohn-Bartholdy");

        Assert.Equal(["Charles Wesley", "Felix Mendelssohn-Bartholdy"], names);
    }

    [Fact]
    public void Split_Trims_Dedupes_EmptiesDropped()
    {
        Assert.Equal(["Adele"], ArtistSplitter.Split("Adele, adele,  , "));
        Assert.Empty(ArtistSplitter.Split("   "));
        Assert.Empty(ArtistSplitter.Split(null));
    }

    // --- Backfill --------------------------------------------------------------

    [Fact]
    public void Backfill_ArtistStrings_MattRedmanFindsBuildMyLife()
    {
        using var db = NewDb(nameof(Backfill_ArtistStrings_MattRedmanFindsBuildMyLife));
        db.Songs.AddRange(
            NewSong("Build My Life", "Brett Younker, Karl Martin, Kirby Kaple, Matt Redman, Pat Barrett", "worship", GChart),
            NewSong("Blessed Be Your Name", "Matt Redman", "worship", GChart));
        db.SaveChanges();

        TagBackfill.Run(db);

        var redman = db.Tags.Single(t => t.Kind == TagKind.Artist && t.NormalizedName == "matt redman");
        var buildMyLife = db.Songs.Single(s => s.Title == "Build My Life");
        var blessed = db.Songs.Single(s => s.Title == "Blessed Be Your Name");
        Assert.True(db.SongTagLinks.Any(l => l.SongId == buildMyLife.Id && l.TagId == redman.Id));
        Assert.True(db.SongTagLinks.Any(l => l.SongId == blessed.Id && l.TagId == redman.Id));

        // All five writers are findable; the tag row is shared, not per-song.
        Assert.Equal(5, db.Tags.Count(t => t.Kind == TagKind.Artist));
        Assert.Equal(2, db.SongTagLinks.Count(l => l.TagId == redman.Id));
    }

    [Fact]
    public void Backfill_Genres_TitleCasedIntoOneRowPerGenre()
    {
        using var db = NewDb(nameof(Backfill_Genres_TitleCasedIntoOneRowPerGenre));
        db.Songs.AddRange(
            NewSong("One", "A", "pop", GChart),
            NewSong("Two", "B", "Pop", GChart),
            NewSong("Three", "C", "POP", GChart),
            NewSong("Four", "D", "rock", GChart),
            NewSong("Five", "E", "Rock", GChart),
            NewSong("Six", "F", "pop rock", GChart),
            NewSong("Seven", "G", null, GChart));
        db.SaveChanges();

        TagBackfill.Run(db);

        var pop = db.Tags.Single(t => t.Kind == TagKind.Genre && t.NormalizedName == "pop");
        Assert.Equal("Pop", pop.Name);
        Assert.Equal(3, db.SongTagLinks.Count(l => l.TagId == pop.Id));

        var rock = db.Tags.Single(t => t.Kind == TagKind.Genre && t.NormalizedName == "rock");
        Assert.Equal("Rock", rock.Name);
        Assert.Equal(2, db.SongTagLinks.Count(l => l.TagId == rock.Id));

        // "pop rock" is one genre, retitled — not split.
        var popRock = db.Tags.Single(t => t.Kind == TagKind.Genre && t.NormalizedName == "pop rock");
        Assert.Equal("Pop Rock", popRock.Name);
    }

    [Fact]
    public void Backfill_Collections_BecomeSetlistTags_CountsMatch()
    {
        using var db = NewDb(nameof(Backfill_Collections_BecomeSetlistTags_CountsMatch));
        var collection = new Collection { Id = Guid.NewGuid(), Name = "Sunday 5 Nov", Category = "Worship" };
        var inA = new Playlist { Id = Guid.NewGuid(), Name = "Worship 5 Nov", CollectionId = collection.Id };
        var inB = new Playlist { Id = Guid.NewGuid(), Name = "Worship 12 Nov", CollectionId = collection.Id };
        var lone = new Playlist { Id = Guid.NewGuid(), Name = "Youth Night" };
        db.Collections.Add(collection);
        db.Playlists.AddRange(inA, inB, lone);
        db.SaveChanges();

        TagBackfill.Run(db);

        var tag = db.Tags.Single(t => t.Kind == TagKind.Tag && t.NormalizedName == "sunday 5 nov");
        Assert.Equal("Sunday 5 Nov", tag.Name);
        Assert.Equal(2, db.PlaylistTagLinks.Count(l => l.TagId == tag.Id));
        Assert.Contains(db.PlaylistTagLinks, l => l.PlaylistId == inA.Id && l.TagId == tag.Id);
        Assert.Contains(db.PlaylistTagLinks, l => l.PlaylistId == inB.Id && l.TagId == tag.Id);
        Assert.DoesNotContain(db.PlaylistTagLinks, l => l.PlaylistId == lone.Id);

        // The retirement gate: every old collection is findable as a tag, same membership.
        foreach (var c in db.Collections)
        {
            var t = db.Tags.Single(x => x.Kind == TagKind.Tag && x.NormalizedName == c.Name.Trim().ToLowerInvariant());
            Assert.Equal(
                db.Playlists.Count(p => p.CollectionId == c.Id),
                db.PlaylistTagLinks.Count(l => l.TagId == t.Id));
        }
    }

    [Fact]
    public void Backfill_KeyDerivedFromChords_OverwritesMetadata_KeepsKeyWhenNoChords()
    {
        using var db = NewDb(nameof(Backfill_KeyDerivedFromChords_OverwritesMetadata_KeepsKeyWhenNoChords));
        var withChords = NewSong("Amazing Grace", "Traditional", "worship", GChart, key: "C");
        var lyricsOnly = NewSong("Lyric Only", "Traditional", null, "{start_of_verse}\nJust words.\n{end_of_verse}", key: "Eb");
        db.Songs.AddRange(withChords, lyricsOnly);
        db.SaveChanges();

        TagBackfill.Run(db);

        Assert.Equal("G", withChords.Key); // chords win over the metadata "C"
        Assert.Equal("Eb", lyricsOnly.Key); // nothing to derive from — key kept
    }

    [Fact]
    public void Backfill_Idempotent_SecondRunWritesNothing()
    {
        using var db = NewDb(nameof(Backfill_Idempotent_SecondRunWritesNothing));
        var collection = new Collection { Id = Guid.NewGuid(), Name = "Sunday 5 Nov", Category = "Worship" };
        db.Collections.Add(collection);
        db.Playlists.Add(new Playlist { Id = Guid.NewGuid(), Name = "W1", CollectionId = collection.Id });
        db.Songs.Add(NewSong("Build My Life", "Brett Younker, Matt Redman", "pop", GChart));
        db.SaveChanges();

        TagBackfill.Run(db);
        var tags = db.Tags.Count();
        var songLinks = db.SongTagLinks.Count();
        var playlistLinks = db.PlaylistTagLinks.Count();

        TagBackfill.Run(db);

        Assert.Equal(tags, db.Tags.Count());
        Assert.Equal(songLinks, db.SongTagLinks.Count());
        Assert.Equal(playlistLinks, db.PlaylistTagLinks.Count());
        Assert.NotNull(db.AppMeta.Find(TagBackfill.MarkerKey));
    }

    [Fact]
    public void Backfill_MarkerPresent_DoesNotResurrectDeletedTags()
    {
        using var db = NewDb(nameof(Backfill_MarkerPresent_DoesNotResurrectDeletedTags));
        db.Songs.Add(NewSong("One", "Matt Redman", "pop", GChart));
        db.SaveChanges();
        TagBackfill.Run(db);

        // User deletes the tag; a later boot with the marker set must not re-run the pass.
        var pop = db.Tags.Single(t => t.NormalizedName == "pop");
        db.SongTagLinks.RemoveRange(db.SongTagLinks.Where(l => l.TagId == pop.Id));
        db.Tags.Remove(pop);
        db.SaveChanges();

        TagBackfill.Run(db);

        Assert.DoesNotContain(db.Tags, t => t.NormalizedName == "pop");
    }
}
