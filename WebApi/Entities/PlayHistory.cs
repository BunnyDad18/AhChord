namespace WebApi.Entities;

/// <summary>Drives the "Recent songs" list.</summary>
public class PlayHistory
{
    public Guid Id { get; set; }
    public Guid SongId { get; set; }
    public string? UserId { get; set; }
    public DateTime ViewedAt { get; set; }
}
