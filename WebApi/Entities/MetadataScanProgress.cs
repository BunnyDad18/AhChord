namespace WebApi.Entities;

/// <summary>
/// ST-68: progress of the (single) full-library metadata scan — one row (Key = "library").
/// The scan loop updates it per song so the Songs page can show a live progress strip
/// (current song, n/total, elapsed, ETA). Overwritten by the next full scan; rows left
/// "running" by a server restart are marked "interrupted" at startup.
/// </summary>
public class MetadataScanProgress
{
    public string Key { get; set; } = "library";

    /// <summary>"running" | "done" | "interrupted" (restart mid-scan).</summary>
    public string Status { get; set; } = "running";

    /// <summary>Title of the song being worked on (null before the first song / after the scan).</summary>
    public string? CurrentTitle { get; set; }

    /// <summary>Songs processed so far (1-based once the first song starts).</summary>
    public int Index { get; set; }

    /// <summary>Library size when the scan started.</summary>
    public int Total { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>Proposals created + auto-applied by this scan so far (per-song count from the job).</summary>
    public int ProposalsAdded { get; set; }
}
