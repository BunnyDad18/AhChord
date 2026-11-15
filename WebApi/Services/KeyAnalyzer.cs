using System.Text.RegularExpressions;
using WebApi.Models;

namespace WebApi.Services;

/// <summary>
/// ST-106: the key of a chart <em>as written</em> — derived from its chords, never from
/// online metadata. The ChordPro text stays the source of truth (AHC-1); this is a
/// read-time analysis, not a stored table. Capo is display-only (AHC-2), so a capo'd
/// chart analyses the shapes the player shows.
///
/// Method: build a pitch-class histogram from the parsed chords (root-weighted, third
/// by quality, cadence bias on section-final chords) and score it against the
/// Krumhansl–Kucucki key profiles for all 12 major and 12 minor keys (Pearson
/// correlation). Confidence is the margin between the top fit and the runner-up.
/// </summary>
public static partial class KeyAnalyzer
{
    /// <summary>The derived key: display name ("G", "F#m") + confidence 0–1.</summary>
    public sealed record Result(string Key, double Confidence);

    private const int MinOccurrences = 4;
    private const int MinDistinctChords = 3;

    // Krumhansl–Kucucki probe-tone profiles misrank chord-driven histograms (they score
    // the major V at 1.18, so parallel minors win everywhere — observed in tests).
    // Fujishaka (2006) templates are chord-derived: tonic/5th dominate, and the
    // discriminating cells are b3 (15.9 for minor, 0.8 for major) and 3 (9.1 major,
    // 0 minor). Index = semitones above the key's root.
    private static readonly double[] MajorTemplate =
        [17.7, 0.0, 6.9, 0.0, 9.1, 7.4, 0.0, 17.1, 0.8, 7.4, 0.4, 2.7];

    private static readonly double[] MinorTemplate =
        [17.4, 0.5, 7.5, 15.9, 0.0, 7.2, 0.0, 17.2, 0.0, 7.7, 0.0, 0.0];

    // Conventional enharmonic spelling for the pitch classes of flat-leaning worship keys.
    private static readonly string[] PitchNames =
        ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    // Root letter + optional accidentals; the quality suffix decides minor/dim/aug/sus.
    [GeneratedRegex(@"^([A-G])(?<acc>(?:##|bb|#|b|♯|♭)*)(?<q>.*)$")]
    private static partial Regex ChordPattern();

    public static Result? Analyze(string? chordProText) => Analyze(ChordProParser.Parse(chordProText));

    public static Result? Analyze(ParsedSong parsed)
    {
        var occurrences = 0;
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var histogram = new double[12];

        foreach (var (chord, sectionWeight) in EnumerateChords(parsed))
        {
            if (!TryChordPitches(chord, out var pitches, out var pitchWeights))
            {
                continue;
            }

            occurrences++;
            distinct.Add(NormalizeChordKey(chord));
            for (var i = 0; i < pitches.Length; i++)
            {
                histogram[pitches[i]] += pitchWeights[i] * sectionWeight;
            }
        }

        if (occurrences < MinOccurrences || distinct.Count < MinDistinctChords)
        {
            return null;
        }

        var (bestPc, bestIsMinor, bestScore) = BestKey(histogram);
        if (bestScore <= 0)
        {
            return null;
        }

        var (secondPc, secondIsMinor, secondScore) = SecondBestKey(histogram, bestPc, bestIsMinor);
        var margin = (bestScore - secondScore) / Math.Max(bestScore, 1e-9);
        var confidence = Math.Clamp(margin, 0, 1);

        var name = PitchNames[bestPc] + (bestIsMinor ? "m" : string.Empty);
        return new Result(name, Math.Round(confidence, 3));
    }

    /// <summary>Every chord with its cadence weight: ×3 the last chord of the chart,
    /// ×2 a section-final chord, ×1 otherwise. Sections are the chart body the player
    /// renders; header directives carry no chords.</summary>
    private static IEnumerable<(string Chord, double Weight)> EnumerateChords(ParsedSong parsed)
    {
        var sections = parsed.Sections;
        for (var s = 0; s < sections.Count; s++)
        {
            var chords = sections[s].Lines.SelectMany(l => l.Chords).Select(c => c.Name).ToList();
            for (var i = 0; i < chords.Count; i++)
            {
                var isSectionEnd = i == chords.Count - 1;
                var weight = s == sections.Count - 1 && isSectionEnd ? 3.0 : isSectionEnd ? 2.0 : 1.0;
                yield return (chords[i], weight);
            }
        }
    }

    private static string NormalizeChordKey(string chord) => chord.Trim().ToUpperInvariant();

