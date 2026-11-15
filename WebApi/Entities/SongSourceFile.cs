namespace WebApi.Entities;

/// <summary>
/// ST-46 (bug-56): a source file attached to a song — the original PDF/PNG/JPG the chart was
/// taken from. Stored as bytes in Postgres (single-container model: no path management, cleanup
/// on delete is a row delete). Served inline so it can be viewed on the song page; 10 MB cap.
/// </summary>
public class SongSourceFile
{
    public Guid Id { get; set; }
    public Guid SongId { get; set; }
    public string FileName { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTime CreatedAt { get; set; }
}
