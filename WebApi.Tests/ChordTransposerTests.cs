using WebApi.Services;

namespace WebApi.Tests;

public class ChordTransposerTests
{
    // --- Basic roots ----------------------------------------------------------

    [Fact]
    public void Transpose_G_Plus3_IsSharpPreferred()
    {
        // G up 3 semitones = A# (sharp-preferred normalization).
        Assert.Equal("A#", ChordTransposer.Transpose("G", 3));
    }

    [Fact]
    public void Transpose_G_Plus4_IsB()
    {
        Assert.Equal("B", ChordTransposer.Transpose("G", 4));
    }

    [Fact]
    public void Transpose_Dm_Plus2_IsEm()
    {
        // D up 2 semitones = E; quality suffix preserved verbatim.
        Assert.Equal("Em", ChordTransposer.Transpose("Dm", 2));
    }

    [Fact]
    public void Transpose_Dm_Plus3_IsFm()
    {
        Assert.Equal("Fm", ChordTransposer.Transpose("Dm", 3));
    }

    // --- Slash chords ---------------------------------------------------------

    [Fact]
    public void Transpose_Slash_TransposesBothParts()
    {
        Assert.Equal("A/C#", ChordTransposer.Transpose("G/B", 2));
    }

    [Fact]
    public void Transpose_SlashWithQuality_TransposesBothParts()
    {
        Assert.Equal("Dm7/C", ChordTransposer.Transpose("Bm7/A", 3));
    }

    // --- Qualities preserved verbatim ----------------------------------------

    [Fact]
    public void Transpose_KeepsQualityVerbatim()
    {
        Assert.Equal("Am7", ChordTransposer.Transpose("Em7", 5));
        Assert.Equal("A#sus4", ChordTransposer.Transpose("Fsus4", 5));
        Assert.Equal("Ddim", ChordTransposer.Transpose("Adim", 5));
        Assert.Equal("Faug", ChordTransposer.Transpose("Caug", 5));
        Assert.Equal("G#m7", ChordTransposer.Transpose("D#m7", 5));
    }

    // --- Octave wrap ----------------------------------------------------------

    [Fact]
    public void Transpose_OctaveWrap_IsIdentity()
    {
        Assert.Equal("C", ChordTransposer.Transpose("C", 12));
        Assert.Equal("F#m", ChordTransposer.Transpose("F#m", 12));
    }

    // --- Normalization (sharp-preferred) --------------------------------------

    [Fact]
    public void Transpose_FlatInputs_NormalizeToSharps()
    {
        Assert.Equal("B", ChordTransposer.Transpose("Cb", 0));
        Assert.Equal("C#", ChordTransposer.Transpose("Db", 0));
        Assert.Equal("A#", ChordTransposer.Transpose("Bb", 0));
        Assert.Equal("E", ChordTransposer.Transpose("Fb", 0));
    }

    // --- Identity -------------------------------------------------------------

    [Fact]
    public void Transpose_Zero_IsIdentity()
    {
        Assert.Equal("C", ChordTransposer.Transpose("C", 0));
        Assert.Equal("Dm", ChordTransposer.Transpose("Dm", 0));
        Assert.Equal("G/B", ChordTransposer.Transpose("G/B", 0));
        Assert.Equal("F#m7", ChordTransposer.Transpose("F#m7", 0));
    }

    // --- Negative semitones (sign-flip case) ----------------------------------

    [Fact]
    public void Transpose_Negative_Downwards()
    {
        Assert.Equal("B", ChordTransposer.Transpose("C", -1));
        Assert.Equal("A#", ChordTransposer.Transpose("C", -2));
        Assert.Equal("Cm", ChordTransposer.Transpose("Dm", -2));
    }

    // --- Invalid input → unchanged --------------------------------------------

    [Fact]
    public void Transpose_InvalidInput_ReturnsUnchanged()
    {
        Assert.Equal("", ChordTransposer.Transpose("", 3));
        Assert.Equal("   ", ChordTransposer.Transpose("   ", 3));
        Assert.Equal("notachord", ChordTransposer.Transpose("notachord", 3));
        // ST-60: bass qualities are in the grammar now — C/Eb7 is a valid chord (was "invalid").
        Assert.Equal("D#/F#7", ChordTransposer.Transpose("C/Eb7", 3));
        Assert.Equal("[C]", ChordTransposer.Transpose("[C]", 3));
    }

    // --- ST-51: projection transpose keeps text and char indexes in sync ------
    // The renderer slices line text at charIndex + name length, so a transposed name of a
    // different length must be reflected in the (rewritten) line text and shifted indexes.

    [Fact]
    public void Transpose_Parsed_SameLengthNames_TextAndIndexesStable()
    {
        var parsed = ChordProParser.Parse("{start_of_verse}\n[C] hello [G] world\n{end_of_verse}");
        ChordTransposer.Transpose(parsed, 2); // C->D, G->A (same length)

        var line = parsed.Sections[0].Lines[0];
        Assert.Equal("[D] hello [A] world", line.Text);
        Assert.Equal("D", line.Chords[0].Name);
        Assert.Equal(0, line.Chords[0].CharIndex);
        Assert.Equal("A", line.Chords[1].Name);
        Assert.Equal(10, line.Chords[1].CharIndex);
    }

