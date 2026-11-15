using System.Net;
using System.Net.Sockets;

namespace WebApi.Services;

/// <summary>
/// The deployment's LLM endpoint policy, from <c>LLM_ALLOW_PRIVATE_NETWORK</c> (default
/// <c>true</c>). AhChord's normal LLM is a local model — LM Studio / Ollama on this box or the
/// LAN — so private endpoints are the product, and the default keeps them working.
/// Set it to <c>false</c> on an instance that faces the internet: no member can then point the
/// server at an internal address, and <c>LLM_BASE_URL</c> must be public too.
/// </summary>
public interface ILlmEndpointPolicy
{
    bool AllowPrivateNetwork { get; }
}

/// <inheritdoc cref="ILlmEndpointPolicy" />
public sealed class LlmEndpointPolicy(bool allowPrivate) : ILlmEndpointPolicy
{
    public bool AllowPrivateNetwork { get; } = allowPrivate;
}

/// <summary>
/// SSRF guard for the LLM surface (<c>POST /api/settings/llm/test</c>, the stored per-user
/// BaseUrl used by conversions). The conversion input is pasted text — the server fetches no
/// member URLs anywhere else — so this is the only member-supplied fetch surface.
/// It cannot use an allowlist — the whole point is that a member may point AhChord at
/// their own OpenAI-compatible server — so it validates the address instead:
///
///  • absolute http(s) URL, no userinfo, non-empty host;
///  • when the policy is closed, the host must not be a private/reserved range
///    (loopback, RFC1918, CGNAT, link-local incl. cloud metadata, benchmark, multicast,
///    IPv6 ::1/fc00::/7/fe80::/10) or an internal name (<c>localhost</c>, <c>.local</c>,
///    <c>.test</c>, <c>.internal</c>, <c>.lan</c>, <c>.home</c>, single-label hosts);
///  • with the policy closed, the socket layer re-checks the RESOLVED address (Program.cs
///    <c>ConnectCallback</c>), so a public name that answers with a LAN address, and DNS
///    rebinding, are refused too;
///  • redirects are off on both LLM clients, so a public endpoint cannot bounce the fetch
///    into the private network;
///  • failures never echo the target, the port, <see cref="Exception.Message"/> or the
///    upstream body into a user-visible artifact — the detail goes to the log.
///
/// .NET's address parser is strict dotted-quad/IPv6, so the decimal/octal/hex encodings of
/// 127.0.0.1 do not parse as an address and are refused as an unknown single-label name.
/// </summary>
public static class LlmUrlGuard
{
    /// <summary>True for loopback, private, link-local, reserved and multicast addresses.</summary>
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6
            && address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        switch (bytes.Length)
        {
            case 4:
                return bytes[0] switch
                {
                    0 => true,                                    // 0.0.0.0/8 "this host"
                    10 => true,                                   // RFC1918
                    127 => true,                                  // loopback
                    169 when bytes[1] == 254 => true,              // link-local (cloud metadata)
                    172 when bytes[1] >= 16 && bytes[1] <= 31 => true, // RFC1918
                    192 when bytes[1] == 168 => true,              // RFC1918
                    192 when bytes[1] == 0 && bytes[2] == 0 => true,   // IETF protocol assignments
                    198 when bytes[1] is 18 or 19 => true,         // benchmarking
                    100 when bytes[1] >= 64 && bytes[1] <= 127 => true, // CGNAT / Tailscale
                    >= 224 => true,                               // multicast + reserved
                    _ => false
                };
            case 16:
                if (address.IsIPv6SiteLocal) return true;         // fec0::/10
                if (bytes[0] == 0xfc || bytes[0] == 0xfd) return true; // unique local fc00::/7
                if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true; // fe80::/10
                return bytes[0] >= 0xff;                          // IPv6 multicast (ff00::/8)
            default:
                return true;                                      // unknown family — refuse
        }
    }

    /// <summary>True when the host is a private/reserved literal address or an internal name.</summary>
    public static bool IsPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();

        // Bracketed IPv6 literal ([::1]) arrives with the brackets from Uri.Host.
        if (name.StartsWith('[') && name.EndsWith(']'))
            name = name[1..^1];

        if (IPAddress.TryParse(name, out var address))
            return IsPrivateAddress(address);

        // Names that only ever mean "inside": the docker/avahi/mDNS/internal zones, and
        // single-label hosts (db, gitea, router) which resolve from /etc/hosts or search domains.
        if (name == "localhost")
            return true;
        if (!name.Contains('.'))
            return true;
        return name.EndsWith(".local", StringComparison.Ordinal)
            || name.EndsWith(".localhost", StringComparison.Ordinal)
            || name.EndsWith(".internal", StringComparison.Ordinal)
            || name.EndsWith(".test", StringComparison.Ordinal)
            || name.EndsWith(".lan", StringComparison.Ordinal)
            || name.EndsWith(".home", StringComparison.Ordinal)
            || name.EndsWith(".localdomain", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the endpoint may be fetched under <paramref name="allowPrivate"/>. A URL with
    /// userinfo (<c>https://x@evil/</c>) is refused outright — it smuggles credentials and
    /// confuses host parsing.
    /// </summary>
    public static bool IsAllowedEndpoint(string? url, bool allowPrivate)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;
        return allowPrivate || !IsPrivateHost(uri.Host);
    }

    public static bool IsAllowedEndpoint(string? url, ILlmEndpointPolicy policy)
        => IsAllowedEndpoint(url, policy.AllowPrivateNetwork);
}
