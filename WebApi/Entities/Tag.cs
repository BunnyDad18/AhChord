namespace WebApi.Entities;

/// <summary>
/// ST-106 (planned 2026-11-14): the label kind. Artists and Genres are multi-valued
/// song metadata; free-form Tags attach to songs <em>and</em> playlists (setlist
/// grouping — collections retire into Tags).
/// </summary>
public enum TagKind
{
    Artist = 1,
    Genre = 2,
    Tag = 3,
}

/// <summary>
/// ST-106: a normalised label. Uniqueness is per (Kind, <see cref="NormalizedName"/>) —
/// artists and free tags keep the spelling first entered; genre names are canonicalised
/// to title case (pop/Pop/POP are one "Pop"). The stored <see cref="Name"/> is display,
/// <see cref="NormalizedName"/> is the match key; <c>Services/TagName.cs</c> owns both.
/// </summary>
public class Tag
{
    public Guid Id { get; set; }
    public TagKind Kind { get; set; }

    /// <summary>Display spelling, trimmed (genres title-cased).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-cased match key (trimmed, genres post-title-case) — unique per Kind.</summary>
    public string NormalizedName { get; set; } = string.Empty;
}
