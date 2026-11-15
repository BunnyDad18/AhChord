using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;

namespace WebApi.Services;

/// <summary>
/// ST-61: authorization decisions that must NOT be frozen into the 30-day JWT.
///
/// The <c>is_admin</c> claim (JwtTokenService) stays as a display hint (the login response
/// tells the UI which panels to show), but every gate loads the user row by <c>sub</c>:
/// a demotion in the database takes effect on the next request, not up to 43200 minutes
/// later. The endpoints and the Hangfire dashboard filter both go through here.
/// </summary>
public static class Authz
{
    /// <summary>
    /// The user id carried by the token. JwtBearer's default inbound claim mapping RENAMES
    /// <c>sub</c> to <see cref="ClaimTypes.NameIdentifier"/>, so reading "sub" alone finds
    /// nothing on a real request — every consumer here checks both names.
    /// </summary>
    public static string? Subject(ClaimsPrincipal? principal)
        => principal?.FindFirstValue("sub") ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>True when the request's user (by sub claim) exists and IsAdmin=true in the database.</summary>
    public static async Task<bool> IsAdminAsync(HttpContext http)
    {
        var sub = Subject(http.User);
        if (string.IsNullOrEmpty(sub))
            return false;

        var db = http.RequestServices.GetService<ApplicationDbContext>();
        if (db is null)
            return false;

        return await db.Users.AnyAsync(u => u.Id == sub && u.IsAdmin);
    }

    /// <summary>
    /// Public-demo gate 3: on an instance that faces the internet
    /// (<c>LIBRARY_WRITES_ADMIN_ONLY=true</c>) the writes that <b>destroy or rename shared
    /// structure</b> belong to the admin — an attachment DELETE, a collection rename/delete,
    /// bug-report triage. Additive writes stay open (upload a source file, create a collection,
    /// report a bug): a shared library members can only read is not the product, and the LAN
    /// deployment keeps the shared-library posture by default (flag off).
    /// Returns <c>null</c> when the request may proceed, the 403 when it may not.
    /// </summary>
    public static async Task<IResult?> DenySharedDestroyAsync(
        HttpContext http, IDeploymentPolicy policy, string verb)
    {
        if (!policy.LibraryWritesAdminOnly || await IsAdminAsync(http))
            return null;

        return Results.Json(
            new { error = new { code = "forbidden", message = $"Only the admin account can {verb} on this server." } },
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// ST-61: the <c>?access_token=</c> fallback is SignalR's handshake convention (browser
    /// WebSockets cannot set headers) — accepted ONLY on hub paths. On any other route a
    /// query-string token leaks the 30-day credential into access logs, browser history and
    /// Referer headers; API clients must use the Authorization header.
    /// </summary>
    public static bool AllowsQueryStringToken(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        // PathString comparisons are case-insensitive by contract ("Hub" matches "/hubs").
        var p = new PathString(path);
        return p.StartsWithSegments("/hubs");
    }
}
