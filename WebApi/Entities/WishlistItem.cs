namespace WebApi.Entities;

/// <summary>
/// ST-44: a song the user wants chords for but doesn't have a readable source for yet.
/// Status lifecycle: "waiting" → "found" (chords pasted / conversion queued) → "converted"
/// (a Song was saved from it). The wishlist is per-user; converted items keep their
/// ConvertedSongId as a link back into the library.
/// </summary>
public class WishlistItem
{
    public Guid Id { get; set; }

    /// <summary>The owner — the wishlist is per-user.</summary>
    public string UserId { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string? Artist { get; set; }

    /// <summary>Where the user found (or plans to find) chords — optional link.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>"waiting" | "found" | "converted".</summary>
    public string Status { get; set; } = "waiting";

    /// <summary>The Song created when the conversion was saved (status == "converted").</summary>
    public Guid? ConvertedSongId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
