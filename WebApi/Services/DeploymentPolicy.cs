using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;

namespace WebApi.Services;

/// <summary>
/// The deployment-shape knobs — one place for what differs between the normal self-hosted band
/// server and an instance that faces the internet (the public-demo gates in SECURITY.md).
///
/// Everything defaults to the LAN product: open registration, shared library writes, generous
/// rate limits, no per-request token validation. A public demo turns them down. Read once at
/// startup into a singleton, so a change is a redeploy, not a surprise mid-request policy flip.
/// </summary>
public interface IDeploymentPolicy
{
    /// <summary><c>REGISTRATION_OPEN</c> (default true) — false closes POST /api/auth/register.</summary>
    bool RegistrationOpen { get; }

    /// <summary><c>LIBRARY_WRITES_ADMIN_ONLY</c> (default false) — non-admins stop being able to
    /// destroy or rename shared structure (attachment DELETE, collection rename/delete,
    /// bug-report triage). Adding things stays open: that is the shared-library product.</summary>
    bool LibraryWritesAdminOnly { get; }

    /// <summary><c>JWT_STAMP_CHECK</c> (default <b>false</b>) — true validates the token's
    /// <c>sstamp</c> claim against the user row on every request, which is what makes a password
    /// change, a stamp rotation or a deleted account revoke an issued 30-day token. Off by
    /// default because it is the one gate that changes today's behaviour: turning it on ends
    /// every token issued before it (all devices sign in again) and a password change signs out
    /// every device. A public demo sets it true.</summary>
    bool JwtStampCheck { get; }

    int AuthPermitLimit { get; }
    int AuthWindowSeconds { get; }
    int ImportPermitLimit { get; }
    int ImportWindowSeconds { get; }
}

/// <inheritdoc cref="IDeploymentPolicy" />
public sealed class DeploymentPolicy : IDeploymentPolicy
{
    public DeploymentPolicy(IConfiguration config)
    {
        RegistrationOpen = config.GetValue("REGISTRATION_OPEN", true);
        LibraryWritesAdminOnly = config.GetValue("LIBRARY_WRITES_ADMIN_ONLY", false);
        JwtStampCheck = config.GetValue("JWT_STAMP_CHECK", false);
        AuthPermitLimit = Positive(config, "RATE_LIMIT_AUTH_PERMIT", 30);
        AuthWindowSeconds = Positive(config, "RATE_LIMIT_AUTH_WINDOW_SECONDS", 60);
        ImportPermitLimit = Positive(config, "RATE_LIMIT_IMPORT_PERMIT", 10);
        ImportWindowSeconds = Positive(config, "RATE_LIMIT_IMPORT_WINDOW_SECONDS", 60);
    }

    public bool RegistrationOpen { get; }
    public bool LibraryWritesAdminOnly { get; }
    public bool JwtStampCheck { get; }
    public int AuthPermitLimit { get; }
    public int AuthWindowSeconds { get; }
    public int ImportPermitLimit { get; }
    public int ImportWindowSeconds { get; }

    /// <summary>A zero/negative/garbage limit would disable the limiter silently — fall back.</summary>
    private static int Positive(IConfiguration config, string key, int fallback)
    {
        var value = config.GetValue(key, fallback);
        return value is > 0 and <= 1_000_000 ? value : fallback;
    }
}

/// <summary>
/// The rate limiter's partition key: the signed-in user when a token is present, the client
/// address for the anonymous auth endpoints. Address, not "the proxy's address" — with
/// TRUSTED_PROXY_IPS set, UseForwardedHeaders has already rewritten RemoteIpAddress to the
/// real client (Program.cs), which is what makes a behind-a-proxy deployment limit people
/// rather than the whole site together.
/// </summary>
public static class RateLimitKey
{
    public static string Partition(HttpContext http)
    {
        // Authz.Subject, not FindFirst("sub"): JwtBearer's inbound mapping renames "sub" to
        // ClaimTypes.NameIdentifier, so reading the raw name would put every signed-in user in
        // the anonymous address bucket.
        var sub = Authz.Subject(http.User);
        if (!string.IsNullOrEmpty(sub))
            return $"u:{sub}";

        var ip = http.Connection.RemoteIpAddress?.ToString();
        return string.IsNullOrEmpty(ip) ? "anon:unknown" : $"ip:{ip}";
    }
}

/// <summary>
/// TRUSTED_PROXY_IPS entries into ForwardedHeadersOptions (public-demo gate 2). Exact
/// addresses AND CIDR blocks: on a docker network the natural value is the subnet (Traefik's
/// address moves on every redeploy), so exact-IP-only matching silently trusts nothing and the
/// whole site collapses into one client-address bucket — a warning line nobody reads.
/// Unparsable entries are reported through <paramref name="onUnparsable"/> and skipped.
/// </summary>
public static class TrustedProxies
{
    public static void Apply(ForwardedHeadersOptions options, IEnumerable<string> entries, Action<string>? onUnparsable = null)
    {
        foreach (var raw in entries)
        {
            // Trim here too: Program.cs already trims the split, and IPAddress.TryParse is
            // lenient about surrounding space (" 192.168.1.1 " parses as ::1 — a proxy nobody
            // configured), so the value is normalised before it is tested.
            var entry = raw.Trim();
            var slash = entry.IndexOf('/');
            var hostPart = slash < 0 ? entry : entry[..slash];

            if (!IPAddress.TryParse(hostPart, out var addr))
            {
                onUnparsable?.Invoke(entry);
                continue;
            }

            if (slash < 0)
            {
                options.KnownProxies.Add(addr);
                continue;
            }

            // Prefix length must fit the address family: /40 on an IPv4 address is garbage.
            var maxBits = addr.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (!int.TryParse(entry[(slash + 1)..], out var bits) || bits < 0 || bits > maxBits)
            {
                onUnparsable?.Invoke(entry);
                continue;
            }
            // KnownIPNetworks + System.Net.IPNetwork — the ASP.NET Core pair (KnownNetworks,
            // HttpOverrides.IPNetwork) is obsolete in .NET 10.
            options.KnownIPNetworks.Add(new IPNetwork(addr, bits));
        }
    }
}
