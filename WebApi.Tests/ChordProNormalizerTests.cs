using WebApi.Services;

namespace WebApi.Tests;

public class ChordProNormalizerTests
{
    // --- Shorthand headers ------------------------------------------------------

    [Fact]
    public void Normalize_ShorthandTitle_BecomesFullForm()
    {
        Assert.Equal("{title: Africa}", ChordProNormalizer.Normalize("{t:Africa}"));
    }

    [Fact]
    public void Normalize_ShorthandArtistKeyComment_BecomeFullForms()
    {
        var result = ChordProNormalizer.Normalize("""
            {a: Toto}
            {k: E minor}
            {c: practice version}
            """);
        Assert.Equal("""
            {artist: Toto}
            {key: E minor}
            {comment: practice version}
            """, result);
    }

    [Fact]
    public void Normalize_NumericT_LeftAlone_LikelyTempo()
    {
        // A purely numeric {t:...} is most likely a tempo — the app has no tempo field, so it
        // stays as written (and renders as an ignored directive) rather than becoming a title.
        Assert.Equal("{t:120}", ChordProNormalizer.Normalize("{t:120}"));
    }

    [Fact]
    public void Normalize_EmptyShorthandValue_LeftAlone()
    {
        // {t:} has no value — nothing to promote.
        Assert.Equal("{t:}", ChordProNormalizer.Normalize("{t:}"));
    }

    // --- Section aliases ---------------------------------------------------------

    [Fact]
    public void Normalize_Soc_BecomesChorus()
    {
        Assert.Equal("{chorus}", ChordProNormalizer.Normalize("{soc}"));
    }

    [Fact]
    public void Normalize_CaseVariantSectionNames_Lowercased()
    {
        var result = ChordProNormalizer.Normalize("""
            {Verse}
            {Chorus}
            {Bridge}
            """);
        Assert.Equal("""
            {verse}
            {chorus}
            {bridge}
            """, result);
    }

    [Fact]
    public void Normalize_UntypedSectionNames_BecomeCommentHeadings()
    {
        var result = ChordProNormalizer.Normalize("""
            {intro}
            {solo}
            {pre-chorus}
            """);
        Assert.Equal("""
            {comment: Intro}
            {comment: Solo}
            {comment: Pre-Chorus}
            """, result);
    }

    // --- Bare heading lines ------------------------------------------------------

    [Fact]
    public void Normalize_BareHeadingsWithColonOrPeriod_BecomeTypedDirectives()
    {
        var result = ChordProNormalizer.Normalize("""
            Verse1:
            Chorus:
            Bridge.
            """);
        Assert.Equal("""
            {verse label="Verse 1"}
            {chorus}
            {bridge}
            """, result);
    }

    [Fact]
    public void Normalize_ModifiedHeading_KeepsLabel()
    {
        Assert.Equal("{chorus label=\"Final Chorus\"}", ChordProNormalizer.Normalize("Final Chorus:"));
    }

    [Fact]
    public void Normalize_UntypedBareHeading_BecomesCommentHeading()
    {
        Assert.Equal("{comment: Intro}", ChordProNormalizer.Normalize("Intro:"));
    }

    // --- Safety ------------------------------------------------------------------

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var input = """
            {t:Africa}
            {a: Toto}
            Verse1:
            Hello [C] world
            Chorus:
            Sing [Em] along
            {soc}
            """;
        var once = ChordProNormalizer.Normalize(input);
        Assert.Equal(once, ChordProNormalizer.Normalize(once));
    }

    [Fact]
    public void Normalize_LyricLinesAndUnknownDirectives_Untouched()
    {
        var input = """
            Hello [C] world {t:inline}
            {unknown: thing}
                indented lyric line
            """;
        Assert.Equal(input, ChordProNormalizer.Normalize(input));
    }

    // --- End to end ---------------------------------------------------------------

    [Fact]
    public void Normalize_ThenParse_FullSong_ResolvesMetadataAndSections()
    {
        var input = """
            {t:Africa}
            {a: Toto}
            {k:E minor}
            {c: live version}

            Verse1:
            I [Em] send you a picture
            Of [C] my life

            Chorus:
            Africa [G] I [Em] hold your [C] land in my mind
            """;

        var song = ChordProParser.Parse(ChordProNormalizer.Normalize(input));

        Assert.Equal("Africa", song.Header?.Title);
        Assert.Equal("Toto", song.Header?.Artist);
        Assert.Equal("E minor", song.Header?.Key);

        // comment heading, labeled verse 1, synthesized chorus label.
        var labels = song.Sections.Select(s => s.Label).ToList();
        Assert.Contains("live version", labels);
        Assert.Contains("Verse 1", labels);
        Assert.Contains("Chorus", labels);

        var chorus = song.Sections.Single(s => s.Type == "chorus");
        Assert.Single(chorus.Lines);
    }

    // --- ST-60: case-sensitive shorthands, tempo forms, numbered headings -------

    [Fact]
    public void Normalize_UppercaseCShorthand_BecomesCapo()
    {
        // ST-60 #4: {C: 3} is the ChordPro-64 capo shorthand — the old case-insensitive map
        // rewrote it into {comment: 3}, destroying a stored capo.
        Assert.Equal("{capo: 3}", ChordProNormalizer.Normalize("{C: 3}"));
    }

    [Fact]
    public void Normalize_LowercaseCShorthand_StillComment()
    {
        Assert.Equal("{comment: note}", ChordProNormalizer.Normalize("{c: note}"));
    }

    [Theory]
    [InlineData("{t:4/4}")]
    [InlineData("{t:120 bpm}")]
    [InlineData("{t:120}")]
    [InlineData("{t:92.}")]
    public void Normalize_TempoFormT_LeftAsWritten(string directive)
    {
        // ST-60 #5: the all-digit guard missed meter/BPM forms — import titled songs "4/4".
        Assert.Equal(directive, ChordProNormalizer.Normalize(directive));
    }

    [Fact]
    public void Normalize_RealTitleT_StillRewritten()
    {
        Assert.Equal("{title: Africa}", ChordProNormalizer.Normalize("{t:Africa}"));
        Assert.Equal("{title: 4/4 Time}", ChordProNormalizer.Normalize("{t:4/4 Time}"));
    }

    [Theory]
    [InlineData("Chorus 2", "{chorus label=\"Chorus 2\"}")]
    [InlineData("Second Final Chorus", "{chorus label=\"Second Final Chorus\"}")]
    [InlineData("Bridge 3", "{bridge label=\"Bridge 3\"}")]
    [InlineData("Verse 2a", "{verse label=\"Verse 2\"}")]
    public void Normalize_NumberedBareHeadings(string line, string expected)
    {
        // ST-60 #11: BareHeading widens with the parser's SectionHeading rule.
        Assert.Equal(expected, ChordProNormalizer.Normalize(line));
    }
}
