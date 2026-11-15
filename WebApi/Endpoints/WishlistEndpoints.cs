using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;

namespace WebApi.Endpoints;

/// <summary>
/// /api/wishlist endpoints (ST-44): the per-user wishlist — songs the user wants chords for but
/// has no readable source for yet. Status lifecycle: waiting → found → converted. The /api group
/// is RequireAuthorization by default (see Program.cs).
/// </summary>
public static class WishlistEndpoints
{
    private const int MaxTitleLength = 200;
    private const int MaxListSize = 200;

    public static RouteGroupBuilder MapWishlistEndpoints(this RouteGroupBuilder api)
    {
        var wishlist = api.MapGroup("wishlist");

        // POST /api/wishlist — add a song (status "waiting").
        wishlist.MapPost("", async (CreateWishlistRequest request, HttpContext http, ApplicationDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
                return Error(400, "validation-failed", "'title' is required.");

            var title = request.Title.Trim();
            if (title.Length > MaxTitleLength)
                title = title[..MaxTitleLength];

            var item = new WishlistItem
            {
                Id = Guid.NewGuid(),
                UserId = CurrentUserId(http),
                Title = title,
                Artist = string.IsNullOrWhiteSpace(request.Artist) ? null : request.Artist.Trim(),
                SourceUrl = string.IsNullOrWhiteSpace(request.SourceUrl) ? null : request.SourceUrl.Trim(),
                Status = "waiting",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.WishlistItems.Add(item);
            await db.SaveChangesAsync();
            return Results.Json(WishlistItemDto.From(item), statusCode: StatusCodes.Status201Created);
        });

        // GET /api/wishlist — the caller's wishlist, newest first.
        wishlist.MapGet("", async (HttpContext http, ApplicationDbContext db) =>
        {
            var userId = CurrentUserId(http);
            var items = await db.WishlistItems.AsNoTracking()
                .Where(w => w.UserId == userId)
                .OrderByDescending(w => w.CreatedAt)
                .Take(MaxListSize)
                .ToListAsync();
            return Results.Ok(items.Select(WishlistItemDto.From));
        });

        // PATCH /api/wishlist/{id} — advance the status and/or link the saved song.
        wishlist.MapPatch("/{id:guid}", async (Guid id, UpdateWishlistRequest request, HttpContext http, ApplicationDbContext db) =>
        {
            var userId = CurrentUserId(http);
            var item = await db.WishlistItems.FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId);
            if (item is null)
                return Error(404, "not-found", "Wishlist item not found.");

            if (!string.IsNullOrWhiteSpace(request.Status))
            {
                var status = request.Status.Trim().ToLowerInvariant();
                if (status is not ("waiting" or "found" or "converted"))
                    return Error(400, "validation-failed", "'status' must be 'waiting', 'found' or 'converted'.");

                // Converting requires the saved song id — and that song must exist in the library.
                if (status == "converted")
                {
                    var songId = request.ConvertedSongId ?? item.ConvertedSongId;
                    if (songId is null)
                        return Error(400, "validation-failed", "'convertedSongId' is required when setting status to 'converted'.");
                    if (!await db.Songs.AnyAsync(s => s.Id == songId))
                        return Error(400, "validation-failed", "The given song does not exist in the library.");
                    item.ConvertedSongId = songId;
                }

                item.Status = status;
            }
            else if (request.ConvertedSongId is not null)
            {
                if (!await db.Songs.AnyAsync(s => s.Id == request.ConvertedSongId))
                    return Error(400, "validation-failed", "The given song does not exist in the library.");
                item.ConvertedSongId = request.ConvertedSongId;
            }

            item.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Json(WishlistItemDto.From(item));
        });

        // DELETE /api/wishlist/{id} — remove the item (409 when already converted).
        wishlist.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ApplicationDbContext db) =>
        {
            var userId = CurrentUserId(http);
            var item = await db.WishlistItems.FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId);
            if (item is null)
                return Error(404, "not-found", "Wishlist item not found.");
            if (item.Status == "converted")
                return Error(409, "already-converted",
                    "This item was converted to a song — it stays on the wishlist as a record of where it came from.");

            db.WishlistItems.Remove(item);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return api;
    }

    private static string CurrentUserId(HttpContext http)
        => http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static IResult Error(int statusCode, string code, string message)
        => Results.Json(new { error = new { code, message } }, statusCode: statusCode);
}
