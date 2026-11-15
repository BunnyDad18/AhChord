using WebApi.Endpoints;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-77 (bug report #116): the display projection shifts the chart by
/// (session transpose − saved capo) — the exact SongEndpoints.ProjectionShift
/// formula GET /api/songs/{id}?capo=N applies to the parsed chart. The capo moves
/// the shapes down and keeps the key; the transpose moves both.
/// </summary>
public class St77CapoModelTests
{
    [Fact]
    public void ProjectionShift_CapoMovesShapesDown()
    {
        Assert.Equal(-2, SongEndpoints.ProjectionShift(0, 2));
        Assert.Equal(0, SongEndpoints.ProjectionShift(2, 2));
        Assert.Equal(2, SongEndpoints.ProjectionShift(2, 0));
        Assert.Equal(0, SongEndpoints.ProjectionShift(null, 0));
    }

    [Fact]
    public void CapoOnly_ShapesMoveDown_KeyStays()
    {
        // G chart, capo 2, no transpose: shapes drop to F so the capoed guitar sounds G.
        Assert.Equal("F", ChordTransposer.Transpose("G", SongEndpoints.ProjectionShift(0, 2)));
        Assert.Equal("Am", ChordTransposer.Transpose("Bm", SongEndpoints.ProjectionShift(0, 2)));
    }

    [Fact]
    public void TransposeMovesShapesAndKey()
    {
        // G chart, transpose +2, no capo: shapes and key both land on A.
        Assert.Equal("A", ChordTransposer.Transpose("G", SongEndpoints.ProjectionShift(2, 0)));
    }

    [Fact]
    public void TransposeMatchesCapo_AsWritten()
    {
        // G chart, capo 2, transpose +2: shift 0 — the written chart is what's shown.
        Assert.Equal("G", ChordTransposer.Transpose("G", SongEndpoints.ProjectionShift(2, 2)));
    }

    [Fact]
    public void NoCapoNoTranspose_Unchanged()
    {
        Assert.Equal("Em7", ChordTransposer.Transpose("Em7", SongEndpoints.ProjectionShift(0, 0)));
    }
}
