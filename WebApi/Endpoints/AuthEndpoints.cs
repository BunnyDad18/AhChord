using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Identity;
using WebApi.Services;

namespace WebApi.Endpoints;

// ST-35 A1: username-or-email auth. Username is a dedicated unique login name (Identity's
// UserName column, already uniquely indexed via NormalizedUserName); email stays as the
// unique contact/recovery address. Login accepts either in whichever field the client sends.
public record RegisterRequest(string? Username, string? Email, string? Password, string? DisplayName);

public record LoginRequest(string? Username, string? Email, string? Password);

public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public record UpdateMeRequest(string? DisplayName);

public record AuthUserDto(
    string Id,
    string Email,
    string? Username,
    string? DisplayName,
    bool IsAdmin = false,
    long? AvatarVersion = null);

public record AuthResponse(string Token, int ExpiresIn, AuthUserDto User);

/// <summary>
/// /api/auth endpoints. The /api group is RequireAuthorization by default (see Program.cs);
/// register and login opt out explicitly. Errors use the { error: { code, message, fields? } } shape.
/// Logout is client-side (discard the JWT) — no endpoint. Issued tokens ARE revoked server-side:
/// they carry the Identity security stamp, and a password change rotates it (gate 4, SECURITY.md).
/// Registration is a deployment switch (REGISTRATION_OPEN) — closed on an internet-facing demo,
/// where the accounts are the seeded admin (SEED_ADMIN_USERNAME) plus existing users.
/// </summary>
public static class AuthEndpoints
{
    private const int MinPasswordLength = 8;

    // ST-70c: avatar image limits (1 MB content + multipart framing headroom).
    private const long MaxAvatarBytes = 1024 * 1024;
    private const long MaxAvatarRequestBytes = 2 * 1024 * 1024;

