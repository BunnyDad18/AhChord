using WebApi.Services;

namespace WebApi.Tests;

public class ChordProParserTests
{
    // --- Header ---------------------------------------------------------------

    [Fact]
    public void Parse_BasicSong_ExtractsHeader()
    {
        var song = ChordProParser.Parse("""
            {title: Test}
            {artist: Tester}
            {key: C}

            {start_of_verse}
            Hello [C] world
            {end_of_verse}
            """);

        Assert.Equal("Test", song.Header?.Title);
        Assert.Equal("Tester", song.Header?.Artist);
        Assert.Equal("C", song.Header?.Key);
    }

    [Fact]
    public void Parse_CapoTimeTag_Captured()
    {
        var song = ChordProParser.Parse("""
            {title: T}
            {capo: 3}
            {time: 4/4}
            {tag: folk}
            """);

        Assert.Equal("3", song.Header?.Capo);
        Assert.Equal("4/4", song.Header?.Time);
        Assert.Equal("folk", song.Header?.Tag);
    }

    [Fact]
    public void Parse_GenreYear_Captured()
    {
        var song = ChordProParser.Parse("""
            {title: T}
            {genre: folk}
            {year: 1969}
            """);

        Assert.Equal("folk", song.Header?.Genre);
        Assert.Equal("1969", song.Header?.Year);
    }

    [Fact]
    public void Parse_MetaFields_Captured()
    {
        var song = ChordProParser.Parse("""
            {title: T}
            {meta: tuning E A D G B e}
            {meta: src https://tabs.example.com/tab/test}
            """);

        var meta = song.Header?.Meta;
        Assert.NotNull(meta);
        Assert.Equal(2, meta!.Count);
        Assert.Equal("tuning", meta[0].Name);
        Assert.Equal("E A D G B e", meta[0].Value);
        Assert.Equal("src", meta[1].Name);
        Assert.Equal("https://tabs.example.com/tab/test", meta[1].Value);
    }

    [Fact]
    public void Parse_HeaderOnly_NoSections()
    {
        var song = ChordProParser.Parse("""
            {title: Solo}
            {artist: Lone}
            {key: G}
            """);

        Assert.NotNull(song.Header);
        Assert.Empty(song.Sections);
        Assert.NotEmpty(song.Warnings);
    }

    // --- Sections -------------------------------------------------------------

    [Fact]
    public void Parse_MultiSection_Ordered()
    {
        var song = ChordProParser.Parse("""
            {title: Multi}
            {key: C}

            {start_of_verse label="Verse 1"}
            [C] one
            {end_of_verse}

            {start_of_chorus label="Chorus"}
            [C] two
            {end_of_chorus}

            {start_of_bridge label="Bridge"}
            [C] three
            {end_of_bridge}
            """);

        Assert.Equal(3, song.Sections.Count);
        Assert.Equal("verse", song.Sections[0].Type);
        Assert.Equal("chorus", song.Sections[1].Type);
        Assert.Equal("bridge", song.Sections[2].Type);
        Assert.Equal("Verse 1", song.Sections[0].Label);
        Assert.Equal("Chorus", song.Sections[1].Label);
        Assert.Equal("Bridge", song.Sections[2].Label);
    }

    [Fact]
    public void Parse_RepeatedSectionLabels_Preserved()
    {
        var song = ChordProParser.Parse("""
            {start_of_verse label="Verse 1"}
            [C] a
            {end_of_verse}

            {start_of_verse label="Verse 2"}
            [C] b
            {end_of_verse}
            """);

        Assert.Equal(2, song.Sections.Count);
        Assert.Equal("Verse 1", song.Sections[0].Label);
        Assert.Equal("Verse 2", song.Sections[1].Label);
    }

    [Fact]
    public void Parse_TextBeforeFirstSection_BecomesOtherSection()
    {
        var song = ChordProParser.Parse("""
            Intro lyrics [C] here
            {start_of_verse}
            [C] verse
            {end_of_verse}
            """);

        Assert.Equal(2, song.Sections.Count);
        Assert.Equal("other", song.Sections[0].Type);
        Assert.Null(song.Sections[0].Label);
        Assert.Single(song.Sections[0].Lines);
        Assert.Equal("verse", song.Sections[1].Type);
    }

