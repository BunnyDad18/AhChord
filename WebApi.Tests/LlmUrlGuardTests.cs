using System.Net;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// SSRF guard for the LLM surface (POST /api/settings/llm/test + the stored per-user BaseUrl).
/// Conversion input is pasted text, so the LLM endpoint is the only member-supplied URL the
/// server fetches. The guard cannot use a domain allowlist — a member may point AhChord at
/// their own OpenAI-compatible server — so the rule is the ADDRESS: private/internal targets are
/// refused only when the deployment closes them (LLM_ALLOW_PRIVATE_NETWORK=false), because the
/// normal self-hosted LLM is local.
/// </summary>
public class LlmUrlGuardTests
{
    [Theory]
    // loopback, by address and by name
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://127.1.2.3:1234/v1")]                  // whole 127/8
    [InlineData("https://localhost:5000/api/health")]
    [InlineData("http://[::1]:1234/v1")]
    // private ranges
    [InlineData("http://10.1.2.3:8080/v1")]
    [InlineData("http://172.16.0.9:8080/v1")]
    [InlineData("http://172.31.255.255/v1")]
    [InlineData("http://192.168.1.5:3000/v1")]
    [InlineData("http://100.64.0.7:11434/v1")]                // CGNAT / Tailscale
    // cloud metadata + link-local
    [InlineData("http://169.254.169.254/latest/meta-data")]
    // "this host" and reserved
    [InlineData("http://0.0.0.0:1234/v1")]
    [InlineData("http://198.18.0.1/v1")]                      // benchmarking
    [InlineData("http://224.0.0.1/v1")]                       // multicast
    [InlineData("http://[fd00::1234]/v1")]                    // IPv6 unique local
    [InlineData("http://[fe80::1]/v1")]                       // IPv6 link-local
    // internal names: the docker/avahi/internal zones and single-label hosts
    [InlineData("http://host.docker.internal:1234/v1")]
    [InlineData("http://lmstudio.local:1234/v1")]
    [InlineData("http://ollama.internal:11434/v1")]
    [InlineData("http://llm.test:1234/v1")]
    [InlineData("http://nas.lan:8080/v1")]
    [InlineData("http://router/v1")]
    [InlineData("http://gitea/v1")]
    // not an address at all, and credential smuggling
    [InlineData("https://user:pass@api.example.com/v1")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://10.0.0.1:11434")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public void Closed_policy_refuses_private_internal_and_malformed(string? url)
        => Assert.False(LlmUrlGuard.IsAllowedEndpoint(url, allowPrivate: false));

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("http://203.0.113.10:11434/v1")]              // public literal IPv4
    [InlineData("https://[2001:db8::1]:443/v1")]              // public literal IPv6
    [InlineData("https://llm.example.com:8443/v1")]
    public void Closed_policy_allows_public_endpoints(string url)
        => Assert.True(LlmUrlGuard.IsAllowedEndpoint(url, allowPrivate: false));

    [Theory]
    // the product's normal shape: a local model on this box or the LAN — allowed by default
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://localhost:11434/api")]
    [InlineData("http://host.docker.internal:1234/v1")]
    [InlineData("http://192.168.1.99:8080/v1")]
    [InlineData("http://strata.local:11434/v1")]
    public void Open_policy_keeps_local_endpoints_working(string url)
        => Assert.True(LlmUrlGuard.IsAllowedEndpoint(url, allowPrivate: true));

    [Theory]
    [InlineData("https://user:pass@api.example.com/v1")]      // userinfo is refused under BOTH
    [InlineData("ftp://api.example.com/v1")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void Malformed_is_refused_whatever_the_policy(string? url)
        => Assert.False(LlmUrlGuard.IsAllowedEndpoint(url, allowPrivate: true));

    [Fact]
    public void Closed_policy_refuses_a_public_name_with_the_v1_suffix_appended()
    {
        // NormalizeBaseUrl appends /v1 — the host verdict must survive it.
        Assert.False(LlmUrlGuard.IsAllowedEndpoint(
            LlmSettingsProvider.NormalizeBaseUrl("http://192.168.1.50:11434"), allowPrivate: false));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.100.100.100", true)]
    [InlineData("172.15.0.1", false)]                          // just outside 172.16/12
    [InlineData("172.32.0.1", false)]
    [InlineData("100.128.0.1", false)]                         // just outside the CGNAT block
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.7", false)]
    public void Address_ranges_are_bounded_exactly(string address, bool private_)
        => Assert.Equal(private_, LlmUrlGuard.IsPrivateAddress(IPAddress.Parse(address)));

    [Fact]
    public void IPv4_mapped_IPv6_loopback_is_loopback()
        => Assert.True(LlmUrlGuard.IsPrivateAddress(IPAddress.Parse("::ffff:127.0.0.1")));
}