    [Fact]
    public void Transpose_Parsed_ShorterName_NoStrayBracket()
    {
        // Regression (bug report 2026-10-19): [Bb] +1 -> [B] used to leave a stray ']' in the text.
        var parsed = ChordProParser.Parse("{start_of_verse}\n[Bb] rest\n{end_of_verse}");
        ChordTransposer.Transpose(parsed, 1);

        var line = parsed.Sections[0].Lines[0];
        Assert.Equal("[B] rest", line.Text);
        Assert.Equal("B", line.Chords[0].Name);
        Assert.Equal(0, line.Chords[0].CharIndex);
    }

    [Fact]
    public void Transpose_Parsed_LongerName_FollowingTextIntact()
    {
        // Regression: [C] +1 -> [C#] used to eat one character of the following text.
        var parsed = ChordProParser.Parse("{start_of_verse}\n[C] rule\n{end_of_verse}");
        ChordTransposer.Transpose(parsed, 1);

        var line = parsed.Sections[0].Lines[0];
        Assert.Equal("[C#] rule", line.Text);
        Assert.Equal("C#", line.Chords[0].Name);
        Assert.Equal(0, line.Chords[0].CharIndex);
    }

    [Fact]
    public void Transpose_Parsed_MultipleChords_IndexesRecomputed()
    {
        var parsed = ChordProParser.Parse("{start_of_verse}\n[Bb] a [C] b\n{end_of_verse}");
        ChordTransposer.Transpose(parsed, 1); // Bb->B (shorter), C->C# (longer)

        var line = parsed.Sections[0].Lines[0];
        Assert.Equal("[B] a [C#] b", line.Text);
        Assert.Equal(0, line.Chords[0].CharIndex);
        // Second chord shifted by -1 (the Bb->B shortening before it).
        Assert.Equal("C#", line.Chords[1].Name);
        Assert.Equal(6, line.Chords[1].CharIndex);
    }

    [Fact]
    public void Transpose_Parsed_AnnotationIndexesShifted()
    {
        var parsed = ChordProParser.Parse("{start_of_verse}\n[Bb] a [*coda] b\n{end_of_verse}");
        ChordTransposer.Transpose(parsed, 1); // Bb->B: -1

        var line = parsed.Sections[0].Lines[0];
        Assert.Equal("[B] a [*coda] b", line.Text);
        Assert.Equal(6, line.Annotations[0].CharIndex); // was 7
    }

    [Fact]
    public void Transpose_Parsed_BarIndexesShifted()
    {
        var parsed = ChordProParser.Parse("{start_of_verse}\n[Bb] a [|] b\n{end_of_verse}");
        ChordTransposer.Transpose(parsed, 1); // Bb->B: -1

        var line = parsed.Sections[0].Lines[0];
        Assert.Equal("[B] a [|] b", line.Text);
        Assert.Equal(6, line.Bars[0].CharIndex); // was 7
    }

    [Fact]
    public void Transpose_Parsed_ZeroSemitones_NormalizesNamesOnly()
    {
        var parsed = ChordProParser.Parse("{start_of_verse}\n[Bb] a\n{end_of_verse}");
        ChordTransposer.Transpose(parsed, 0);

        var line = parsed.Sections[0].Lines[0];
        Assert.Equal("[A#] a", line.Text); // sharp-preferred normalization, text kept in sync
        Assert.Equal("A#", line.Chords[0].Name);
    }

    // --- ST-60: widened grammar, bass quality, overflow guard -------------------

    [Theory]
    [InlineData("C add9", 2, "D add9")]
    [InlineData("F#m7(b5)", 1, "Gm7(b5)")]
    [InlineData("Em7-5", 2, "F#m7-5")]
    [InlineData("Gm7(add9)", 2, "Am7(add9)")]
    [InlineData("D7sus4", 2, "E7sus4")]
    public void Transpose_WideQualities_RootShiftedQualityPreserved(string chord, int semitones, string expected)
    {
        // ST-60 #2: the transposer grammar mirrors the parser — space/hyphen/paren qualities
        // used to be untouched text (never transposed).
        Assert.Equal(expected, ChordTransposer.Transpose(chord, semitones));
    }

    [Fact]
    public void Transpose_BassQualityPreserved()
    {
        // ST-60: [C/Gsus4]-style bass qualities keep their quality after the shifted bass root.
        Assert.Equal("D/Asus4", ChordTransposer.Transpose("C/Gsus4", 2));
    }

    [Theory]
    [InlineData("Bbb", 0)]
    [InlineData("Cbb", 1)]
    [InlineData("F##", 3)]
    public void Transpose_DoubleAccidentals_LeftUntouched(string chord, int semitones)
    {
        // ST-60 #12: the grammar rejects double accidentals — "Bbb" used to become "A#b".
        Assert.Equal(chord, ChordTransposer.Transpose(chord, semitones));
    }

    [Fact]
    public void Transpose_ExtremeSemitones_NoOverflowCrash()
    {
        // ST-60 #13: the shift is reduced modulo 12 before adding (int overflow used to
        // produce a negative modulo — defense in depth, the API clamps capo).
        Assert.Equal("G", ChordTransposer.Transpose("C", int.MaxValue));   // 2^31-1 % 12 = 7
        Assert.Equal("E", ChordTransposer.Transpose("C", int.MinValue));   // 2^31 % 12 -> -8, mod-safe
    }
}
