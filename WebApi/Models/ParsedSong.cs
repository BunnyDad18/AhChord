namespace WebApi.Models;

/// <summary>
/// A structured, read-time projection of a song's raw ChordPro text.
/// The raw text (<c>Song.ChordProText</c>) remains the source of truth; this view is
/// derived on read and is never written back.
/// </summary>
public record ParsedSong
{
    public Header? Header { get; set; }
    public List<Section> Sections { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Song-level metadata from the ChordPro header directives.</summary>
public record Header
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Key { get; set; }
    public string? Capo { get; set; }
    public string? Time { get; set; }
    public string? Tag { get; set; }
    /// <summary>ST-35 F2: from a <c>{genre: ...}</c> directive (the LLM fills it during conversion).</summary>
    public string? Genre { get; set; }
    /// <summary>ST-35 F2: release year from a <c>{year: YYYY}</c> directive, as written.</summary>
    public string? Year { get; set; }
    public List<MetaField> Meta { get; set; } = new();
}

/// <summary>A <c>{meta: name value}</c> field.</summary>
public record MetaField
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>A labelled block of lyric lines.</summary>
public record Section
{
    public string? Label { get; set; }
    /// <summary>"verse" | "chorus" | "bridge" | "other".</summary>
    public string Type { get; set; } = "other";
    public List<Line> Lines { get; set; } = new();
}

/// <summary>A single lyric line with its chord/annotation/bar projections.</summary>
public record Line
{
    /// <summary>The line as written (chord, annotation and bar tokens intact).</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary><c>[Chord]</c> tokens; <see cref="Chord.CharIndex"/> is the 0-based index of the '[' in <see cref="Text"/>.</summary>
    public List<Chord> Chords { get; set; } = new();
    /// <summary><c>[*annotation]</c> tokens; <see cref="Annotation.CharIndex"/> is the 0-based index of the '[' in <see cref="Text"/>.</summary>
    public List<Annotation> Annotations { get; set; } = new();
    /// <summary>ST-54 bar notation tokens (<c>[|]</c>, <c>[||:]</c>, <c>[:||]</c>); <see cref="Bar.CharIndex"/> is the 0-based index of the '[' in <see cref="Text"/>.</summary>
    public List<Bar> Bars { get; set; } = new();
}

/// <summary>A chord token; <see cref="Name"/> is returned exactly as written (no transposition — see ST-08).</summary>
public record Chord
{
    public string Name { get; set; } = string.Empty;
    public int CharIndex { get; set; }
}

/// <summary>A <c>[*...]*]</c> annotation token.</summary>
public record Annotation
{
    public string Text { get; set; } = string.Empty;
    public int CharIndex { get; set; }
}

/// <summary>A ST-54 bar notation token. <see cref="Kind"/> is "bar" | "repeat-start" | "repeat-end".</summary>
public record Bar
{
    public string Kind { get; set; } = string.Empty;
    public int CharIndex { get; set; }
}
