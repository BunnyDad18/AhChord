using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebApi.Data;
using WebApi.Endpoints;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-65: single tests for the low-priority sweep items where observable —
/// facets total grouping, Songsterr id filter + 429 backoff, provider whole-row
/// fallback, LlmClient config default model.
/// </summary>
public class St65SweepTests
{
    // ---------- #2: facets total mirrors the list's case-insensitive grouping ----------

    [Fact]
    public void CountDistinctGroups_CaseAndSpacesMatchListGrouping()
    {
        // ST-106: grouping is by TITLE only (artist is a list now — title+artist would
        // fragment multi-artist songs), same trim + lowercase key as the grouped list.
        var titles = new[]
        {
            "Drive",
            "drive",      // same group (lowercased)
            " drive ",    // same group (trimmed)
            "Drive",      // same group (artist no longer splits versions)
            "Drive!",     // different title → its own group
        };
        Assert.Equal(2, SongEndpoints.CountDistinctTitleGroups(titles));
    }

    // ---------- #9: Songsterr drops entries without a usable id ----------

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) { Requests.Add(request); }
            return await responder(request);
        }
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Songsterr_EntriesWithoutId_AreDropped()
    {
        var body = """
            [
              { "songId": null, "title": "Wire change", "artist": "A" },
              { "songId": 0,   "title": "Fake zero", "artist": "A" },
              { "songId": 5,   "title": "Real", "artist": "A" }
            ]
            """;
        var client = new SongsterrClient(
            new HttpClient(new FakeHandler(_ => Task.FromResult(JsonResponse(body))))
            {
                BaseAddress = new Uri("https://www.songsterr.com/")
            },
            TimeSpan.FromMilliseconds(1));

        var results = await client.SearchAsync("x", 10);

        var r = Assert.Single(results);
        Assert.Equal(5, r.SongId);
    }

    // ---------- #10: 429 Retry-After widens the queue spacing ----------

    [Fact]
    public async Task Songsterr_RateLimited_BumpsMinInterval()
    {
        var handler = new FakeHandler(_ =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(res);
        });
        var client = new SongsterrClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.songsterr.com/") },
            TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<SongsterrException>(() => client.SearchAsync("x", 5));
        Assert.Equal(TimeSpan.FromSeconds(7), client.CurrentMinInterval);
    }

    [Fact]
    public async Task MusicBrainz_RateLimited_BumpsMinInterval()
    {
        var handler = new MusicBrainzProbe();
        var client = new MusicBrainzClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") },
            TimeSpan.FromSeconds(1.5));

        await Assert.ThrowsAsync<MusicBrainzException>(() => client.SearchAsync("x", 5));
        Assert.Equal(TimeSpan.FromSeconds(11), client.CurrentMinInterval); // 1.5s default < 11s bump
    }

    /// <summary>429 + Retry-After: 11 for the MusicBrainz probe above.</summary>
    private sealed class MusicBrainzProbe : System.Net.Http.HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(11));
            return Task.FromResult(res);
        }
    }

    // ---------- #13: band-job fallback uses the whole row, never a field merge ----------

    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static IConfiguration Env() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    [Fact]
    public async Task ResolveForJobAsync_AnyRowFallback_KeepsThatRowWhole()
    {
        var db = NewDb();
        // Triggerer: no endpoint, but a model. Member "u2": a complete config.
        db.LlmSettings.Add(new LlmSettings { UserId = "u1", BaseUrl = null, Model = "triggerer-model", ApiKey = "k1", MaxTokens = 1000 });
        db.LlmSettings.Add(new LlmSettings { UserId = "u2", BaseUrl = "http://member.test", Model = "member-model", ApiKey = "k2", MaxTokens = 2000 });
        db.SaveChanges();

        var provider = new LlmSettingsProvider(db, Env());
        var effective = await provider.ResolveForJobAsync("u1");

        // ST-65: endpoint + key + model travel TOGETHER — the triggerer's model must not
        // be merged onto the member's endpoint/key.
        Assert.Equal("http://member.test/v1", effective.BaseUrl);
        Assert.Equal("member-model", effective.Model);
        Assert.Equal("k2", effective.ApiKey);
        Assert.Equal(2000, effective.MaxTokens);
    }

    // ---------- #11: the default model is config-provided, and failures name it ----------

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task LlmClient_DefaultModelComesFromCtor()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{ "choices": [ { "message": { "content": "ok" } } ] }""")));
        var client = new LlmClient(new HttpClient(handler), defaultModel: "config-model");

        await client.ConvertAsync(new LlmEffectiveSettings("http://llm.test/v1", null, null, 4096, "user"), "sys", "user");

        var body = await handler.Requests[0].Content!.ReadAsStringAsync();
        Assert.Contains("\"model\":\"config-model\"", body);
    }

    [Fact]
    public async Task LlmClient_FailureNamesTheModel()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, """{ "error": { "message": "nope" } }""")));
        var client = new LlmClient(new HttpClient(handler), defaultModel: "config-model");

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            client.ConvertAsync(new LlmEffectiveSettings("http://llm.test/v1", null, null, 4096, "user"), "sys", "user"));

        Assert.Contains("config-model", ex.Message);
    }
}
