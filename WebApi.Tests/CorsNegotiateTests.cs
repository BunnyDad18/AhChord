namespace WebApi.Tests;

/// <summary>
/// ST-87: the CORS policy in the shape the Capacitor WebView needs — the SignalR
/// JS client POSTs /hubs/session/negotiate with credentials:include from origin
/// http://localhost, and browsers reject a credentialed response with the
/// ACAO wildcard. The policy must ECHO the origin and allow credentials.
/// Runs in the shared ST-82 collection app.
/// </summary>
[Collection("ST-82 endpoint app")]
public class CorsNegotiateTests
{
    private readonly ApiTestFactory _app;

    public CorsNegotiateTests(ApiTestFactory app) => _app = app;

    [Fact]
    public async Task Preflight_EchoesOrigin_AndAllowsCredentials()
    {
        var client = _app.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/hubs/session/negotiate");
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-requested-with");

        var response = await client.SendAsync(request);

        Assert.True((int)response.StatusCode is 200 or 204);
        Assert.Equal("http://localhost",
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? string.Join(",", v) : null);
        Assert.Equal("true",
            response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var c) ? string.Join(",", c) : null);
    }

    [Fact]
    public async Task NegotiatePost_EchoesOrigin_AndAllowsCredentials()
    {
        var user = await _app.RegisterUserAsync();
        var client = _app.AuthedClient(user.Token);
        var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/session/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", "https://capacitor.localhost");
        request.Content = new StringContent("");

        var response = await client.SendAsync(request);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("https://capacitor.localhost",
            response.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? string.Join(",", v) : null);
        Assert.Equal("true",
            response.Headers.TryGetValues("Access-Control-Allow-Credentials", out var c) ? string.Join(",", c) : null);
    }
}
