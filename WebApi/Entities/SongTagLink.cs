namespace WebApi.Entities;

/// <summary>ST-106: many-to-many between <see cref="Song"/> and <see cref="Tag"/> (any Kind).
/// Position is the artist-list order (1..n, written order = display order); genres and
/// free tags use 0 and sort alphabetically in the read shape.</summary>
public class SongTagLink
{
    public Guid SongId { get; set; }
    public Guid TagId { get; set; }
    public int Position { get; set; }
}
