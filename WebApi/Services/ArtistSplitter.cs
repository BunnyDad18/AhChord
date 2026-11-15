using System.Text.RegularExpressions;

namespace WebApi.Services;

/// <summary>
/// ST-106: the conservative artist-string splitter for the tag backfill. Splits on
/// commas and feat./ft./featuring, strips Words by / Music by credit prefixes, and
/// deliberately does NOT split on " & " — it is a band name as often as a duo
/// ("Bryan &amp; Katie Torwalt", "The Mamas &amp; The Papas" vs "Keith Getty &amp; Stuart
/// Townend"), so those stay one chip and get split by hand in the editor.
/// </summary>
public static partial class ArtistSplitter
{
    [GeneratedRegex(@",|\s+featuring\s+|\s+feat\.?\s+|\s+ft\.?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Splitter();

    [GeneratedRegex(@"^(?:words by|music by|lyrics by|text by|lyrics and music by)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex CreditPrefix();

    /// <summary>Names in written order, trimmed, de-duplicated ignore-case; empty input → empty.</summary>
    public static IReadOnlyList<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return Splitter().Split(raw)
            .Select(part => CreditPrefix().Replace(part.Trim(), string.Empty))
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
