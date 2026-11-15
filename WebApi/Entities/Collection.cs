namespace WebApi.Entities;

public class Collection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string Category { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
