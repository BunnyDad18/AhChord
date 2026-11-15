namespace WebApi.Services;

/// <summary>
/// ST-63 (finding 5): bounded user-supplied text. Unbounded fields are storage DoS with a
/// runtime tail: ChordProText is re-parsed on every view and embedded in every metadata-scan
/// prompt, so a 50 MB chart is permanent CPU/queue amplification, not just disk. The song
/// endpoints, the import service, and the conversion draft creation all consult these limits.
/// </summary>
public static class InputLimits
{
    public const int MaxMetaFieldLength = 200;   // title / artist / album / key / genre
    public const int MaxChordProLength = 500_000; // ~500 KB chart
    public const int MaxUrlLength = 2048;         // browsers cap addresses around this size
    public const int MaxDisplayNameLength = 100;

    /// <summary>Cap for pasted conversion input — a multi-MB prompt would flood the local GPU queue.</summary>
    public const int MaxConversionInputBytes = 512 * 1024;

    /// <summary>Cap for reading an upstream (member-supplied LLM endpoint) response body.</summary>
    public const int MaxResponseProbeBytes = 512 * 1024;

    /// <summary>Adds "too long" field errors; a null argument means "not sent" (PATCH semantics).</summary>
    public static void AddFieldLimits(Dictionary<string, string?> fields,
        string? title, string? artist, string? album, string? key, string? genre,
        string? chordProText, string? sourceUrl)
    {
        Check(fields, "title", title, MaxMetaFieldLength);
        Check(fields, "artist", artist, MaxMetaFieldLength);
        Check(fields, "album", album, MaxMetaFieldLength);
        Check(fields, "key", key, MaxMetaFieldLength);
        Check(fields, "genre", genre, MaxMetaFieldLength);
        Check(fields, "chordProText", chordProText, MaxChordProLength);
        Check(fields, "sourceUrl", sourceUrl, MaxUrlLength);
    }

    private static void Check(Dictionary<string, string?> fields, string name, string? value, int max)
    {
        if (value is not null && value.Trim().Length > max)
            fields[name] = $"{name} is limited to {max:N0} characters.";
    }
}