    [Fact]
    public void Parse_CommentLine_BecomesOtherSectionMarker()
    {
        var song = ChordProParser.Parse("""
            {comment: Interlude: Am F G}
            {start_of_verse}
            [C] x
            {end_of_verse}
            """);

        Assert.Equal(2, song.Sections.Count);
        Assert.Equal("other", song.Sections[0].Type);
        Assert.Equal("Interlude: Am F G", song.Sections[0].Label);
        Assert.Empty(song.Sections[0].Lines);
    }

    // --- ST-41: comment headings + synthesized labels -------------------------

    [Fact]
    public void Parse_UnlabeledSections_GetSynthesizedLabels()
    {
        var song = ChordProParser.Parse("""
            {start_of_verse}
            [C] a
            {end_of_verse}

            {start_of_chorus}
            [C] b
            {end_of_chorus}

            {start_of_verse}
            [C] c
            {end_of_verse}
            """);

        Assert.Equal("Verse 1", song.Sections[0].Label);
        Assert.Equal("Chorus", song.Sections[1].Label);
        Assert.Equal("Verse 2", song.Sections[2].Label);
    }

    [Fact]
    public void Parse_MixedLabeledUnlabeledVerses_NumberOnlyUnlabeled()
    {
        var song = ChordProParser.Parse("""
            {start_of_verse label="Intro"}
            [C] a
            {end_of_verse}

            {start_of_verse}
            [C] b
            {end_of_verse}
            """);

        Assert.Equal("Intro", song.Sections[0].Label);
        Assert.Equal("Verse 1", song.Sections[1].Label);
    }

    [Fact]
    public void Parse_CommentHeadings_LinesAttachInPlace()
    {
        var song = ChordProParser.Parse("""
            {title: T}
            {key: C}

            {comment: Verse 1}
            [C] a
            [G] b

            {comment: Chorus}
            [C] c
            """);

        // Each comment starts its own section; the lines follow it in place.
        Assert.Equal(2, song.Sections.Count);
        Assert.Equal("other", song.Sections[0].Type);
        Assert.Equal("Verse 1", song.Sections[0].Label);
        Assert.Equal(2, song.Sections[0].Lines.Count);
        Assert.Equal("Chorus", song.Sections[1].Label);
        Assert.Single(song.Sections[1].Lines);
    }

    [Fact]
    public void Parse_CommentThenTypedSection_ThenTrailingLines_Ordered()
    {
        var song = ChordProParser.Parse("""
            {comment: Intro}
            [C] i

            {start_of_verse label="Verse 1"}
            [C] v
            {end_of_verse}

            [G] outro line
            """);

        Assert.Equal(3, song.Sections.Count);
        Assert.Equal("Intro", song.Sections[0].Label);
        Assert.Single(song.Sections[0].Lines);
        Assert.Equal("Verse 1", song.Sections[1].Label);
        // Trailing lines after {end_of_verse} form a new unlabeled "other" section.
        Assert.Null(song.Sections[2].Label);
        Assert.Single(song.Sections[2].Lines);
    }

    [Fact]
    public void Parse_BareHeadingLines_StartSectionsInPlace()
    {
        var song = ChordProParser.Parse("""
            Verse 1
            [C] a
            [G] b

            Chorus
            [C] c
            """);

        Assert.Equal(2, song.Sections.Count);
        Assert.Equal("Verse 1", song.Sections[0].Label);
        Assert.Equal(2, song.Sections[0].Lines.Count);
        Assert.Equal("Chorus", song.Sections[1].Label);
        Assert.Single(song.Sections[1].Lines);
    }

    [Fact]
    public void Parse_BareHeadingWithModifier_Matched()
    {
        var song = ChordProParser.Parse("""
            Final Chorus
            [C] a
            """);

        Assert.Single(song.Sections);
        Assert.Equal("Final Chorus", song.Sections[0].Label);
        Assert.Single(song.Sections[0].Lines);
    }

    [Fact]
    public void Parse_HeadingLineWithChord_StaysLyric()
    {
        var song = ChordProParser.Parse("[C] Chorus");

        // A line carrying chords is a lyric, not a heading.
        Assert.Single(song.Sections);
        Assert.Null(song.Sections[0].Label);
        Assert.Single(song.Sections[0].Lines);
    }

