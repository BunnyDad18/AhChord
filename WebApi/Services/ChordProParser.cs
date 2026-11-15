using System.Text.RegularExpressions;
using WebApi.Models;

namespace WebApi.Services;

/// <summary>
/// Parses raw ChordPro text into a structured <see cref="ParsedSong"/>.
/// Pure and side-effect free. The contract is "never throw": malformed or unknown input
/// degrades to an (possibly empty) result plus <see cref="ParsedSong.Warnings"/>.
/// Chord names are returned exactly as written — no transposition (that is ST-08).
/// </summary>
public static class ChordProParser
{
    // ST-60: chord grammar widened to the shapes real charts use — qualities with spaces,
    // hyphens, plus and parens ([C add9], [Em7-5], [F#m7(b5)], [Gm7(add9)], [D7sus4]).
    // The root stays strict (A–G + one accidental); (?![#b]) rejects double accidentals
    // ([Bbb]) so they degrade as text instead of being mangled by the transposer.
    private static readonly Regex ChordName = new(
        @"^[A-G](#|b)?(?![#b])[A-Za-z0-9()\-+ ]*(?:/[A-G](#|b)?(?![#b])[A-Za-z0-9()\-+]*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ST-60: a bracket token honoring ChordPro's \[ \] escapes — `sing [C\]m] here` is one
    // token "C\]m" (unescape below), not a split at the escaped bracket.
    private static readonly Regex BracketToken = new(
        @"\[(?<content>(?:\\.|[^\[\]\\])*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ST-51: an inline directive anywhere in a line ({eoc}, {q: 120}, ...) — invisible markers
    // that must not render as text.
    private static readonly Regex InlineDirective = new(
        @"\{[^{}]*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ST-41: bare section heading lines ("Verse 1", "Chorus", ...) written as plain text by some
    // conversions — an optional modifier word (Final Chorus) plus the section name. Treated as a
    // section marker so they render in place as headings instead of lyric lines. ST-48: also
    // accepts a trailing colon/period ("Verse1:", "Chorus:") from files that forget the braces.
    // ST-60: multiple modifier words ("Second Final Chorus") and numbered variants
    // ("Chorus 2", "Verse 2a", "Bridge 3") are headings too; unmatched lyric lines are untouched.
    private static readonly Regex SectionHeading = new(
        @"^(?:[A-Za-z]+\s+)*(Intro|Outro|Interlude|Pre-?Chorus|Post-?Chorus|Verse\s*\d*[a-z]?|Chorus(\s*\d*[a-z]?)?|Bridge(\s*\d*[a-z]?)?)[.:]?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] HeaderNames =
        { "title", "artist", "key", "capo", "time", "tag", "genre", "year", "meta" };

    /// <summary>Parses <paramref name="input"/>. Never throws.</summary>
    public static ParsedSong Parse(string? input)
    {
        var result = new ParsedSong();

        try
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                result.Warnings.Add("Input is empty.");
                return result;
            }

            Section? current = null; // the section currently open (typed or comment)

            foreach (var rawLine in Normalize(input).Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                // ST-51: a directive can share its line with lyric text ({soc}[Gbm]Gonna...) or sit
                // inline mid-line ([E]{eoc}). Process any directive at the start of the line, then
                // strip the rest from the text so nothing renders literally.
                var rest = line;
                while (rest.Length >= 2 && rest[0] == '{')
                {
                    var close = rest.IndexOf('}');
                    if (close <= 0)
                        break; // unbalanced brace — treat what's left as text

                    var directive = rest[..(close + 1)];
                    rest = rest[(close + 1)..].TrimStart();

                    var (name, value) = SplitDirective(directive);
                    if (name.Length == 0)
                        continue; // bare {} → ignore gracefully

                    // ST-60: shorthand header aliases ({t:} {a:} {k:} {c:}) parse directly —
                    // stored charts must not depend on import history. Case-sensitive per spec:
                    // {c:} = comment, {C:} = capo. A tempo-form {t:...} (120 / 120 bpm / 4/4)
                    // is a tempo/meter marking with no header target — dropped, never a title.
                    (name, value) = CanonicalizeAlias(name, value);
                    if (name.Length == 0)
                        continue;

                    if (IsHeaderName(name))
                    {
                        ApplyHeader(result, name, value);
                    }
                    else if (TryGetSectionType(name, out var type))
                    {
                        current = new Section { Type = type, Label = ExtractLabel(value) };
                        result.Sections.Add(current);
                    }
                    else if (IsEndName(name))
                    {
                        current = null;
                    }
                    else if (name == "comment")
                    {
                        // ST-41: a {comment: ...} line is a section heading of type "other" —
                        // it starts a new section and following lyric lines belong to it until
                        // the next marker, so comments render in place (e.g. "{comment: Verse 1}").
                        current = new Section { Type = "other", Label = value, Lines = new List<Line>() };
                        result.Sections.Add(current);
                    }
                    // ST-60: an unrecognized start_of_*/end_of_* is silent data loss today —
                    // lyrics leak into the previous section with no trace. Convert it into a
                    // visible warning (the marker is still skipped; parsing continues).
                    else if (name.StartsWith("start_of_", StringComparison.Ordinal)
                             || name.StartsWith("end_of_", StringComparison.Ordinal))
                        result.Warnings.Add($"Unrecognized section directive “{name}” — the marker was skipped.");
                    // else: unknown/undocumented directive → ignore gracefully
                }

                rest = InlineDirective.Replace(rest, "");
                if (rest.Length == 0)
                    continue;

                if (SectionHeading.IsMatch(rest) && !rest.Contains('['))
                {
                    // ST-41: a bare heading line starts a new "other" section, like a comment marker.
                    // ST-48: strip a trailing colon/period from the label ("Chorus:" → "Chorus").
                    current = new Section { Type = "other", Label = rest.TrimEnd(':', '.').Trim(), Lines = new List<Line>() };
                    result.Sections.Add(current);
                }
                else
                {
                    var parsed = ParseLine(rest);
                    if (current is not null)
                    {
                        current.Lines.Add(parsed);
                    }
                    else
                    {
                        // Text outside any section (e.g. before the first marker): an unlabeled
                        // "other" section that following lines attach to until the next marker.
                        current = new Section { Type = "other", Label = null, Lines = new List<Line>() };
                        result.Sections.Add(current);
                        current.Lines.Add(parsed);
                    }
                }
            }

            // ST-41: typed sections without an explicit label get one synthesized from their
            // type — verses are numbered in order (Verse 1, Verse 2, ...), choruses and bridges
            // stay plain. "other" sections keep whatever label the comment carried (or none).
            var verseCount = 0;
            foreach (var section in result.Sections)
            {
                if (section.Label is not null || section.Type.Length == 0)
                    continue;
                section.Label = section.Type switch
                {
                    "verse" => $"Verse {++verseCount}",
                    "chorus" => "Chorus",
                    "bridge" => "Bridge",
                    _ => null
                };
            }

            if (result.Sections.Count == 0)
                result.Warnings.Add("No sections or lyrics found.");
        }
        catch
        {
            // Last-resort safety net: the contract is "never throw".
            result.Warnings.Add("Could not fully parse input.");
        }

        return result;
    }

    private static string Normalize(string input)
    {
        var s = input
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        // Bug report (2026-11-12, "On Facedown there is a [[G]]"): some chord-tab converters
        // emit double-bracket chords. BracketToken can only match the INNER [G], so the outer
        // brackets echoed as lyric text — a chord wearing visible brackets. [[X]] reads as [X]
        // (lenient input); the stored .cho text stays the source of truth, untouched.
        s = DoubleBracket.Replace(s, "[$1]");

        // ST-60: ChordPro permits directives spanning lines ({comment: X ↵ Y}) — join them so
        // they process instead of rendering literally. Bounded passes (nested/odd input safe).
        for (var pass = 0; pass < 4; pass++)
        {
            var joined = MultiLineDirective.Replace(s, "{$1 $2}");
            if (joined == s)
                break;
            s = joined;
        }
        return s;
    }

    private static readonly Regex DoubleBracket = new(
        @"\[\[([^\[\]]*)\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MultiLineDirective = new(
        @"\{([^{}\n]*)\n([^{}\n]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsHeaderName(string name) => Array.IndexOf(HeaderNames, name) >= 0;

    private static bool IsEndName(string name)
        => name is "end_of_verse" or "end_of_chorus" or "end_of_bridge"
            // ST-60: the legacy one-letter end shorthands are the counterparts of {sov}/{soc}/{sob},
            // which the parser already accepts — text after {eoc} used to land inside the chorus.
            or "eov" or "eoc" or "eob";

    private static bool TryGetSectionType(string name, out string type)
    {
        // ST-48: the short section names ({verse}, {chorus}, {bridge}) are common in the wild —
        // accept them alongside the long start_of_* forms. The legacy one-letter shorthands
        // ({sov}, {soc}, {sob}) appear in older charts (e.g. pre-ST-48 imports) and mean the same.
        type = name switch
        {
            "start_of_verse" or "verse" or "sov" => "verse",
            "start_of_chorus" or "chorus" or "soc" => "chorus",
            "start_of_bridge" or "bridge" or "sob" => "bridge",
            _ => ""
        };
        return type.Length > 0;
    }

    // ST-60: shorthand header aliases accepted directly by the parser (mirror of the import-time
    // normalizer, so stored pre-ST-48 charts parse). Case-sensitive: {c:} comment, {C:} capo.
    private static (string Name, string Value) CanonicalizeAlias(string name, string value)
        => name switch
        {
            // A tempo-form {t:...} is a tempo/meter marking (120, 120 bpm, 4/4) — no header
            // target here; drop it rather than title the song "4/4".
            "t" => TempoForm.IsMatch(value) ? (string.Empty, value) : ("title", value),
            "a" => ("artist", value),
            "k" => ("key", value),
            "c" => ("comment", value),
            "C" => ("capo", value),
            _ => (name, value),
        };

    private static readonly Regex TempoForm = new(
        @"^(\d{1,3}(\s*(bpm|beat|tempo))?\.?|\d+/\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits a <c>{name: value}</c> / <c>{name value}</c> / <c>{name}</c> directive.
    /// ST-60: the separator colon/space counts only OUTSIDE double quotes —
    /// <c>{start_of_verse label="Verse: 1"}</c> used to split inside the label, name the
    /// directive <c>start_of_verse label="Verse</c>, match no branch, and silently drop the line.
    /// </summary>
    private static (string Name, string Value) SplitDirective(string line)
    {
        var inner = line.Trim().TrimStart('{').TrimEnd('}').Trim();

        var colonIdx = IndexOutsideQuotes(inner, ':');
        if (colonIdx > 0)
            return (inner[..colonIdx].Trim(), inner[(colonIdx + 1)..].Trim());

        var spaceIdx = IndexOutsideQuotes(inner, ' ');
        if (spaceIdx > 0)
            return (inner[..spaceIdx].Trim(), inner[(spaceIdx + 1)..].Trim());

        return (inner, "");
    }

    private static int IndexOutsideQuotes(string s, char ch)
    {
        var inQuotes = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"')
                inQuotes = !inQuotes;
            else if (s[i] == ch && !inQuotes)
                return i;
        }
        return -1;
    }

    private static void ApplyHeader(ParsedSong result, string name, string value)
    {
        // ST-60: an empty value ({title:}) yields no header field — "" renders as a blank
        // title everywhere; null is the app's "not set".
        if (name != "meta" && string.IsNullOrWhiteSpace(value))
            return;

        var header = result.Header ??= new Header();
        switch (name)
        {
            case "title": header.Title = value; break;
            case "artist": header.Artist = value; break;
            case "key": header.Key = value; break;
            case "capo": header.Capo = value; break;
            case "time": header.Time = value; break;
            case "tag": header.Tag = value; break;
            // ST-35 F2: genre + release year (the LLM fills these during chord-page→ChordPro conversion).
            case "genre": header.Genre = value; break;
            case "year": header.Year = value; break;
            case "meta":
                // ST-60: {meta:} / {meta: } names nothing — adding an empty MetaField is junk.
                if (string.IsNullOrWhiteSpace(value))
                    break;
                var spaceIdx = value.IndexOf(' ');
                header.Meta.Add(spaceIdx > 0
                    ? new MetaField { Name = value[..spaceIdx], Value = value[(spaceIdx + 1)..].Trim() }
                    : new MetaField { Name = value, Value = "" });
                break;
        }
    }

    /// <summary>Extracts <c>label="..."</c> (or unquoted) from a section-start value.</summary>
    private static string? ExtractLabel(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("label=", StringComparison.Ordinal))
            return null;
        var rest = value["label=".Length..].Trim();

        // ST-60: quote-aware extraction — with several attributes (label="A" extra="B") the old
        // trim-both-quotes produced `A" extra="B`. Quoted labels end at the closing quote;
        // unquoted labels end at the first space.
        if (rest.Length >= 2 && rest[0] == '"')
        {
            var close = rest.IndexOf('"', 1);
            rest = close > 0 ? rest[1..close] : rest[1..];
        }
        else
        {
            var space = rest.IndexOf(' ');
            if (space > 0)
                rest = rest[..space];
        }
        return rest.Length > 0 ? rest : null;
    }

    private static Line ParseLine(string line)
    {
        var chords = new List<Chord>();
        var annotations = new List<Annotation>();
        var bars = new List<Bar>();

        foreach (Match m in BracketToken.Matches(line))
        {
            // ST-60: unescape ChordPro's \[ \] inside the token — [C\]m] is the chord "C]m"…
            // not a token, and not a split: the regex kept the token whole; the backslashes
            // disappear from the recognized content.
            var content = m.Groups["content"].Value.Replace("\\]", "]").Replace("\\[", "[");
            if (content.Length == 0)
                continue;

            if (content[0] == '*')
                annotations.Add(new Annotation { Text = content, CharIndex = m.Index });
            else if (ChordName.IsMatch(content))
                chords.Add(new Chord { Name = content, CharIndex = m.Index });
            // ST-54: exact-match bar notation tokens; anything else keeps today's drop behavior.
            else if (TryGetBarKind(content, out var kind))
                bars.Add(new Bar { Kind = kind, CharIndex = m.Index });
            // else: bracketed text that is neither a chord nor an annotation → left as text
        }

        return new Line { Text = line, Chords = chords, Annotations = annotations, Bars = bars };
    }

    /// <summary>ST-54: exact-match bar notation — <c>|</c> barline, <c>||:</c> repeat start, <c>:||</c> repeat end.</summary>
    private static bool TryGetBarKind(string content, out string kind)
    {
        switch (content)
        {
            case "|": kind = "bar"; return true;
            case "||:": kind = "repeat-start"; return true;
            case ":||": kind = "repeat-end"; return true;
            default: kind = ""; return false;
        }
    }
}
