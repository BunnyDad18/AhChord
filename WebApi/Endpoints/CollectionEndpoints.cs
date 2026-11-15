using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// /api/collections endpoints (ST-11). The /api group is RequireAuthorization by default (see
/// Program.cs), so no explicit [Authorize] is needed. Errors use the { error: { code, message,
/// fields? } } shape. A collection groups playlists by category (Worship / Events / Home / Other);
/// deleting it unassigns its playlists (CollectionId → null) — it never deletes them.
/// </summary>
public static class CollectionEndpoints
{
    /// <summary>Locked categories. Matched case-insensitively; stored in canonical form.</summary>
    private static readonly string[] Categories = { "Worship", "Events", "Home", "Other" };

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapCollectionEndpoints(this RouteGroupBuilder api)
    {
        var collections = api.MapGroup("collections");

        // GET /api/collections?category=<Worship|Events|Home|Other>
        collections.MapGet("/", async (
            ApplicationDbContext db,
            string? category) =>
        {
            var query = db.Collections.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(category))
            {
                // Case-insensitive exact match on the stored category.
                var term = category.Trim();
                query = query.Where(c => EF.Functions.ILike(c.Category, EscapeLike(term), "\\"));
            }

            var items = await query
                .OrderBy(c => c.Name)
                .ThenBy(c => c.Id)
                .Select(c => new CollectionDto(
                    c.Id, c.Name, c.Description, c.Category,
                    db.Playlists.Count(p => p.CollectionId == c.Id),
                    c.CreatedAt))
                .ToListAsync();
            return Results.Ok(items);
        });

        // POST /api/collections -> 201
        collections.MapPost("/", async (CreateCollectionRequest request, ApplicationDbContext db) =>
        {
            var (category, fields) = ValidateRequest(request.Name, request.Category, requireName: true);
            if (fields.Count > 0)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.", fields);

            var now = DateTime.UtcNow;
            var collection = new Collection
            {
                Id = Guid.NewGuid(),
                Name = request.Name!.Trim(),
                Description = Clean(request.Description),
                Category = category,
                CreatedAt = now
            };
            db.Collections.Add(collection);
            await db.SaveChangesAsync();
            return Results.Json(ToDto(collection, 0), statusCode: StatusCodes.Status201Created);
        });

        // GET /api/collections/{id} -> CollectionDetailDto (playlists ordered by name)
        collections.MapGet("/{id:guid}", async (Guid id, ApplicationDbContext db) =>
        {
            var collection = await db.Collections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (collection is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Collection not found.");

            var playlists = await db.Playlists.AsNoTracking()
                .Where(p => p.CollectionId == id)
                .OrderBy(p => p.Name)
                .ThenBy(p => p.Id)
                .ToListAsync();
            // ST-65: one grouped count query (was a CountAsync per playlist — N+1).
            var plIds = playlists.Select(p => p.Id).ToList();
            var counts = await db.PlaylistSongs.AsNoTracking()
                .Where(ps => plIds.Contains(ps.PlaylistId))
                .GroupBy(ps => ps.PlaylistId)
                .Select(g => new { PlaylistId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.PlaylistId, x => x.Count);
            var items = playlists
                .Select(p => ToPlaylistDto(p, counts.GetValueOrDefault(p.Id)))
                .ToList();

            return Results.Ok(new CollectionDetailDto(
                collection.Id, collection.Name, collection.Description, collection.Category,
                items.Count, collection.CreatedAt, items));
        });

        // PUT /api/collections/{id}
        collections.MapPut("/{id:guid}", async (Guid id, UpdateCollectionRequest request, HttpContext http, ApplicationDbContext db, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: renaming/re-categorising a collection rewrites structure
            // every other member sees.
            if (await Authz.DenySharedDestroyAsync(http, policy, "rename a collection") is { } denied)
                return denied;

            var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == id);
            if (collection is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Collection not found.");

            var (category, fields) = ValidateRequest(
                request.Name is null ? null : request.Name,
                request.Category,
                requireName: false);
            if (fields.Count > 0)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.", fields);

            if (request.Name is not null) collection.Name = request.Name.Trim();
            if (request.Description is not null) collection.Description = Clean(request.Description);
            if (request.Category is not null) collection.Category = category;
            await db.SaveChangesAsync();

            var playlistCount = await db.Playlists.CountAsync(p => p.CollectionId == id);
            return Results.Ok(ToDto(collection, playlistCount));
        });

        // DELETE /api/collections/{id} -> 204 (playlists become unassigned; they are not deleted)
        collections.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ApplicationDbContext db, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: destroying shared structure is the admin's call on an
            // internet-facing instance.
            if (await Authz.DenySharedDestroyAsync(http, policy, "delete a collection") is { } denied)
                return denied;

            var collection = await db.Collections.FirstOrDefaultAsync(c => c.Id == id);
            if (collection is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Collection not found.");
            db.Collections.Remove(collection);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return api;
    }

    /// <summary>
    /// Validates the mutable fields. Returns the canonical category (or the default "Other" when
    /// none was given) plus the field-errors bag (empty when valid).
    /// </summary>
    private static (string Category, Dictionary<string, string?> Fields) ValidateRequest(
        string? name, string? category, bool requireName)
    {
        var fields = new Dictionary<string, string?>();
        if (requireName)
        {
            if (string.IsNullOrWhiteSpace(name))
                fields["name"] = "Name is required.";
        }
        else if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            fields["name"] = "Name must not be empty.";
        }

        var resolved = "Other";
        if (category is not null)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                fields["category"] = "Category must be one of: Worship, Events, Home, Other.";
            }
            else
            {
                var match = Categories.FirstOrDefault(c =>
                    string.Equals(c, category.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    fields["category"] = "Category must be one of: Worship, Events, Home, Other.";
                else
                    resolved = match;
            }
        }

        return (resolved, fields);
    }

    /// <summary>Null/blank optional strings normalize to null; non-blank values are trimmed.</summary>
    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Escapes LIKE wildcards so a filter term is matched literally.</summary>
    private static string EscapeLike(string term)
        => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static PlaylistDto ToPlaylistDto(Playlist p, int songCount)
        => new(p.Id, p.Name, p.Description, p.CollectionId, p.IsSetlist, p.GigDate, songCount, p.CreatedAt, p.UpdatedAt);

    private static CollectionDto ToDto(Collection c, int playlistCount)
        => new(c.Id, c.Name, c.Description, c.Category, playlistCount, c.CreatedAt);

    private static IResult Error(int statusCode, string code, string message, Dictionary<string, string?>? fields = null)
        => Results.Json(new { error = new { code, message, fields } }, statusCode: statusCode, options: Json);
}
