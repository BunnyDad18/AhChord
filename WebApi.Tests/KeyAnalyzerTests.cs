using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-106: KeyAnalyzer — the key of the written chords (AHC-1/AHC-2: derived from the
/// chart, capo never shifts the analysis). Fixtures are public-domain melodies with
/// our own chord arrangements.
/// </summary>
public class KeyAnalyzerTests
{
    [Fact]
    public void Analyze_AmazingGrace_GMajor()
    {
        var result = KeyAnalyzer.Analyze("""
            {title: Amazing Grace}

            {start_of_verse}
            [G]A-maz-ing [G]Grace,
            [D]how [G]sweet
            the [G]sound!
            {end_of_verse}

            {start_of_verse}
            'Twas [G]grace that taught my [C]heart to [G]fear,
            And [G]grace my [D]fears re[G]lieved.
            {end_of_verse}
            """);

        Assert.NotNull(result);
        Assert.Equal("G", result.Key);
        Assert.True(result.Confidence > 0.15, $"expected a confident fit, got {result.Confidence}");
    }

    [Fact]
    public void Analyze_RhythmChanges_CMajor()
    {
        var result = KeyAnalyzer.Analyze("""
            {start_of_chorus}
            [C] [Am] [F] [G]
            [C] [Am] [F] [G]
            {end_of_chorus}
            {start_of_outro}
            [F] [G] [C]
            {end_of_outro}
            """);

        Assert.NotNull(result);
        Assert.Equal("C", result.Key);
    }

    [Fact]
    public void Analyze_RisingSun_AMinor_NotRelativeC()
    {
        // The E major (G#) is the leading-tone evidence that pins A minor over C.
        var result = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [Am] [C] [D] [F]
            [Am] [C] [E] [Am]
            {end_of_verse}
            {start_of_verse}
            [Am] [C] [D] [F]
            [Am] [E] [Am] [Am]
            {end_of_verse}
            """);

        Assert.NotNull(result);
        Assert.Equal("Am", result.Key);
    }

    [Fact]
    public void Analyze_DMajor_CadenceToD()
    {
        var result = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [D] [G] [D] [A]
            [D] [G] [Bm] [A]
            {end_of_verse}
            {start_of_outro}
            [G] [A] [D]
            {end_of_outro}
            """);

        Assert.NotNull(result);
        Assert.Equal("D", result.Key);
    }

    [Fact]
    public void Analyze_EmWithLeadingTone_EMinor()
    {
        var result = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [Em] [C] [G] [D]
            [Em] [C] [B7] [Em]
            {end_of_verse}
            """);

        Assert.NotNull(result);
        Assert.Equal("Em", result.Key);
    }

    [Fact]
    public void Analyze_FlatKey_BbMajor()
    {
        var result = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [Bb] [Eb] [Bb] [F]
            [Bb] [Eb] [Cm] [F]
            {end_of_verse}
            {start_of_outro}
            [Eb] [F] [Bb]
            {end_of_outro}
            """);

        Assert.NotNull(result);
        Assert.Equal("Bb", result.Key);
    }

    [Fact]
    public void Analyze_SharpMinorKey_FSharpMinor()
    {
        var result = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [F#m] [D] [A] [E]
            [F#m] [D] [E] [F#m]
            {end_of_verse}
            """);

        Assert.NotNull(result);
        Assert.Equal("F#m", result.Key);
    }

    [Fact]
    public void Analyze_SlashChords_BassDoesNotShiftKey()
    {
        var result = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [C] [C/G] [F] [F/C]
            [Am] [G/B] [F] [G]
            {end_of_verse}
            {start_of_outro}
            [F] [G/D] [C]
            {end_of_outro}
            """);

        Assert.NotNull(result);
        Assert.Equal("C", result.Key);
    }

    [Fact]
    public void Analyze_Jazzii_V_I_CMajor()
    {
        var result = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [Dm7] [G7] [Cmaj7] [Am7]
            [Dm7] [G7] [Cmaj7] [Cmaj7]
            {end_of_verse}
            """);

        Assert.NotNull(result);
        Assert.Equal("C", result.Key);
    }

    [Fact]
    public void Analyze_HeaderKeyLies_ChordsWin()
    {
        // AHC-1: the chords in the text are the truth; {key:} is ignored here.
        var result = KeyAnalyzer.Analyze("""
            {key: C}
            {start_of_verse}
            [G] [D] [Em] [C]
            [G] [D] [C] [D]
            {end_of_verse}
            {start_of_outro}
            [C] [D] [G]
            {end_of_outro}
            """);

        Assert.NotNull(result);
        Assert.Equal("G", result.Key);
    }

    [Fact]
    public void Analyze_CapoInHeader_ReportsWrittenShapes()
    {
        // AHC-2: capo is display-only; the analysis is the key of the shapes shown.
        var result = KeyAnalyzer.Analyze("""
            {capo: 3}
            {start_of_verse}
            [G] [D] [Em] [C]
            [G] [D] [C] [D]
            {end_of_verse}
            {start_of_outro}
            [C] [D] [G]
            {end_of_outro}
            """);

        Assert.NotNull(result);
        Assert.Equal("G", result.Key);
    }

    [Fact]
    public void Analyze_NoChords_ReturnsNull()
    {
        Assert.Null(KeyAnalyzer.Analyze("""
            {title: Lyric Only}
            {start_of_verse}
            Just words, no chords at all.
            {end_of_verse}
            """));
    }

    [Fact]
    public void Analyze_SingleChordRepeating_ReturnsNull()
    {
        Assert.Null(KeyAnalyzer.Analyze("""
            {start_of_verse}
            [G] droning [G] on [G] one [G] chord [G]
            {end_of_verse}
            """));
    }

    [Fact]
    public void Analyze_NonChordTokens_Skipped()
    {
        var result = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [NC] [G] [D] [Em] [C]
            [G] [D] [C] [D]
            {end_of_verse}
            {start_of_outro}
            [C] [D] [G]
            {end_of_outro}
            """);

        Assert.NotNull(result);
        Assert.Equal("G", result.Key);
    }

    [Fact]
    public void Analyze_CleanCadence_BeatsChromaticNoise_Confidence()
    {
        var clean = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [G] [C] [G] [D]
            [G] [C] [D] [G]
            {end_of_verse}
            """);

        var noise = KeyAnalyzer.Analyze("""
            {start_of_verse}
            [C] [Db] [D] [Eb] [E] [F] [Gb]
            [G] [Ab] [A] [Bb] [B] [Cm] [C#m]
            {end_of_verse}
            """);

        Assert.NotNull(clean);
        if (noise != null)
        {
            Assert.True(clean.Confidence > noise.Confidence);
        }
    }
}
