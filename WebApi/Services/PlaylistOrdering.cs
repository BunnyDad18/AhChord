namespace WebApi.Services;

/// <summary>
/// Pure ordering logic for playlist songs (ST-10). Positions are 1-based and contiguous.
/// Kept separate from the database so the insert/reorder rules are unit-testable in isolation.
/// </summary>
public static class PlaylistOrdering
{
    /// <summary>
    /// Returns the new member order after inserting <paramref name="newId"/> at the 1-based
    /// <paramref name="position"/> (clamped to [1, count+1]). Assumes <paramref name="newId"/>
    /// is not already in <paramref name="current"/>.
    /// </summary>
    public static List<Guid> InsertAt(IReadOnlyList<Guid> current, Guid newId, int position)
    {
        var list = current.ToList();
        var pos = Math.Clamp(position, 1, list.Count + 1);
        list.Insert(pos - 1, newId);
        return list;
    }

    /// <summary>
    /// True when <paramref name="requested"/> is exactly the current membership — the same songs,
    /// in any order, with no duplicates and nothing missing/extra. I.e. a valid full reorder.
    /// </summary>
    public static bool IsExactReorder(IReadOnlyList<Guid> current, IReadOnlyList<Guid> requested)
    {
        if (requested.Count != current.Count)
            return false;
        if (requested.Distinct().Count() != requested.Count)
            return false; // duplicates
        var set = current.ToHashSet();
        return requested.All(set.Contains);
    }
}
