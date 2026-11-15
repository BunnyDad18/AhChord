using System.Net;
using System.Net.Http;
using System.Text;
using WebApi.Services;

namespace WebApi.Tests;

public class MusicBrainzClientTests
{
    /// <summary>Scriptable handler: records requests, returns canned responses or throws.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) { Requests.Add(request); }
            return await _responder(request);
        }
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static MusicBrainzClient NewClient(FakeHandler handler, TimeSpan? interval = null)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") },
            interval);

    // --- response mapping ------------------------------------------------------

    [Fact]
    public async Task Search_MapsRelease_FullFields()
    {
        var body = """
            {
              "created": "2026-01-01T00:00:00Z",
              "count": 1,
              "offset": 0,
              "releases": [
                {
                  "id": "mbid-1",
                  "score": 95,
                  "title": "Let It Be",
                  "date": "1970-05-08",
                  "artist-credit": [ { "name": "The Beatles" } ]
                }
              ]
            }
            """;
        var client = NewClient(new FakeHandler(_ => Task.FromResult(JsonResponse(body))));

        var results = await client.SearchAsync("let it be beatles", 10);

        var r = Assert.Single(results);
        Assert.Equal("mbid-1", r.Mbid);
        Assert.Equal("Let It Be", r.Title);
        Assert.Equal("The Beatles", r.Artist);
        Assert.Equal("Let It Be", r.Album);
        Assert.Equal(1970, r.Year);
        Assert.Equal(95, r.Score);
    }

    [Fact]
    public async Task Search_MapsRelease_WithoutArtistOrDate()
    {
        var body = """
            { "releases": [ { "id": "mbid-2", "score": 50, "title": "Live Jam" } ] }
            """;
        var client = NewClient(new FakeHandler(_ => Task.FromResult(JsonResponse(body))));

        var r = Assert.Single(await client.SearchAsync("live jam", 5));

        Assert.Equal("mbid-2", r.Mbid);
        Assert.Equal("Live Jam", r.Title);
        Assert.Null(r.Artist);
        Assert.Equal("Live Jam", r.Album); // album = release title
        Assert.Null(r.Year);
        Assert.Equal(50, r.Score);
    }

    [Fact]
    public async Task Search_MapsRelease_YearOnlyDate()
    {
        var body = """
            {
              "releases": [
                {
                  "id": "m3",
                  "score": 40,
                  "title": "Catch a Fire",
                  "date": "1973",
                  "artist-credit": [ { "name": "Bob Marley & The Wailers" } ]
                }
              ]
            }
            """;
        var client = NewClient(new FakeHandler(_ => Task.FromResult(JsonResponse(body))));

        var r = Assert.Single(await client.SearchAsync("catch a fire", 5));

        Assert.Equal("Bob Marley & The Wailers", r.Artist);
        Assert.Equal(1973, r.Year);
    }

    [Fact]
    public async Task Search_GarbageDate_YearIsNull()
    {
        var body = """
            { "releases": [ { "id": "m4", "title": "X", "date": "unknown" } ] }
            """;
        var client = NewClient(new FakeHandler(_ => Task.FromResult(JsonResponse(body))));

        var r = Assert.Single(await client.SearchAsync("x", 5));

        Assert.Null(r.Year);
    }

    [Fact]
    public async Task Search_MultipleReleases_PreservesOrder()
    {
        var body = """
            {
              "releases": [
                { "id": "a", "score": 90, "title": "First" },
                { "id": "b", "score": 80, "title": "Second" }
              ]
            }
            """;
        var client = NewClient(new FakeHandler(_ => Task.FromResult(JsonResponse(body))));

        var results = await client.SearchAsync("zzz", 5);

        var ids = results.Select(r => r.Mbid).ToList();
        Assert.Equal(new[] { "a", "b" }, ids);
    }

    [Fact]
    public async Task Search_EmptyReleases_ReturnsEmptyList()
    {
        var client = NewClient(new FakeHandler(_ => Task.FromResult(JsonResponse("""{ "releases": [] }"""))));
        Assert.Empty(await client.SearchAsync("zzz", 5));
    }

    [Fact]
    public async Task Search_NoReleasesField_ReturnsEmptyList()
    {
        var client = NewClient(new FakeHandler(_ => Task.FromResult(JsonResponse("{}"))));
        Assert.Empty(await client.SearchAsync("zzz", 5));
    }

    // --- request shape ---------------------------------------------------------

    [Fact]
    public async Task Search_SendsEscapedQueryLimitAndUserAgent()
    {
        var handler = new FakeHandler(_ => Task.FromResult(JsonResponse("""{ "releases": [] }""")));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AhChord/1.0 (self-hosted chord app)");
        var client = new MusicBrainzClient(http, TimeSpan.FromMilliseconds(1));

        await client.SearchAsync("hello world & friends", 5);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://musicbrainz.org/ws/2/release", req.RequestUri!.GetLeftPart(UriPartial.Path));
        var queryPart = req.RequestUri.Query.TrimStart('?');
        Assert.Equal("query=hello%20world%20%26%20friends", queryPart.Split('&', 2)[0]);
        Assert.Contains("limit=5", queryPart);
        Assert.Equal("AhChord/1.0 (self-hosted chord app)", req.Headers.UserAgent.ToString());
    }

    // --- rate limiter ----------------------------------------------------------

    [Fact]
    public async Task RateLimiter_SecondCall_WaitsForInterval()
    {
        var handler = new FakeHandler(_ => Task.FromResult(JsonResponse("""{ "releases": [] }""")));
        var interval = TimeSpan.FromMilliseconds(400);
        var client = NewClient(handler, interval);

        var start = DateTimeOffset.UtcNow;
        await client.SearchAsync("a", 1);
        await client.SearchAsync("b", 1);
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.Equal(2, handler.Requests.Count); // both went through — queued, not dropped
        // ST-65: 50ms tolerance — Task.Delay oversleeps, never undersleeps by much, but
        // wall-clock sampling on a loaded CI box made this flaky by a few ms.
        Assert.True(elapsed >= interval - TimeSpan.FromMilliseconds(50), $"expected >= {interval.TotalMilliseconds - 50}ms, was {elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task RateLimiter_ConcurrentCalls_AllComplete()
    {
        var handler = new FakeHandler(_ => Task.FromResult(JsonResponse("""{ "releases": [] }""")));
        var client = NewClient(handler, TimeSpan.FromMilliseconds(150));

        var results = await Task.WhenAll(
            client.SearchAsync("a", 1),
            client.SearchAsync("b", 1),
            client.SearchAsync("c", 1));

        Assert.All(results, r => Assert.Empty(r));
        Assert.Equal(3, handler.Requests.Count);
    }

    // --- failure mapping -------------------------------------------------------

    [Fact]
    public async Task Search_429_MapsTo503RateLimited()
    {
        var handler = new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") }));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<MusicBrainzException>(() => client.SearchAsync("x", 1));
        Assert.Equal(503, ex.HttpStatus);
        Assert.Equal("rate-limited", ex.Code);
    }

    [Fact]
    public async Task Search_503_MapsTo503RateLimited()
    {
        var handler = new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("throttled") }));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<MusicBrainzException>(() => client.SearchAsync("x", 1));
        Assert.Equal(503, ex.HttpStatus);
        Assert.Equal("rate-limited", ex.Code);
    }

    [Fact]
    public async Task Search_Timeout_MapsTo502UpstreamError()
    {
        var handler = new FakeHandler(_ => throw new TaskCanceledException("request took too long"));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<MusicBrainzException>(() => client.SearchAsync("x", 1));
        Assert.Equal(502, ex.HttpStatus);
        Assert.Equal("upstream-error", ex.Code);
    }

    [Fact]
    public async Task Search_NetworkFailure_MapsTo502UpstreamError()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("DNS lookup failed"));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<MusicBrainzException>(() => client.SearchAsync("x", 1));
        Assert.Equal(502, ex.HttpStatus);
        Assert.Equal("upstream-error", ex.Code);
    }

    [Fact]
    public async Task Search_500_MapsTo502UpstreamError()
    {
        var handler = new FakeHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") }));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<MusicBrainzException>(() => client.SearchAsync("x", 1));
        Assert.Equal(502, ex.HttpStatus);
        Assert.Equal("upstream-error", ex.Code);
    }

    // --- ST-65: year range aligned with the app canonical 1000–2100 -------------

    [Fact]
    public async Task Search_YearOutsideAppCanonicalRange_IsNotProposed()
    {
        // A year the app can never store/re-propose (ImportService + MetadataReviewJob
        // accept 1000–2100) must not be proposed at all — 2500 used to slip through.
        var body = """
            { "releases": [
              { "id": "r1", "title": "Future", "date": "2500-01-01" },
              { "id": "r2", "title": "Classic", "date": "1975-06-01" }
            ] }
            """;
        var handler = new FakeHandler(_ => Task.FromResult(JsonResponse(body)));
        var client = NewClient(handler);

        var results = await client.SearchAsync("x", 2);

        var future = results.Single(r => r.Title == "Future");
        Assert.Null(future.Year);
        var classic = results.Single(r => r.Title == "Classic");
        Assert.Equal(1975, classic.Year);
    }
}
