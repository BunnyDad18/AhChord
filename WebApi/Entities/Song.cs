namespace WebApi.Entities;

public class Song
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public int? Year { get; set; }
    public string? Key { get; set; }
    /// <summary>
    /// ST-106: the Key is derived from the chart's chords (<see cref="KeyAnalyzer"/>).
    /// A hand-set key sets this flag; auto-derivation then never overwrites it, and
    /// the editor's "detect from chords" action clears it again.
    /// </summary>
    public bool KeyOverride { get; set; }
    /// <summary>ST-106: confidence 0–1 of the last derived key (null = key was never derived).</summary>
    public double? KeyConfidence { get; set; }
    /// <summary>ST-35 F2/D4: musical genre — populated by the LLM during conversion, editable in the UI.</summary>
    public string? Genre { get; set; }
    /// <summary>ST-35 F3: approximate tempo (BPM) — proposed by the metadata review job.</summary>
    public int? Tempo { get; set; }
    public int CapoFret { get; set; }
    public string ChordProText { get; set; } = null!;
    public string? SourceUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
