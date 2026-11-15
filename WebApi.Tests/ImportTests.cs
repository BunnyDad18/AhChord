using WebApi.Services;

namespace WebApi.Tests;

public class ImportTests
{
    // --- IsChordPro -------------------------------------------------------------

    [Fact]
    public void IsChordPro_WithLinesAndChords_True()
    {
        var parsed = ChordProParser.Parse("{title: X}\n[G] Hello [D] world");
        Assert.True(ImportService.IsChordPro(parsed));
    }

    [Fact]
    public void IsChordPro_OnlyDirectives_NoLines_False()
    {
        var parsed = ChordProParser.Parse("{title: X}\n{artist: Y}\n{key: G}");
        Assert.False(ImportService.IsChordPro(parsed));
    }

    [Fact]
    public void IsChordPro_LinesWithoutChords_False()
    {
        var parsed = ChordProParser.Parse("{title: X}\njust some plain lyrics\nno chords here");
        Assert.False(ImportService.IsChordPro(parsed));
    }

    [Fact]
    public void IsChordPro_EmptyInput_False()
    {
        Assert.False(ImportService.IsChordPro(ChordProParser.Parse(null)));
        Assert.False(ImportService.IsChordPro(ChordProParser.Parse("   ")));
        Assert.False(ImportService.IsChordPro(null));
    }

    [Fact]
    public void IsChordPro_ChordOnlyOnLastLine_True()
    {
        var parsed = ChordProParser.Parse("plain line\nanother line\n[C] one chord at the end");
        Assert.True(ImportService.IsChordPro(parsed));
    }

    // --- ResolveMetadata --------------------------------------------------------

    [Fact]
    public void ResolveMetadata_FullHeader_ExtractsAllFields()
    {
        var parsed = ChordProParser.Parse("{title: Hello Sun}\n{artist: Test Band}\n{key: G}\n{capo: 3}\n{genre: folk}\n{year: 1969}\n\n[C] Hello");
        var (title, artist, key, capo, genre, year) = ImportService.ResolveMetadata(null, null, header: parsed.Header);

        Assert.Equal("Hello Sun", title);
        Assert.Equal("Test Band", artist);
        Assert.Equal("G", key);
        Assert.Equal(3, capo);
        Assert.Equal("folk", genre);
        Assert.Equal(1969, year);
    }

    [Fact]
    public void ResolveMetadata_GenreYear_BodyOverridesHeader_AndInvalidYearIsNull()
    {
        var parsed = ChordProParser.Parse("{genre: header genre}\n{year: 999}\n\n[C] x");

        // Body overrides win; a valid body year is used as-is.
        var (_, _, _, _, genre, year) = ImportService.ResolveMetadata(null, null, "body genre", 2024, parsed.Header);
        Assert.Equal("body genre", genre);
        Assert.Equal(2024, year);

        // Without overrides: header genre is used; an out-of-range year falls back to null.
        var (_, _, _, _, genre2, year2) = ImportService.ResolveMetadata(null, null, header: parsed.Header);
        Assert.Equal("header genre", genre2);
        Assert.Null(year2);
    }

    [Fact]
    public void ResolveMetadata_BodyOverridesHeader()
    {
        var parsed = ChordProParser.Parse("{title: Header Title}\n{artist: Header Artist}\n\n[C] x");
        var (title, artist, _, _, _, _) = ImportService.ResolveMetadata("Body Title", "Body Artist", header: parsed.Header);

        Assert.Equal("Body Title", title);
        Assert.Equal("Body Artist", artist);
    }

    [Fact]
    public void ResolveMetadata_BlankBodyFallsBackToHeader()
    {
        var parsed = ChordProParser.Parse("{title: Header Title}\n{artist: Header Artist}\n\n[C] x");
        var (title, artist, _, _, _, _) = ImportService.ResolveMetadata("   ", null, header: parsed.Header);

        Assert.Equal("Header Title", title);
        Assert.Equal("Header Artist", artist);
    }

    [Fact]
    public void ResolveMetadata_NoHeader_UsesBodyOnly()
    {
        var parsed = ChordProParser.Parse("[C] just a line with a chord");
        var (title, artist, key, capo, _, _) = ImportService.ResolveMetadata("Body Title", "Body Artist", header: parsed.Header);

        Assert.Equal("Body Title", title);
        Assert.Equal("Body Artist", artist);
        Assert.Null(key);
        Assert.Equal(0, capo);
    }

    [Fact]
    public void ResolveMetadata_NoTitleAnywhere_Null()
    {
        var parsed = ChordProParser.Parse("[C] just a line with a chord");
        var (title, _, _, _, _, _) = ImportService.ResolveMetadata(null, null, header: parsed.Header);
        Assert.Null(title);
    }

    [Theory]
    [InlineData("7", 6)]   // above range -> clamp down
    [InlineData("-2", 0)]  // below range -> clamp up
    [InlineData("0", 0)]   // as written
    [InlineData("2", 2)]   // in range
    [InlineData("abc", 0)] // unparseable -> as written
    public void ResolveMetadata_CapoVariants_ClampedOrZero(string capoValue, int expected)
    {
        var parsed = ChordProParser.Parse($"{{capo: {capoValue}}}\n\n[C] x");
        var (_, _, _, capo, _, _) = ImportService.ResolveMetadata(null, null, header: parsed.Header);
        Assert.Equal(expected, capo);
    }
}