    [Fact]
    public void Parse_MissingEndOf_Recover()
    {
        var song = ChordProParser.Parse("""
            {start_of_verse}
            [C] a
            [C] b
            {start_of_chorus}
            [C] c
            """);

        // A new section start closes the previous one; nothing is lost or thrown.
        Assert.Equal(2, song.Sections.Count);
        Assert.Equal(2, song.Sections[0].Lines.Count);
        Assert.Single(song.Sections[1].Lines);
    }

    [Fact]
    public void Parse_UnknownDirective_IgnoredGracefully()
    {
        var song = ChordProParser.Parse("""
            {unknown_directive: hello}
            {title: X}
            {start_of_verse}
            [C] a
            {end_of_verse}
            {another: thing}
            """);

        Assert.Equal("X", song.Header?.Title);
        Assert.Single(song.Sections);
        Assert.Equal("verse", song.Sections[0].Type);
    }

    // --- Lines & chords -------------------------------------------------------

    [Fact]
    public void Parse_Chords_CaptureCharIndex()
    {
        var line = "When I [F]find myself [G]out of [C]time";
        var song = ChordProParser.Parse($"{{start_of_verse}}\n{line}\n{{end_of_verse}}");

        var chords = song.Sections[0].Lines[0].Chords;
        Assert.Equal(3, chords.Count);
        Assert.Equal("F", chords[0].Name);
        Assert.Equal(7, chords[0].CharIndex);
        Assert.Equal("G", chords[1].Name);
        Assert.Equal(22, chords[1].CharIndex);
        Assert.Equal("C", chords[2].Name);
        Assert.Equal(32, chords[2].CharIndex);
        // charIndex points at the '[' in the preserved text.
        Assert.Equal('[', song.Sections[0].Lines[0].Text[7]);
    }

    [Fact]
    public void Parse_ChordAtLineStart_IndexZero()
    {
        var song = ChordProParser.Parse("{start_of_verse}\n[C] start\n{end_of_verse}");

        var chord = song.Sections[0].Lines[0].Chords[0];
        Assert.Equal("C", chord.Name);
        Assert.Equal(0, chord.CharIndex);
    }

    [Fact]
    public void Parse_SlashChord_ParsedAsWritten()
    {
        var song = ChordProParser.Parse("{start_of_verse}\nPlay [F/C] now\n{end_of_verse}");

        var chord = song.Sections[0].Lines[0].Chords[0];
        Assert.Equal("F/C", chord.Name);
    }

    [Fact]
    public void Parse_ChordQualities_ParsedAsWritten()
    {
        var song = ChordProParser.Parse("{start_of_verse}\n[Am7] [Cmaj7] [G7sus4] [F#m] [Bbm9]\n{end_of_verse}");

        var names = song.Sections[0].Lines[0].Chords.Select(c => c.Name).ToList();
        Assert.Equal(new[] { "Am7", "Cmaj7", "G7sus4", "F#m", "Bbm9" }, names);
    }

    [Fact]
    public void Parse_NonChordBrackets_Ignored()
    {
        var song = ChordProParser.Parse("{start_of_verse}\n[tab] [123] [Zz] [C]\n{end_of_verse}");

        var chords = song.Sections[0].Lines[0].Chords;
        Assert.Single(chords);
        Assert.Equal("C", chords[0].Name);
        // The non-chord brackets remain in the text.
        Assert.Contains("[tab]", song.Sections[0].Lines[0].Text);
    }

    [Fact]
    public void Parse_Annotation_RecordedAndKeptAsText()
    {
        var song = ChordProParser.Parse("{start_of_verse}\n[*N.C.] rest [*coda] go\n{end_of_verse}");

        var line = song.Sections[0].Lines[0];
        Assert.Equal(2, line.Annotations.Count);
        Assert.Equal("*N.C.", line.Annotations[0].Text);
        Assert.Equal(0, line.Annotations[0].CharIndex);
        Assert.Equal("*coda", line.Annotations[1].Text);
        Assert.Equal(13, line.Annotations[1].CharIndex);
        // Kept as text.
        Assert.Contains("[*N.C.]", line.Text);
    }

