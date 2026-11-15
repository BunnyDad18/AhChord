namespace WebApi.Services;

/// <summary>
/// Pure, side-effect-free selection logic for the ST-09 random + recent endpoints.
/// Kept separate from the database so the "pick" and "dedupe/order/limit" rules are
/// unit-testable in isolation. The endpoints fetch the candidate rows and hand them here.
/// </summary>
public static class SongService
{
    /// <summary>A play-history row — the only fields the recent ordering cares about.</summary>
    public sealed class HistoryRow
    {
        public Guid SongId { get; set; }
        public string? UserId { get; set; }
        public DateTime ViewedAt { get; set; }
    }

    /// <summary>
    /// Picks a uniformly random element from <paramref name="songIds"/>.
    /// Returns <c>null</c> when the set is empty (the endpoint maps that to a 404).
    /// </summary>
    public static Guid? PickRandom(IReadOnlyList<Guid> songIds)
    {
        if (songIds.Count == 0)
            return null;
        return songIds[Random.Shared.Next(songIds.Count)];
    }

    /// <summary>
    /// Orders a user's play history so each song appears exactly once (the latest view wins),
    /// most recent first, capped at <paramref name="limit"/>. Rows for other users are ignored.
    /// </summary>
    public static List<Guid> OrderRecent(IEnumerable<HistoryRow> rows, string? userId, int limit)
    {
        if (limit <= 0)
            limit = 1;
        return rows
            .Where(r => r.UserId == userId)
            .GroupBy(r => r.SongId)
            .Select(g => new { SongId = g.Key, Last = g.Max(r => r.ViewedAt) })
            .OrderByDescending(x => x.Last)
            .Take(limit)
            .Select(x => x.SongId)
            .ToList();
    }
}
