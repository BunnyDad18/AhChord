using System.Text.RegularExpressions;

namespace WebApi.Services;

/// <summary>
/// ST-48: rewrites common ChordPro variants into the canonical form this app parses, at import
/// time — so the stored chart is clean ChordPro. Handles shorthand header directives ({t:}, {a:},
/// {k:}, {c:}), section aliases ({soc} → chorus, {verse}/{chorus}/{bridge}, {intro} → comment
/// heading) and bare heading lines ("Verse1:", "Chorus", "Final Chorus."). Pure and idempotent;
/// anything it does not recognize is left exactly as written (untouched lines keep their
/// original whitespace). Never throws — falls back to the input as-is.
/// </summary>
public static class ChordProNormalizer
{
    // Shorthand header names → full form. ST-60: ORDINAL (case-sensitive), per the ChordPro
    // spec: lowercase {c:} is a comment, uppercase {C:} is the capo shorthand — the old
    // OrdinalIgnoreCase map rewrote {C: 3} into {comment: 3}, destroying a stored capo.
    // ST-48 note: files in circulation also use {c:} for capo; the explicit {capo: N} form
    // is unaffected either way.
    private static readonly Dictionary<string, string> HeaderShorthands = new(StringComparer.Ordinal)
    {
        ["t"] = "title",
        ["a"] = "artist",
        ["k"] = "key",
        ["c"] = "comment",
        ["C"] = "capo",
    };

    // ST-60: {t:...} shapes that are tempo/meter markings, not titles — 120, 120 bpm, 4/4, 92.
    // These are left exactly as written (import must not title a song "4/4").
    private static readonly Regex TempoForm = new(
        @"^(\d{1,3}(\s*(bpm|beat|tempo))?\.?|\d+/\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Section directive aliases → canonical typed section name (case-insensitive).
    private static readonly Dictionary<string, string> SectionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["verse"] = "verse",
        ["chorus"] = "chorus",
        ["bridge"] = "bridge",
        ["soc"] = "chorus", // start of chorus
    };

    // Un-typed section names → rendered as a comment heading with this label.
    private static readonly Dictionary<string, string> CommentSections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["intro"] = "Intro",
        ["outro"] = "Outro",
        ["interlude"] = "Interlude",
        ["solo"] = "Solo",
        ["refrain"] = "Refrain",
        ["ending"] = "Ending",
        ["coda"] = "Coda",
        ["pre-chorus"] = "Pre-Chorus",
        ["prechorus"] = "Pre-Chorus",
        ["post-chorus"] = "Post-Chorus",
        ["postchorus"] = "Post-Chorus",
    };

    // Bare heading line: modifier words (Final Chorus / Second Final Chorus) + section name,
    // optional trailing colon/period ("Verse1:", "Chorus."). ST-60: mirrors the parser's widened
    // SectionHeading rule — multiple modifiers and numbered variants ("Chorus 2", "Verse 2a",
    // "Bridge 3") are headings too. The name group captures the number ("Verse 2a").
    private static readonly Regex BareHeading = new(
        @"^(?<mod>(?:[A-Za-z]+\s+)*?)?(?<name>Intro|Outro|Interlude|Pre-?Chorus|Post-?Chorus|Verse\s*\d*[a-z]?|Chorus(\s*\d*[a-z]?)?|Bridge(\s*\d*[a-z]?)?)[.:]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Normalizes the text; idempotent. Never throws.</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input ?? "";

        try
        {
            var lines = input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var normalized = NormalizeLine(trimmed);
                // Untouched lines keep their original whitespace.
                lines[i] = normalized == trimmed ? lines[i] : normalized;
            }
            return string.Join("\n", lines);
        }
        catch
        {
            return input;
        }
    }

    private static string NormalizeLine(string line)
    {
        if (line.Length == 0)
            return line;

        // Whole-line directive: {name} / {name: value} / {name value}.
        if (line.Length >= 2 && line[0] == '{' && line[^1] == '}')
        {
            var inner = line[1..^1].Trim();
            var colonIdx = inner.IndexOf(':');
            var spaceIdx = inner.IndexOf(' ');
            string name, value;
            if (colonIdx > 0)
                (name, value) = (inner[..colonIdx].Trim(), inner[(colonIdx + 1)..].Trim());
            else if (spaceIdx > 0)
                (name, value) = (inner[..spaceIdx].Trim(), inner[(spaceIdx + 1)..].Trim());
            else
                (name, value) = (inner, "");

            // Shorthand header with a value: {t:Africa} → {title: Africa}. A tempo-form
            // {t:...} (120 / 120 bpm / 4/4) is a tempo/meter marking — leave as written (ST-60).
            if (!string.IsNullOrEmpty(value) && HeaderShorthands.TryGetValue(name, out var full))
                return name == "t" && TempoForm.IsMatch(value)
                    ? line
                    : $"{{{full}: {value}}}";

            // Bare section alias: {soc} → {chorus}, {Chorus} → {chorus}, {intro} → {comment: Intro}.
            if (value.Length == 0)
            {
                if (SectionAliases.TryGetValue(name, out var section))
                    return $"{{{section}}}";
                if (CommentSections.TryGetValue(name, out var label))
                    return "{comment: " + label + "}";
            }

            return line; // unknown directive — leave as written
        }

        // Bare heading line: "Verse1:", "Chorus", "Final Chorus." → typed directive.
        var m = BareHeading.Match(line);
        if (m.Success)
        {
            var name = m.Groups["name"].Value;
            var mod = m.Groups["mod"].Value.Trim();

            if (name.StartsWith("Verse", StringComparison.OrdinalIgnoreCase))
            {
                var num = Regex.Match(name, @"\d+").Value;
                var baseLabel = num.Length > 0 ? $"Verse {num}" : "Verse";
                return mod.Length > 0
                    ? $"{{verse label=\"{mod} {baseLabel}\"}}"
                    : (num.Length > 0 ? $"{{verse label=\"Verse {num}\"}}" : "{verse}");
            }

            if (name.StartsWith("Chorus", StringComparison.OrdinalIgnoreCase))
            {
                // ST-60: numbered variants carry the number in the label ("Chorus 2").
                var label = name.TrimEnd(':', '.').Trim();
                return label.Equals("Chorus", StringComparison.OrdinalIgnoreCase)
                    ? (mod.Length > 0 ? $"{{chorus label=\"{mod} Chorus\"}}" : "{chorus}")
                    : $"{{chorus label=\"{(mod.Length > 0 ? $"{mod} {label}" : label)}\"}}";
            }

            if (name.StartsWith("Bridge", StringComparison.OrdinalIgnoreCase))
            {
                var label = name.TrimEnd(':', '.').Trim();
                return label.Equals("Bridge", StringComparison.OrdinalIgnoreCase)
                    ? (mod.Length > 0 ? $"{{bridge label=\"{mod} Bridge\"}}" : "{bridge}")
                    : $"{{bridge label=\"{(mod.Length > 0 ? $"{mod} {label}" : label)}\"}}";
            }

            // Intro / Outro / Interlude / Pre-Chorus / Post-Chorus → comment heading.
            var clean = line.TrimEnd(':', '.').Trim();
            return "{comment: " + clean + "}";
        }

        return line;
    }
}