    [Fact]
    public void Parse_BarNotation_RecordedAndKeptAsText()
    {
        var song = ChordProParser.Parse("{start_of_verse}\n[||:] [C] sing [|] [G] on [:||]\n{end_of_verse}");

        var line = song.Sections[0].Lines[0];
        Assert.Equal(3, line.Bars.Count);
        Assert.Equal("repeat-start", line.Bars[0].Kind);
        Assert.Equal(0, line.Bars[0].CharIndex);
        Assert.Equal("bar", line.Bars[1].Kind);
        Assert.Equal(15, line.Bars[1].CharIndex);
        Assert.Equal("repeat-end", line.Bars[2].Kind);
        Assert.Equal(26, line.Bars[2].CharIndex);
        // Kept as text.
        Assert.Contains("[||:]", line.Text);
    }

    [Fact]
    public void Parse_NearMissBarTokens_NotBars()
    {
        var song = ChordProParser.Parse("{start_of_verse}\n[|] x [||] y [|:] z [:|] w\n{end_of_verse}");

        var line = song.Sections[0].Lines[0];
        Assert.Single(line.Bars); // only the first token is a real barline
        Assert.Equal("bar", line.Bars[0].Kind);
    }

    [Fact]
    public void Parse_NoChords_LinesStillCaptured()
    {
        var song = ChordProParser.Parse("""
            {start_of_verse}
            Just a lyric line
            Another line
            {end_of_verse}
            """);

        Assert.Equal(2, song.Sections[0].Lines.Count);
        Assert.All(song.Sections[0].Lines, l => Assert.Empty(l.Chords));
    }

    [Fact]
    public void Parse_UnicodeLyrics_Preserved()
    {
        var song = ChordProParser.Parse("{start_of_verse}\n[C] Café au lait — naïve\n{end_of_verse}");

        var line = song.Sections[0].Lines[0];
        Assert.Equal("Café au lait — naïve", line.Text.Replace("[C]", "").Trim());
        Assert.Equal("C", line.Chords[0].Name);
    }

    [Fact]
    public void Parse_LongLine_NoThrow()
    {
        var prefix = new string('a', 5000);
        var longLine = prefix + " [C] end"; // 5000 a's + one space, so '[' is at index 5001
        var song = ChordProParser.Parse($"{{start_of_verse}}\n{longLine}\n{{end_of_verse}}");

        var chord = song.Sections[0].Lines[0].Chords[0];
        Assert.Equal("C", chord.Name);
        Assert.Equal(5001, chord.CharIndex);
    }

    // --- Robustness (never throw) --------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   \t\n  ")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("[[")]
    [InlineData("]]")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("{}")]
    [InlineData("{{{")]
    [InlineData("}}}")]
    [InlineData("hello [C] world [")]
    [InlineData("%%%%")]
    [InlineData("[*]")]
    [InlineData("x")]
    public void Parse_Garbage_NeverThrows(string input)
    {
        var ex = Record.Exception(() => ChordProParser.Parse(input));
        Assert.Null(ex);
    }

