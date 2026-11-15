using WebApi.Entities;

namespace WebApi.Dtos;

/// <summary>POST /api/wishlist (ST-44) — add a song to the wishlist (status "waiting").</summary>
public record CreateWishlistRequest(string Title, string? Artist = null, string? SourceUrl = null);

/// <summary>PATCH /api/wishlist/{id} (ST-44) — advance the status and/or link the saved song.</summary>
public record UpdateWishlistRequest(string? Status = null, Guid? ConvertedSongId = null);

/// <summary>The wishlist item as returned by GET/POST/PATCH /api/wishlist (ST-44).</summary>
public record WishlistItemDto(
    Guid Id,
    string Title,
    string? Artist,
    string? SourceUrl,
    string Status,
    Guid? ConvertedSongId,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static WishlistItemDto From(WishlistItem w) => new(
        w.Id, w.Title, w.Artist, w.SourceUrl, w.Status, w.ConvertedSongId, w.CreatedAt, w.UpdatedAt);
}
