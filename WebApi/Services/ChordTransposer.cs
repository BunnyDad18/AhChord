using System.Text;
using System.Text.RegularExpressions;
using WebApi.Models;

namespace WebApi.Services;

/// <summary>
/// Transposes a single chord name by a number of semitones (ST-08).
/// Pure and side-effect free. The contract is "never throw": invalid input is
/// returned unchanged.
///
/// Convention (MVP): the displayed chords are the fingerings to play, so
/// <c>display = transpose(written, +N)</c> where N is the selected capo fret.
/// Note names normalize to sharps (e.g. <c>Cb</c> → <c>B</c>, <c>Db</c> → <c>C#</c>);
/// the quality suffix (m, 7, maj9, sus4, dim, aug, …) is preserved verbatim.
/// Slash chords transpose both the root and the bass note.
/// </summary>
public static class ChordTransposer
{
    // Mirrors ChordProParser.ChordName (ST-60): root A–G, one accidental, quality with
    // spaces/hyphens/plus/parens ([C add9], [Em7-5], [F#m7(b5)]), optional /bass.
    // (?![#b]) rejects double accidentals ([Bbb]) — they stay untouched text, not mangled.
    private static readonly Regex Chord = new(
        @"^(?<root>[A-G])(?<acc>#|b)?(?![#b])(?<quality>[A-Za-z0-9()\-+ ]*)(?:/(?<bassRoot>[A-G])(?<bassAcc>#|b)?(?![#b])(?<bassQuality>[A-Za-z0-9()\-+]*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Pitch class 0–11, sharp-preferred.
    private static readonly string[] NoteNames =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>Transposes <paramref name="chord"/> by <paramref name="semitones"/>. Invalid input is returned unchanged.</summary>
    public static string Transpose(string chord, int semitones)
    {
        if (string.IsNullOrWhiteSpace(chord))
            return chord;

        var m = Chord.Match(chord.Trim());
        if (!m.Success)
            return chord; // invalid → unchanged (never throw)

        var quality = m.Groups["quality"].Value;
        var root = TransposeNote(m.Groups["root"].Value, m.Groups["acc"].Value, semitones);
        if (root is null)
            return chord;

        var result = root + quality;

        if (m.Groups["bassRoot"].Success)
        {
            // ST-60: the bass may carry a quality too ([C/Gsus4] style) — preserved verbatim.
            var bass = TransposeNote(m.Groups["bassRoot"].Value, m.Groups["bassAcc"].Value, semitones);
            if (bass is null)
                return chord;
            result += "/" + bass + m.Groups["bassQuality"].Value;
        }

        return result;
    }

    /// <summary>
    /// Transposes every chord in a parsed projection by <paramref name="semitones"/> (ST-08 read-time
    /// transpose). The line text is rewritten so bracketed tokens stay in sync with the (possibly
    /// renamed) chord names and all char indexes are recomputed: the renderer slices text at
    /// charIndex + name length, so a transposed name of a different length would otherwise leave a
    /// stray <c>]</c> behind or eat into the following text. Header is untouched; the raw source
    /// text (the stored chart) is never mutated — this operates on the parse result only.
    /// </summary>
    public static void Transpose(ParsedSong parsed, int semitones)
    {
        foreach (var section in parsed.Sections)
            foreach (var line in section.Lines)
                TransposeLine(line, semitones);
    }

    private static void TransposeLine(Line line, int semitones)
    {
        var chords = line.Chords.OrderBy(c => c.CharIndex).ToList();
        if (chords.Count == 0)
            return;

        var sb = new StringBuilder(line.Text.Length);
        var deltas = new List<(int Start, int Delta)>(); // original start → length change
        int pos = 0;
        foreach (var chord in chords)
        {
            var start = chord.CharIndex;                    // index of '[' in the current text
            var end = start + 1 + chord.Name.Length + 1;    // one past ']'
            if (start < pos || end > line.Text.Length)
                continue; // defensive: token not where expected — leave it as is

            sb.Append(line.Text, pos, start - pos);
            var newStart = sb.Length; // position where '[' will land in the rewritten text
            var orig = chord.Name;
            var transposed = Transpose(orig, semitones);
            sb.Append('[').Append(transposed).Append(']');
            chord.CharIndex = newStart;
            chord.Name = transposed;
            if (transposed.Length != orig.Length)
                deltas.Add((start, transposed.Length - orig.Length));
            pos = end;
        }
        sb.Append(line.Text, pos, line.Text.Length - pos);

        // Annotations share the same text: shift each by the length changes of chords before it.
        foreach (var ann in line.Annotations)
        {
            int shift = 0;
            foreach (var d in deltas)
                if (d.Start < ann.CharIndex)
                    shift += d.Delta;
            ann.CharIndex += shift;
        }

        // ST-54: bars share the same text — same shift as annotations.
        foreach (var bar in line.Bars)
        {
            int shift = 0;
            foreach (var d in deltas)
                if (d.Start < bar.CharIndex)
                    shift += d.Delta;
            bar.CharIndex += shift;
        }

        line.Text = sb.ToString();
    }

    private static int RootIndex(char c) => c switch
    {
        'C' => 0,
        'D' => 2,
        'E' => 4,
        'F' => 5,
        'G' => 7,
        'A' => 9,
        'B' => 11,
        _ => -1
    };

    private static string? TransposeNote(string root, string acc, int semitones)
    {
        var idx = RootIndex(root[0]);
        if (idx < 0)
            return null;
        if (acc.Length == 1)
            idx += acc[0] == '#' ? 1 : -1;
        // ST-60: reduce the shift before adding — int overflow at extreme semitones inputs
        // used to produce a negative modulo (unreachable via the API; defense in depth).
        var target = ((idx + semitones % 12) % 12 + 12) % 12;
        return NoteNames[target];
    }
}