    /// <summary>ST-70c: extension → canonical content type, the source of truth (source-files pattern).</summary>
    private static readonly Dictionary<string, string> AvatarTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
    };

    /// <summary>ST-70c: testable validation helper.</summary>
    internal static bool TryAvatarType(string extension, out string contentType)
        => AvatarTypes.TryGetValue(extension, out contentType!);

    internal const long MaxAvatarSizeBytes = MaxAvatarBytes;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("auth");
        // Public-demo gate 2: login/register are the enumeration + stuffing surface (and /me,
        // /change-password ride the same generous budget). Partitioned per user when a token is
        // present, per client address for the anonymous calls (Program.cs).
        auth.RequireRateLimiting("auth");

        auth.MapPost("/register", async (
            RegisterRequest request,
            UserManager<ApplicationUser> userManager,
            JwtTokenService tokenService,
            IDeploymentPolicy deployment) =>
        {
            // Public-demo gate 1: registration is the product on a band's own server (anyone the
            // band invites registers), and the open door on the internet. Closed, the accounts
            // are the seeded admin (SEED_ADMIN_USERNAME) plus whoever is already there.
            if (!deployment.RegistrationOpen)
                return Error(StatusCodes.Status403Forbidden, "registration-closed",
                    "Registration is closed on this server. Ask the server admin for an account.");

            var fields = new Dictionary<string, string?>();
            if (string.IsNullOrWhiteSpace(request.Username) || !IsValidUsername(request.Username))
                fields["username"] = "Username must be 3-32 characters: letters, numbers, dot, dash or underscore.";
            if (string.IsNullOrWhiteSpace(request.Email) || !MailAddress.TryCreate(request.Email, out _))
                fields["email"] = "A valid email address is required.";
            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < MinPasswordLength)
                fields["password"] = $"Password must be at least {MinPasswordLength} characters.";
            if (fields.Count > 0)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.", fields);

            if (await userManager.FindByNameAsync(request.Username!) is not null)
                return Error(StatusCodes.Status409Conflict, "username-taken", "That username is already taken.",
                    new Dictionary<string, string?> { ["username"] = "Username is already registered." });
            if (await userManager.FindByEmailAsync(request.Email!) is not null)
                return Error(StatusCodes.Status409Conflict, "email-taken", "An account with this email already exists.",
                    new Dictionary<string, string?> { ["email"] = "Email is already registered." });

            var user = new ApplicationUser
            {
                UserName = request.Username!, // ST-35 A1: dedicated unique username (was: the email)
                Email = request.Email,
                EmailConfirmed = true, // no email flow by design
                DisplayName = CleanDisplayName(request.DisplayName),
                // ST-61: registration never self-grants admin. The old `!Users.AnyAsync()`
                // check raced two concurrent first registrations into co-admins (TOCTOU on a
                // privilege decision); DbSeeder.SeedFirstUserIsAdmin owns promotion and runs
                // idempotently on every startup — the first account becomes admin at the
                // next start.
                IsAdmin = false
            };

            var created = await userManager.CreateAsync(user, request.Password!);
            if (!created.Succeeded)
            {
                // ST-63: the concurrent-registration race surfaces Identity's duplicate guards —
                // map them to the same 409s the pre-checks answer (raw Identity messages used to
                // leak through as a 400 "validation-failed").
                if (created.Errors.Any(e => e.Code == "DuplicateUserName" || e.Code == "DuplicateRoleNames"))
                    return Error(StatusCodes.Status409Conflict, "username-taken", "That username is already registered.",
                        new Dictionary<string, string?> { ["username"] = "Username is already registered." });
                if (created.Errors.Any(e => e.Code == "DuplicateEmail"))
                    return Error(StatusCodes.Status409Conflict, "email-taken", "An account with this email already exists.",
                        new Dictionary<string, string?> { ["email"] = "Email is already registered." });
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Registration failed.", ToFields(created, "password"));
            }

            var (token, expiresAt) = tokenService.CreateToken(user);
            return Results.Json(ToResponse(token, expiresAt, user), statusCode: StatusCodes.Status201Created);
        }).AllowAnonymous();

        auth.MapPost("/login", async (
            LoginRequest request,
            UserManager<ApplicationUser> userManager,
            JwtTokenService tokenService) =>
        {
            // ST-35 A1: accept a username OR an email — the client sends whichever it has in
            // the matching field. Username is tried first (it's unique), then email.
            var hasIdentity = !string.IsNullOrWhiteSpace(request.Username) || !string.IsNullOrWhiteSpace(request.Email);
            if (!hasIdentity || string.IsNullOrWhiteSpace(request.Password))
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.", new Dictionary<string, string?>
                {
                    ["username"] = !hasIdentity ? "Username or email is required." : null,
                    ["password"] = string.IsNullOrWhiteSpace(request.Password) ? "Password is required." : null
                });

            ApplicationUser? user = null;
            if (!string.IsNullOrWhiteSpace(request.Username))
                user = await userManager.FindByNameAsync(request.Username);
            if (user is null && !string.IsNullOrWhiteSpace(request.Email))
                user = await userManager.FindByEmailAsync(request.Email);
            // CheckPasswordAsync (not the sign-in variant): no lockout accounting — predictable 401s.
            var valid = user is not null && await userManager.CheckPasswordAsync(user, request.Password);
            if (user is null || !valid)
                return Error(StatusCodes.Status401Unauthorized, "bad-credentials", "Invalid username/email or password.");

            var (token, expiresAt) = tokenService.CreateToken(user);
            return Results.Ok(ToResponse(token, expiresAt, user));
        }).AllowAnonymous();

        auth.MapGet("/me", async (HttpContext http, ApplicationDbContext db) =>
        {
            var userId = CurrentUserId(http);
            var user = userId is null ? null : await db.Users.FindAsync(userId);
            if (user is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Authentication required.");
            return Results.Ok(ToUserDto(user));
        });

        // ST-26: update the account display name.
        auth.MapPatch("/me", async (
            UpdateMeRequest request,
            HttpContext http,
            ApplicationDbContext db) =>
        {
            var userId = CurrentUserId(http);
            var user = userId is null ? null : await db.Users.FindAsync(userId);
            if (user is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Authentication required.");
            if (request is null)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.",
                    new Dictionary<string, string?> { ["displayName"] = "displayName is required." });

            var name = request.DisplayName?.Trim();
            if (string.IsNullOrEmpty(name))
                user.DisplayName = null;
            else
                user.DisplayName = CleanDisplayName(name);
            await db.SaveChangesAsync();
            return Results.Ok(ToUserDto(user));
        });

        auth.MapPost("/change-password", async (
            ChangePasswordRequest request,
            HttpContext http,
            UserManager<ApplicationUser> userManager,
            JwtTokenService tokenService) =>
        {
            var userId = CurrentUserId(http);
            var user = userId is null ? null : await userManager.FindByIdAsync(userId);
            if (user is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Authentication required.");

            var fields = new Dictionary<string, string?>();
            if (string.IsNullOrWhiteSpace(request.CurrentPassword))
                fields["currentPassword"] = "Current password is required.";
            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < MinPasswordLength)
                fields["newPassword"] = $"New password must be at least {MinPasswordLength} characters.";
            if (fields.Count > 0)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.", fields);
            if (request.NewPassword == request.CurrentPassword)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "New password must be different from the current password.",
                    new Dictionary<string, string?> { ["newPassword"] = "New password must be different from the current password." });

            var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword!, request.NewPassword!);
            if (!result.Succeeded)
                return Error(StatusCodes.Status400BadRequest, "password-change-failed", "Could not change password.", ToFields(result, "newPassword"));

            // Gate 4 (JWT_STAMP_CHECK) rotates the security stamp, which revokes every issued
            // token — including the one that is answering this request. Without a replacement
            // the form answers "Password updated." and the app bounces to the sign-in screen
            // (the API client clears the token on the next 401). Mint the new one here: this
            // device keeps working, the OTHER devices sign out, which is the control working.
            // Additive fields — a client that predates them keeps reading { message }.
            var (token, expiresAt) = tokenService.CreateToken(user);
            var expiresIn = (int)Math.Max(0, (expiresAt - DateTime.UtcNow).TotalSeconds);
            return Results.Ok(new { message = "Password updated.", token, expiresIn, user = ToUserDto(user) });
        });

        // --- ST-70c: profile image (avatar) -------------------------------------------------
        // Bytes on the user row (bytes-in-Postgres convention, like source files). Uploaded
        // multipart with Bearer auth; served by ANONYMOUS GET /api/users/{id}/avatar below —
        // an <img> tag can't send the Bearer header and query tokens are hub-only (ST-61).
        auth.MapPut("/me/avatar", async (HttpContext http, IFormFile? file, ApplicationDbContext db) =>
        {
            var userId = CurrentUserId(http);
            var user = userId is null ? null : await db.Users.FindAsync(userId);
            if (user is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Authentication required.");

            if (file is null || file.Length == 0)
                return Error(StatusCodes.Status400BadRequest, "no-file", "A multipart body with a \"file\" image field is required.");

            var ext = Path.GetExtension(file.FileName);
            if (!TryAvatarType(ext, out var contentType))
                return Error(StatusCodes.Status400BadRequest, "unsupported-type", "Only PNG, JPG and WebP images are supported.");

            if (file.Length > MaxAvatarBytes)
                return Error(StatusCodes.Status400BadRequest, "too-large",
                    $"Image is larger than the {MaxAvatarBytes / 1024 / 1024} MB limit.");

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            user.AvatarBytes = ms.ToArray();
            user.AvatarContentType = contentType;
            user.AvatarUpdatedAt = DateTime.UtcNow; // new URL version → browser cache invalidated
            await db.SaveChangesAsync();

            return Results.Ok(ToUserDto(user));
        })
            .AddEndpointFilter((ctx, next) =>
            {
                // ST-63 pattern: kill oversized bodies in transport (413), before spooling.
                var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                    feature.MaxRequestBodySize = MaxAvatarRequestBytes;
                return next(ctx);
            })
            .DisableAntiforgery();

        auth.MapDelete("/me/avatar", async (HttpContext http, ApplicationDbContext db) =>
        {
            var userId = CurrentUserId(http);
            var user = userId is null ? null : await db.Users.FindAsync(userId);
            if (user is null)
                return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Authentication required.");

            user.AvatarBytes = null;
            user.AvatarContentType = null;
            user.AvatarUpdatedAt = null;
            await db.SaveChangesAsync();
            return Results.Ok(ToUserDto(user));
        });

        // Anonymous (James's call on anon surfaces, like /app/download): the avatar is
        // display-name-level visibility on a LAN self-host — the app itself requires login.
        api.MapGet("/users/{id}/avatar", async (string id, ApplicationDbContext db) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
            if (user?.AvatarBytes is null || user.AvatarContentType is null)
                return Error(StatusCodes.Status404NotFound, "no-avatar", "This user has no profile image.");

            // ?v=<ticks> cache-bust from the client; immutable per version.
            return Results.File(user.AvatarBytes, user.AvatarContentType);
        }).AllowAnonymous();

        return api;
    }

    private static string? CurrentUserId(HttpContext http)
        => http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static AuthResponse ToResponse(string token, DateTime expiresAt, ApplicationUser user)
        => new(
            token,
            (int)Math.Max(0, (expiresAt - DateTime.UtcNow).TotalSeconds),
            ToUserDto(user));

    /// <summary>ST-35 A1: project a user onto the auth DTO, exposing the unique username.
    /// ST-70c: Id + AvatarVersion (ticks) let the client build the avatar URL.</summary>
    private static AuthUserDto ToUserDto(ApplicationUser user)
        => new(user.Id, user.Email ?? string.Empty, user.UserName, user.DisplayName, user.IsAdmin,
            user.AvatarUpdatedAt?.Ticks);

    /// <summary>ST-63 (finding 5): bounded DisplayName — trimmed, null when blank, hard cap 100
    /// (clipped, wishlist style). Unbounded user text lands in every UI surface and log line.</summary>
    internal const int MaxDisplayNameLength = 100;

    internal static string? CleanDisplayName(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length > MaxDisplayNameLength ? trimmed[..MaxDisplayNameLength] : trimmed;
    }

    /// <summary>ST-35 A1: username shape — 3-32 chars of letters, numbers, dot, dash or underscore.</summary>
    private static bool IsValidUsername(string? username)
        => !string.IsNullOrWhiteSpace(username)
            && username.Length is >= 3 and <= 32
            && username.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>Maps IdentityResult errors into the fields bag, preferring stable field names.</summary>
    private static Dictionary<string, string?> ToFields(IdentityResult result, string defaultField)
    {
        var fields = new Dictionary<string, string?>();
        foreach (var error in result.Errors)
        {
            var field = error.Code switch
            {
                "PasswordMismatch" => "currentPassword",
                "InvalidEmail" => "email",
                _ => defaultField
            };
            fields.TryAdd(field, error.Description); // first error per field wins
        }
        return fields;
    }

    private static IResult Error(int statusCode, string code, string message, Dictionary<string, string?>? fields = null)
        => Results.Json(new { error = new { code, message, fields } }, statusCode: statusCode, options: Json);
}