    /// <summary>
    /// Root + quality → the chord's pitch classes and weights. Root 2.0, third 1.0,
    /// fifth 1.0, sevenths/sixths 0.5; sus/power chords skip the third (no tonal
    /// evidence). Unparseable tokens (NC, percussion stabs, notes) are skipped.
    /// </summary>
    private static bool TryChordPitches(string chord, out int[] pitches, out double[] weights)
    {
        pitches = [];
        weights = [];

        // Slash bass is voicing, not harmony: drop everything after the '/'.
        var body = chord.Trim();
        var slash = body.IndexOf('/');
        if (slash > 0)
        {
            body = body[..slash];
        }

        var match = ChordPattern().Match(body);
        if (!match.Success)
        {
            return false;
        }

        var root = LetterToPitch(match.Groups[1].Value[0]) + AccidentalValue(match.Groups["acc"].Value);
        var quality = match.Groups["q"].Value.ToLowerInvariant();

        // Diminished/augmented change third AND fifth; minor changes the third.
        var isMinor = IsMinorQuality(quality);
        var isDim = quality.StartsWith("dim") || quality.StartsWith("o") || quality.Contains('°');
        var isAug = quality.StartsWith("aug") || quality.Contains('+');

        var third = isDim ? 3 : isAug ? 5 : isMinor ? 3 : 4;
        var fifth = isDim ? 6 : isAug ? 8 : 7;

        // Sus and power chords carry no third evidence.
        var hasThird = !quality.Contains("sus") && !quality.Equals("5") && !quality.Contains("(5)");

        var tones = new List<(int Pitch, double Weight)>
        {
            ((root + 0) % 12, 2.0),
            ((root + fifth) % 12, 1.0),
        };
        if (hasThird)
        {
            tones.Insert(1, ((root + third) % 12, 1.0));
        }

        // Extensions, light weight: dom7 +10, maj7 +11, m7 +10, add6 +9, 9 = +2.
        if (quality.Contains("maj7") || quality.Contains("ma7") || quality.Contains("Δ"))
        {
            tones.Add(((root + 11) % 12, 0.5));
        }
        else if (quality.Contains("7"))
        {
            tones.Add(((root + 10) % 12, 0.5));
        }

        if (quality.Contains("6") && !quality.Contains("13"))
        {
            tones.Add(((root + 9) % 12, 0.5));
        }

        pitches = tones.Select(t => t.Pitch).ToArray();
        weights = tones.Select(t => t.Weight).ToArray();
        return true;
    }

    private static bool IsMinorQuality(string quality)
    {
        // "maj…", "M", "major" are explicit major; a bare 'm' (m, m7, m9, mmaj7) is minor.
        if (quality.StartsWith("maj") || quality.StartsWith("major") || quality.StartsWith("M"))
        {
            return false;
        }

        return quality.Length > 0 && quality[0] == 'm';
    }

    private static int LetterToPitch(char letter) => letter switch
    {
        'C' => 0,
        'D' => 2,
        'E' => 4,
        'F' => 5,
        'G' => 7,
        'A' => 9,
        'B' => 11,
        _ => throw new InvalidOperationException($"Not a chord letter: {letter}"),
    };

    private static int AccidentalValue(string accidentals)
    {
        var semitones = 0;
        foreach (var c in accidentals)
        {
            semitones += c is '#' or '♯' ? 1 : c is 'b' or '♭' ? -1 : 0;
        }

        return semitones;
    }

    private static (int Pc, bool IsMinor, double Score) BestKey(double[] histogram)
    {
        var best = (Pc: 0, IsMinor: false, Score: double.NegativeInfinity);
        for (var minor = 0; minor < 2; minor++)
        {
            var template = minor == 1 ? MinorTemplate : MajorTemplate;
            for (var root = 0; root < 12; root++)
            {
                var score = Correlate(histogram, template, root);
                if (score > best.Score)
                {
                    best = (root, minor == 1, score);
                }
            }
        }

        return best;
    }

    private static (int Pc, bool IsMinor, double Score) SecondBestKey(double[] histogram, int bestPc, bool bestIsMinor)
    {
        var second = (Pc: 0, IsMinor: false, Score: double.NegativeInfinity);
        for (var minor = 0; minor < 2; minor++)
        {
            var template = minor == 1 ? MinorTemplate : MajorTemplate;
            for (var root = 0; root < 12; root++)
            {
                // Skip the winner itself; the relative major/minor of the same root is
                // a legitimate runner-up.
                if (root == bestPc && (minor == 1) == bestIsMinor)
                {
                    continue;
                }

                var score = Correlate(histogram, template, root);
                if (score > second.Score)
                {
                    second = (root, minor == 1, score);
                }
            }
        }

        return second;
    }

    /// <summary>
    /// Template score: Σ log(1 + histogram[pitch]) × template[semitones above root].
    /// The log compresses repetition (an A-belt of 20 G chords is not 20× the evidence
    /// of one) and turns the template's zero cells into hard evidence requirements:
    /// a major third in the material contributes nothing to the parallel minor.
    /// </summary>
    private static double Correlate(double[] histogram, double[] template, int root)
    {
        var score = 0.0;
        for (var i = 0; i < 12; i++)
        {
            var pitch = (root + i) % 12;
            score += Math.Log(1 + histogram[pitch]) * template[i];
        }

        return score;
    }
}
