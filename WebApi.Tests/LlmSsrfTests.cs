using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WebApi.Dtos;

namespace WebApi.Tests;

/// <summary>
/// The LLM SSRF surface as the network sees it: POST /api/settings/llm/test and the stored
/// per-user BaseUrl fetch a member-supplied URL server-side (the only member-URL surface —
/// conversion input is pasted),
/// so on a closed policy (LLM_ALLOW_PRIVATE_NETWORK=false, the public-demo setting) the probe and
/// the saved row refuse private/internal targets, and no failure reply names the host, the port
/// or echoes upstream text. With the flag left at its default the local model keeps working —
/// that is the product, so both halves are tested.
/// </summary>
public class LlmSsrfTests
{
    private static ApiTestFactory ClosedPolicyFactory()
        => new ApiTestFactory().WithConfig("LLM_ALLOW_PRIVATE_NETWORK", "false");

    private static readonly string[] PrivateTargets =
    [
        "http://169.254.169.254/latest/meta-data",   // cloud metadata
        "http://127.0.0.1:1234/v1",                  // LM Studio on this box
        "http://192.168.1.5:3000/v1",               // the LAN
        "http://host.docker.internal:1234/v1",       // the container's route to the host
        "http://10.20.30.40:11434/v1",               // a VPN network
    ];

    // ---------- probe: refused by the policy, with nothing leaked ----------

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Probe_private_target_is_refused_and_names_nothing(string target)
    {
        using var factory = ClosedPolicyFactory();
        var user = await factory.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/settings/llm/test",
            Json(new LlmTestRequest(target, null, null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("endpoint-refused", body);
        // No host, no address, no port, no path from the target — and no upstream detail.
        var uri = Absolute(target);
        Assert.DoesNotContain(uri.Host, body);
        Assert.DoesNotContain(uri.AbsolutePath, body);
    }

    public static TheoryData<string> Targets => new([.. PrivateTargets]);

    /// <summary>The target parsed for "did any of it leak" assertions (Uri's ctor is obsolete in .NET 10).</summary>
    private static Uri Absolute(string url)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
        return uri!;
    }

    [Theory]
    [InlineData("http://localhost:5000/api/health")]
    [InlineData("http://gitea.internal/v1")]
    [InlineData("http://router/v1")]
    [InlineData("https://user:pass@api.openai.com/v1")]
    public async Task Probe_internal_names_and_userinfo_are_refused_under_a_closed_policy(string target)
    {
        using var factory = ClosedPolicyFactory();
        var user = await factory.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/settings/llm/test",
            Json(new LlmTestRequest(target, null, null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("endpoint-refused", body);
        Assert.DoesNotContain(Absolute(target).Host, body);
    }

    [Fact]
    public async Task Probe_public_target_is_not_refused_by_the_policy_and_leaks_no_detail()
    {
        // .invalid never resolves (RFC 2606) — the probe fails at the socket, deterministically,
        // with no egress. What matters: the policy let it through, and the reply is generic.
        using var factory = ClosedPolicyFactory();
        var user = await factory.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/settings/llm/test",
            Json(new LlmTestRequest("https://ahchord-ssrf-probe.invalid/v1", null, null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Could not reach", doc.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("ahchord-ssrf-probe.invalid", body);
    }

    [Fact]
    public async Task Probe_default_policy_still_probes_a_local_model()
    {
        // The flag unset = the self-hosted default: a loopback endpoint is not refused. Nothing
        // is listening on :1, so the probe lands on the generic reach failure, not the policy.
        using var factory = new ApiTestFactory();
        var user = await factory.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/settings/llm/test",
            Json(new LlmTestRequest("http://127.0.0.1:1/v1", null, null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("endpoint-refused", body);
        Assert.Contains("Could not reach", body);
        Assert.DoesNotContain("127.0.0.1", body);      // the address the caller typed is not echoed
    }

    // ---------- saved rows: the same policy at the write boundary ----------

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task PutMine_private_baseUrl_is_rejected_under_a_closed_policy(string target)
    {
        using var factory = ClosedPolicyFactory();
        var user = await factory.RegisterUserAsync();

        var response = await user.Client.PutAsync("/api/settings/llm/mine",
            Json(new LlmSettingsUpdateRequest(target, "model", null, null)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation-failed", body);
        Assert.Contains("baseUrl", body);
        Assert.DoesNotContain(Absolute(target).Host, body);
    }

    [Fact]
    public async Task PutMine_public_baseUrl_is_accepted_under_a_closed_policy()
    {
        using var factory = ClosedPolicyFactory();
        var user = await factory.RegisterUserAsync();

        var response = await user.Client.PutAsync("/api/settings/llm/mine",
            Json(new LlmSettingsUpdateRequest("https://api.openai.com/v1", "gpt", "key", null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PutMine_local_baseUrl_is_accepted_by_default()
    {
        using var factory = new ApiTestFactory();
        var user = await factory.RegisterUserAsync();

        var response = await user.Client.PutAsync("/api/settings/llm/mine",
            Json(new LlmSettingsUpdateRequest("http://127.0.0.1:1234/v1", "qwen", null, null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- the redirect door stays shut ----------

    [Fact]
    public async Task Probe_does_not_follow_a_redirect_into_the_private_network()
    {
        // The classic SSRF bypass: a public (here: loopback) endpoint answers 302 → metadata.
        // The named clients have AllowAutoRedirect=false, so the probe reports the 3xx and the
        // redirect target is never dialed.
        using var relay = new RedirectRelay();
        using var factory = new ApiTestFactory();   // policy open, so the probe reaches the relay
        var user = await factory.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/settings/llm/test",
            Json(new LlmTestRequest(relay.Url, null, null)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("302", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains("redirects are not followed", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, relay.Hits);   // only the /v1/models probe — the redirect target was not dialed
    }

    /// <summary>Loopback listener that 302s the probe at cloud metadata and counts the dials.</summary>
    private sealed class RedirectRelay : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _hits;

        public string Url { get; }
        public int Hits => _hits;

        public RedirectRelay()
        {
            Url = $"http://127.0.0.1:{FreePort()}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await _listener.GetContextAsync();
                    }
                    catch
                    {
                        return;
                    }

                    Interlocked.Increment(ref _hits);
                    if (ctx.Request.Url!.AbsolutePath.EndsWith("/models"))
                    {
                        ctx.Response.StatusCode = (int)HttpStatusCode.Redirect;
                        ctx.Response.RedirectLocation = "http://169.254.169.254/latest/meta-data";
                    }
                    ctx.Response.Close();
                }
            });
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose() => _listener.Close();
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
