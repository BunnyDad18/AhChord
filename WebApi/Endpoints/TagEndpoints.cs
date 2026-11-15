using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// ST-106: GET /api/tags — the tag table for autocomplete and the tag browser.
/// <c>kind</c> is "artist" | "genre" | "tag" (omit = all kinds), <c>q</c> is a
/// case-insensitive substring over the name. Sorted by count desc, name asc —
/// autocomplete wants the useful names first.
/// </summary>
public static class TagEndpoints
{
    public static RouteGroupBuilder MapTagEndpoints(this RouteGroupBuilder api)
    {
        var tags = api.MapGroup("tags");

        tags.MapGet("/", async (ApplicationDbContext db, string? kind, string? q) =>
        {
            var kindFilter = ParseKind(kind);
            if (kind is not null && kindFilter is null)
            {
                return Results.Json(
                    new { error = new { code = "validation-failed", message = "kind must be artist, genre, or tag." } },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var query = db.Tags.AsNoTracking();
            if (kindFilter is { } k)
            {
                query = query.Where(t => t.Kind == k);
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim().ToLowerInvariant();
                query = query.Where(t => t.NormalizedName.Contains(needle));
            }

            // Counts per tag (songs for every kind, playlists for Kind=Tag). The table is
            // tiny (a few hundred rows) — group in memory over the joins, like the facets do.
            var songCounts = (await db.SongTagLinks.AsNoTracking()
                    .GroupBy(l => l.TagId)
                    .Select(g => new { TagId = g.Key, Count = g.Count() })
                    .ToListAsync())
                .ToDictionary(x => x.TagId, x => x.Count);

            var playlistCounts = (await db.PlaylistTagLinks.AsNoTracking()
                    .GroupBy(l => l.TagId)
                    .Select(g => new { TagId = g.Key, Count = g.Count() })
                    .ToListAsync())
                .ToDictionary(x => x.TagId, x => x.Count);

            var rows = await query.ToListAsync();
            var dtos = rows
                .Select(t => new TagDto(
                    KindName(t.Kind),
                    t.Name,
                    songCounts.GetValueOrDefault(t.Id),
                    playlistCounts.GetValueOrDefault(t.Id)))
                .OrderByDescending(d => d.SongCount + d.PlaylistCount)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(dtos);
        });

        return api;
    }

    private static TagKind? ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "artist" => TagKind.Artist,
        "genre" => TagKind.Genre,
        "tag" => TagKind.Tag,
        null or "" => null,
        _ => null,
    };

    private static string KindName(TagKind kind) => kind switch
    {
        TagKind.Artist => "artist",
        TagKind.Genre => "genre",
        _ => "tag",
    };
}
