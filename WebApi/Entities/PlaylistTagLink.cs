namespace WebApi.Entities;

/// <summary>
/// ST-106: many-to-many between <see cref="Playlist"/> and <see cref="Tag"/>.
/// Only Kind=Tag links appear here (artists/genres are song-only).
/// </summary>
public class PlaylistTagLink
{
    public Guid PlaylistId { get; set; }
    public Guid TagId { get; set; }
}
