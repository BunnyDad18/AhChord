namespace WebApi.Entities;

/// <summary>
/// ST-14: one row per in-app chord-page→ChordPro conversion attempt. The LLM job (Hangfire)
/// fills <see cref="ChordProText"/> on success or <see cref="Error"/> on failure.
/// <see cref="SavedSongId"/> links back to the Song created by POST /api/import/convert/{id}/save.
/// </summary>
public class ImportDraft
{
    public Guid Id { get; set; }

    /// <summary>ST-30: the user who started the conversion — the job resolves *their* LLM settings.</summary>
    public string? UserId { get; set; }

    /// <summary>queued | running | succeeded | failed</summary>
    public string Status { get; set; } = "queued";

    /// <summary>Legacy column: a source URL recorded on drafts from the old fetch-based path.
    /// New drafts are paste-only and leave this null; kept so pre-existing rows keep rendering
    /// their source label and the save flow can carry it to the song's SourceUrl.</summary>
    public string? InputUrl { get; set; }

    /// <summary>The pasted chord-page text. Kept for retry/debug.</summary>
    public string? InputText { get; set; }

    /// <summary>ST-37 A1: song name found at queue time (title search of the input) — shown on the card.</summary>
    public string? InputPreview { get; set; }

    /// <summary>LLM output, validated by the ChordPro parser before the status flips to succeeded.</summary>
    public string? ChordProText { get; set; }

    /// <summary>Human-readable failure reason (LLM error, parser rejection, …).</summary>
    public string? Error { get; set; }

    /// <summary>ST-35 E1: title/artist extracted from the converted ChordPro header — shown on the queue row.</summary>
    public string? ExtractedTitle { get; set; }
    public string? ExtractedArtist { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public Guid? SavedSongId { get; set; }
}
