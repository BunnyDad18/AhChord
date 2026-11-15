namespace WebApi.Entities;

public class Playlist
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public Guid? CollectionId { get; set; }
    public Collection? Collection { get; set; }
    public bool IsSetlist { get; set; }
    /// <summary>Bug report #99: the gig this playlist is for (date-only, UTC midnight).</summary>
    public DateTime? GigDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
