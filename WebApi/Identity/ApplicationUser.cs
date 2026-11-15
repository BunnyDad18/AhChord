using Microsoft.AspNetCore.Identity;

namespace WebApi.Identity;

/// <summary>Application user. Identity stores email/username; DisplayName and IsAdmin are ours.</summary>
public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }

    /// <summary>ST-30: the first registered user is admin (may edit the band-wide LLM default).</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Registration time — this Identity version has no built-in user timestamp.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>ST-70c: profile image (PNG/JPG/WebP bytes, 1 MB cap — bytes in Postgres
    /// like source files). Served by anonymous GET /api/users/{id}/avatar: an &lt;img&gt;
    /// tag can't send the Bearer header, and a LAN avatar is no more sensitive than
    /// the display name shown across the app. Null = no image (initials avatar).</summary>
    public byte[]? AvatarBytes { get; set; }

    public string? AvatarContentType { get; set; }

    /// <summary>Cache-bust stamp: the frontend embeds it in the avatar URL (?v=),
    /// so uploading a new image invalidates the browser cache.</summary>
    public DateTime? AvatarUpdatedAt { get; set; }
}
