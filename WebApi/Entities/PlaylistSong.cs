namespace WebApi.Entities;

/// <summary>Ordered join table between <see cref="Playlist"/> and <see cref="Song"/>.</summary>
public class PlaylistSong
{
    public Guid PlaylistId { get; set; }
    public Guid SongId { get; set; }
    public int Position { get; set; }
}