    [Fact]
    public void Parse_Null_ReturnsEmptyWithWarning()
    {
        var song = ChordProParser.Parse(null);
        Assert.Empty(song.Sections);
        Assert.NotEmpty(song.Warnings);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyWithWarning()
    {
        var song = ChordProParser.Parse("   ");
        Assert.Empty(song.Sections);
        Assert.Contains("Input is empty.", song.Warnings);
    }

    // --- Canonical format -----------------------------------------------------

    [Fact]
    public void Parse_CanonicalConversionExample_Parses()
    {
        // The mini example from docs/prompts/chord-page-to-chordpro.md.
        var song = ChordProParser.Parse("""
            {title: Hello Sun}
            {artist: Test Band}
            {key: G}
            {capo: 3}
            {tag: folk}
            {meta: src https://tabs.example.com/tab/test-band/hello-sun-chords-1234567}

            [C] Hello [C] sun, the [Em] light is [D] warm
            [C] Hold my [Em] hand till [D] mornin' [C] light
            """);

        Assert.Equal("Hello Sun", song.Header?.Title);
        Assert.Equal("Test Band", song.Header?.Artist);
        Assert.Equal("G", song.Header?.Key);
        Assert.Equal("3", song.Header?.Capo);
        Assert.Equal("folk", song.Header?.Tag);
        Assert.Single(song.Header?.Meta ?? new());
        Assert.Equal("src", song.Header!.Meta[0].Name);

        // The lyric lines appear before any {start_of_*}, so they form one "other" section.
        Assert.Single(song.Sections);
        Assert.Equal("other", song.Sections[0].Type);
        Assert.Equal(2, song.Sections[0].Lines.Count);

        var firstLine = song.Sections[0].Lines[0];
        Assert.Equal("C", firstLine.Chords[0].Name);
        Assert.Equal(0, firstLine.Chords[0].CharIndex);
        Assert.Contains("Em", firstLine.Chords.Select(c => c.Name));
        Assert.Contains("D", firstLine.Chords.Select(c => c.Name));
    }

    // --- ST-48: short section names + colon-tolerant bare headings ----------------

    [Fact]
    public void Parse_ShortSectionNames_TypedSections()
    {
        var song = ChordProParser.Parse("""
            {verse}
            Line one

            {chorus}
            Line two

            {bridge}
            Line three
            """);

        Assert.Equal(3, song.Sections.Count);
        Assert.Equal("verse", song.Sections[0].Type);
        Assert.Equal("Verse 1", song.Sections[0].Label); // synthesized (numbered)
        Assert.Equal("chorus", song.Sections[1].Type);
        Assert.Equal("Chorus", song.Sections[1].Label);
        Assert.Equal("bridge", song.Sections[2].Type);
        Assert.Equal("Bridge", song.Sections[2].Label);
    }

    [Fact]
    public void Parse_BareHeadingWithColon_IsSectionWithoutColon()
    {
        var song = ChordProParser.Parse("""
            Verse1:
            Line one

            Chorus.
            Line two
            """);

        Assert.Equal(2, song.Sections.Count);
        Assert.Equal("other", song.Sections[0].Type);
        Assert.Equal("Verse1", song.Sections[0].Label); // trailing colon stripped
        Assert.Equal("Chorus", song.Sections[1].Label); // trailing period stripped
    }

    [Fact]
    public void Parse_LegacyShorthandSections_Typed()
    {
        // Regression (bug report 2026-10-19): pre-ST-48 charts use {sov}/{soc}/{sob}; the parser
        // must treat them as section markers instead of rendering them literally.
        var song = ChordProParser.Parse("""
            {sov}
            Line one

            {soc}
            Line two

            {sob}
            Line three
            """);

        Assert.Equal(3, song.Sections.Count);
        Assert.Equal("verse", song.Sections[0].Type);
        Assert.Equal("chorus", song.Sections[1].Type);
        Assert.Equal("bridge", song.Sections[2].Type);
    }

    [Fact]
    public void Parse_DirectiveSharingLineWithLyrics_SectionAndTextBothParsed()
    {
        // Regression (bug report 2026-10-19, "chords don't look right"): pre-ST-48 charts put a
        // section marker and the first lyric on one line ({soc}[Gbm]Gonna...). The marker must be
        // processed as a section start and the rest parsed as a normal lyric line.
        var song = ChordProParser.Parse("""
            {soc}[Gbm]Gonna take a [D]lot to drag me away,
            Second line
            """);

        var section = Assert.Single(song.Sections);
        Assert.Equal("chorus", section.Type);
        var line = section.Lines[0];
        Assert.Equal("[Gbm]Gonna take a [D]lot to drag me away,", line.Text);
        Assert.Equal(2, line.Chords.Count);
        Assert.Equal("Gbm", line.Chords[0].Name);
        Assert.Equal(0, line.Chords[0].CharIndex);
    }

    [Fact]
    public void Parse_InlineDirectiveMidLine_StrippedFromText()
    {
        // Regression (bug report 2026-10-19): inline tags like {eoc} sit mid-line in stored charts
        // ([E]{eoc}) and must not render as literal text.
        var song = ChordProParser.Parse("""
            {sov}
            ha -[E]-[Gbm]d.[E]{eoc}
            """);

        var line = song.Sections[0].Lines[0];
        Assert.Equal("ha -[E]-[Gbm]d.[E]", line.Text);
        Assert.Equal(3, line.Chords.Count);
    }

    [Fact]
    public void Parse_InlineUnknownDirectiveMidLine_StrippedFromText()
    {
        // Any inline {…} token is an invisible marker — unknown ones are stripped too.
        var song = ChordProParser.Parse("""
            {sov}
            [C]hello{q: 120}world
            """);

        Assert.Equal("[C]helloworld", song.Sections[0].Lines[0].Text);
    }

    // --- ST-60: parser hardening ---------------------------------------------

    [Fact]
    public void Parse_ColonInSectionLabel_LineNotDropped()
    {
        // ST-60 #1: SplitDirective split on the first colon INSIDE the label — the directive
        // matched no branch and the whole line vanished, leaking lyrics into the previous section.
        var song = ChordProParser.Parse("""
            {start_of_verse label="Verse: 1"}
            Hello [C] world
            {end_of_verse}
            """);

        Assert.Single(song.Sections);
        Assert.Equal("verse", song.Sections[0].Type);
        Assert.Equal("Verse: 1", song.Sections[0].Label);
        Assert.Single(song.Sections[0].Lines);
    }

    [Fact]
    public void Parse_SpaceSeparatedColonLabel_Parsed()
    {
        var song = ChordProParser.Parse("""
            {start_of_chorus label="Chorus: part 2"}
            Sing [G] it
            """);

        Assert.Equal("chorus", song.Sections[0].Type);
        Assert.Equal("Chorus: part 2", song.Sections[0].Label);
    }

    [Theory]
    [InlineData("[C add9]", "C add9")]
    [InlineData("[Em7-5]", "Em7-5")]
    [InlineData("[F#m7(b5)]", "F#m7(b5)")]
    [InlineData("[Gm7(add9)]", "Gm7(add9)")]
    [InlineData("[D7sus4]", "D7sus4")]
    [InlineData("[C/Gsus4]", "C/Gsus4")]
    public void Parse_WideChordGrammar_RecognizesUGStyleQualities(string token, string name)
    {
        // ST-60 #2: qualities with spaces, hyphens, parens, plus are chords now.
        var song = ChordProParser.Parse(token + " lyric");
        Assert.Equal(new[] { name }, song.Sections[0].Lines[0].Chords.Select(c => c.Name));
    }

    [Theory]
    [InlineData("[Bbb]")]
    [InlineData("[Cbb]")]
    [InlineData("[F##]")]
    public void Parse_DoubleAccidentals_NotChords(string token)
    {
        // ST-60 #12: (?![#b]) after the accidental rejects double accidentals — they stay text
        // instead of being mangled by the transposer.
        var song = ChordProParser.Parse(token + " lyric");
        Assert.Empty(song.Sections[0].Lines[0].Chords);
    }

    [Fact]
    public void Parse_LegacyShorthandHeaders_ParsedDirectly()
    {
        // ST-60 #3: stored pre-ST-48 charts parse without import history.
        var song = ChordProParser.Parse("""
            {t:Africa}
            {a: Toto}
            {k: E minor}
            {c: practice version}
            """);

        Assert.Equal("Africa", song.Header?.Title);
        Assert.Equal("Toto", song.Header?.Artist);
        Assert.Equal("E minor", song.Header?.Key);
        Assert.Equal("other", song.Sections[0].Type);
        Assert.Equal("practice version", song.Sections[0].Label);
    }

    [Theory]
    [InlineData("{t:4/4}")]
    [InlineData("{t:120 bpm}")]
    [InlineData("{t:120}")]
    public void Parse_TempoFormT_NeverATitle(string directive)
    {
        // ST-60 #3/#5: tempo/meter markings have no header target — dropped, never a title.
        var song = ChordProParser.Parse(directive + "\n{start_of_verse}\nHello [C] world");
        Assert.NotEqual("4/4", song.Header?.Title);
        Assert.NotEqual("120 bpm", song.Header?.Title);
        Assert.NotEqual("120", song.Header?.Title);
    }

    [Fact]
    public void Parse_UppercaseCShorthand_IsCapoNotComment()
    {
        // ST-60 #4: per spec, {C:} is the capo shorthand (lowercase {c:} is a comment).
        var song = ChordProParser.Parse("{C: 3}");
        Assert.Equal("3", song.Header?.Capo);
        Assert.Empty(song.Sections);
    }

    [Fact]
    public void Parse_LegacyEndMarkers_CloseTheirSection()
    {
        // ST-60 #6: starts accept {sov}/{soc}/{sob}; the end counterparts used to be ignored,
        // so text after {eoc} landed INSIDE the chorus.
        var song = ChordProParser.Parse("""
            {soc}
            Chorus line [C]
            {eoc}
            After the chorus [G]
            """);

        Assert.Equal(2, song.Sections.Count);
        Assert.Equal("chorus", song.Sections[0].Type);
        Assert.Single(song.Sections[0].Lines);
        Assert.Equal("other", song.Sections[1].Type);
        Assert.Single(song.Sections[1].Lines);
    }

    [Fact]
    public void Parse_EscapedBrackets_KeptWholeAndUnescaped()
    {
        // ST-60 #8: ChordPro escapes \[ \] are honored — the token is kept whole (not split)
        // and the backslashes disappear from the recognized content.
        var song = ChordProParser.Parse("sing [*a\\]b] here");
        var line = song.Sections[0].Lines[0];
        Assert.Single(line.Annotations);
        Assert.Equal("*a]b", line.Annotations[0].Text);
    }

    [Fact]
    public void Parse_NestedBrackets_SplitLikeTheRegex()
    {
        // ST-60 #7 (documented): [C[F]D] — the token regex has no nesting; it matches [F] and
        // leaves "C[" / "D]" as text. Client mirror is identical; no silent chord mangling.
        var song = ChordProParser.Parse("[C[F]D] lyric");
        var line = song.Sections[0].Lines[0];
        Assert.Equal(new[] { "F" }, line.Chords.Select(c => c.Name));
    }

    [Fact]
    public void Parse_MultiLineDirective_Joined()
    {
        // ST-60 #9: {comment: X ↵ Y} is a directive spanning lines — joined, not literal text.
        var song = ChordProParser.Parse("{comment: A\nB}");
        Assert.Single(song.Sections);
        Assert.Equal("A B", song.Sections[0].Label);
        Assert.Empty(song.Sections[0].Lines);
    }

    [Theory]
    [InlineData("Chorus 2", "Chorus 2")]
    [InlineData("Second Final Chorus", "Second Final Chorus")]
    [InlineData("Bridge 3", "Bridge 3")]
    public void Parse_WideBareHeadings(string line, string label)
    {
        // ST-60 #11: multiple modifiers and numbered variants are headings too.
        var song = ChordProParser.Parse(line);
        Assert.Single(song.Sections);
        Assert.Equal("other", song.Sections[0].Type);
        Assert.Equal(label, song.Sections[0].Label);
    }

    [Fact]
    public void Parse_LyricLineNotAHeading()
    {
        var song = ChordProParser.Parse("We sing choruses");
        Assert.Single(song.Sections);
        Assert.Single(song.Sections[0].Lines);
    }

    [Fact]
    public void Parse_MultiAttributeLabel_QuoteAware()
    {
        // ST-60 #14: label="A" extra="B" — the label ends at the closing quote, not the last one.
        var song = ChordProParser.Parse("{start_of_verse label=\"A\" extra=\"B\"}");
        Assert.Equal("A", song.Sections[0].Label);
    }

    [Fact]
    public void Parse_EmptyValues_NoBlankHeaderFields()
    {
        // ST-60 #15: {title:} yields no title ("" renders as blank); {meta:} names nothing.
        var song = ChordProParser.Parse("{title:}\n{meta: }");
        Assert.True(song.Header is null || song.Header.Meta.Count == 0);
    }

    [Fact]
    public void Parse_UnknownSectionDirective_WarnsInsteadOfSilentLoss()
    {
        // ST-60 #1: an unrecognized start_of_* used to be a silent drop.
        var song = ChordProParser.Parse("""
            {start_of_verse}
            First [C]
            {start_of_solo}
            Solo line [G]
            """);

        Assert.Contains(song.Warnings, w => w.Contains("start_of_solo"));
        // The marker is skipped; lyrics continue in the open section.
        Assert.Single(song.Sections);
        Assert.Equal(2, song.Sections[0].Lines.Count);
    }

    [Fact]
    public void Parse_DoubleBracketChord_ReadsAsChordNotBracketText()
    {
        // Bug report (2026-11-12, "On Facedown there is a [[G]]"): a converter wrote
        // double-bracket chords; BracketToken matched only the inner [G] and the outer
        // brackets echoed as lyric text — a chord wearing visible brackets.
        var song = ChordProParser.Parse("""
            {start_of_verse}
            [[G]] Facedown [[Am7]] low
            """);

        var line = song.Sections[0].Lines[0];
        Assert.Equal(["G", "Am7"], line.Chords.Select(c => c.Name));
        // The line as written carries the SINGLE-bracket tokens (Text mirrors the
        // normalized input) — the outer brackets are gone, so nothing echoes as text.
        Assert.Equal("[G] Facedown [Am7] low", line.Text);
    }
}
