using WebApi.Services;

namespace WebApi.Tests;

public class PlaylistOrderingTests
{
    // --- InsertAt -------------------------------------------------------------

    [Fact]
    public void InsertAt_Empty_SingleElement()
    {
        var c = Guid.NewGuid();
        Assert.Equal(new List<Guid> { c }, PlaylistOrdering.InsertAt(Array.Empty<Guid>(), c, 1));
    }

    [Fact]
    public void InsertAt_AtFront_PutsFirst()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        Assert.Equal(new List<Guid> { c, a, b }, PlaylistOrdering.InsertAt(new List<Guid> { a, b }, c, 1));
    }

    [Fact]
    public void InsertAt_Middle_ShiftsRest()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var d = Guid.NewGuid();
        Assert.Equal(new List<Guid> { a, d, b }, PlaylistOrdering.InsertAt(new List<Guid> { a, b }, d, 2));
    }

    [Fact]
    public void InsertAt_BeyondEnd_Appends()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        Assert.Equal(new List<Guid> { a, b, c }, PlaylistOrdering.InsertAt(new List<Guid> { a, b }, c, 99));
    }

    [Fact]
    public void InsertAt_BelowOne_ClampsToFront()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        Assert.Equal(new List<Guid> { c, a, b }, PlaylistOrdering.InsertAt(new List<Guid> { a, b }, c, 0));
    }

    // --- IsExactReorder -------------------------------------------------------

    [Fact]
    public void IsExactReorder_SameSetDifferentOrder_True()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.True(PlaylistOrdering.IsExactReorder(new List<Guid> { a, b }, new List<Guid> { b, a }));
    }

    [Fact]
    public void IsExactReorder_IdenticalOrder_True()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.True(PlaylistOrdering.IsExactReorder(new List<Guid> { a, b }, new List<Guid> { a, b }));
    }

    [Fact]
    public void IsExactReorder_DuplicateWithMissing_False()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        // Same count as current, but a is duplicated and b is missing.
        Assert.False(PlaylistOrdering.IsExactReorder(new List<Guid> { a, b }, new List<Guid> { a, a }));
    }

    [Fact]
    public void IsExactReorder_MissingOne_False()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.False(PlaylistOrdering.IsExactReorder(new List<Guid> { a, b }, new List<Guid> { a }));
    }

    [Fact]
    public void IsExactReorder_ExtraSong_False()
    {
        var a = Guid.NewGuid(); var c = Guid.NewGuid();
        Assert.False(PlaylistOrdering.IsExactReorder(new List<Guid> { a }, new List<Guid> { a, c }));
    }

    [Fact]
    public void IsExactReorder_BothEmpty_True()
    {
        Assert.True(PlaylistOrdering.IsExactReorder(Array.Empty<Guid>(), Array.Empty<Guid>()));
    }
}
