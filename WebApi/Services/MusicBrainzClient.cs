using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebApi.Services;

/// <summary>One MusicBrainz search result (release/album level — see class docs for why).</summary>
public record MusicBrainzResult(
    string Mbid,
    string Title,
    string? Artist,
    string? Album,
    int? Year,
    int? Score);

/// <summary>
/// Carries the HTTP status + stable error code the endpoint should return, so callers can
/// translate failures into the standard { error: { code, message } } shape.
/// </summary>
public class MusicBrainzException(int httpStatus, string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public int HttpStatus { get; } = httpStatus;
    public string Code { get; } = code;
}

/// <summary>
/// Thin client for MusicBrainz track/album metadata search (ST-12). Metadata only — MusicBrainz
/// has no chords.
///
/// NOTE (deviation from the original ST-12 spec): MusicBrainz has **no /track search endpoint**
/// (GET /ws/2/track?query= → 404). The closest search entity that carries performer + album +
/// date is the **release** search, so SearchAsync queries /ws/2/release and maps each release to
/// { mbid, title, artist, album, year, score }. For most songs the release title equals the song
/// title; the editor (ST-22) lets the user adjust the title field.
///
/// Client-side rate limit: max 1 request per 1.5s (queue, never drop) — MusicBrainz also
/// throttles server-side (429/503), which surfaces as 503 "rate-limited".
/// </summary>
public class MusicBrainzClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>MusicBrainz asks for at least 1.5s between requests from a single client.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(1.5);

    private readonly HttpClient _http;
    // ST-65: mutable — a 429's Retry-After bumps the spacing for all queued callers.
    private TimeSpan _minInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public MusicBrainzClient(HttpClient http, TimeSpan? minInterval = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _minInterval = minInterval ?? DefaultMinInterval;
    }

    /// <summary>ST-65: the live queue spacing (test-observable after a 429 bump).</summary>
    internal TimeSpan CurrentMinInterval => _minInterval;

    /// <summary>ST-65: widen the queue spacing after a 429 (Retry-After, 30 s default, 2 min cap).</summary>
    private void BumpMinInterval(TimeSpan? retryAfter)
    {
        var bump = retryAfter ?? TimeSpan.FromSeconds(30);
        if (bump > TimeSpan.FromMinutes(2)) bump = TimeSpan.FromMinutes(2);
        if (bump > _minInterval) _minInterval = bump;
    }

    /// <summary>
    /// Searches releases for <paramref name="query"/> (max <paramref name="limit"/> results).
    /// Waits its turn in the rate-limit queue; never drops a request.
    /// </summary>
    public async Task<IReadOnlyList<MusicBrainzResult>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        // Relative (no leading slash) so it resolves against the base address https://musicbrainz.org/ws/2/
        var url = $"release?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}";

        await AcquireSlotAsync(ct);
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MusicBrainzException(502, "upstream-error", "The MusicBrainz request timed out.", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException)
        {
            throw new MusicBrainzException(502, "upstream-error", "MusicBrainz is unreachable.", ex);
        }

        try
        {
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                // ST-65: honor Retry-After — the queue's spacing is bumped for ALL callers,
                // not just this request (was: header discarded, hammering continued).
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    BumpMinInterval(response.Headers.RetryAfter?.Delta);
                throw new MusicBrainzException(503, "rate-limited", "MusicBrainz is rate-limiting us; try again in a moment.");
            }
            if (!response.IsSuccessStatusCode)
                throw new MusicBrainzException(502, "upstream-error", $"MusicBrainz returned HTTP {(int)response.StatusCode}.", null);

            var payload = await response.Content.ReadFromJsonAsync<ReleaseSearchResponse>(JsonOpts, ct);
            return (payload?.Releases ?? new List<Release>())
                .Select(MapRelease)
                .ToList();
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Serializes callers and spaces requests at least <see cref="_minInterval"/> apart.
    /// Requests queue (wait their turn) rather than being dropped. The client must be a shared
    /// (singleton) instance for the queue to mean anything across requests.
    /// </summary>
    private async Task AcquireSlotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequestUtc + _minInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static MusicBrainzResult MapRelease(Release release)
    {
        string? artist = null;
        if (release.ArtistCredit is { Count: > 0 })
        {
            var first = release.ArtistCredit[0].Name;
            artist = string.IsNullOrWhiteSpace(first) ? null : first;
        }

        int? year = null;
        if (!string.IsNullOrEmpty(release.Date) &&
            int.TryParse(release.Date.AsSpan(0, Math.Min(4, release.Date.Length)), out var y) &&
            // ST-65: the app's canonical range (ImportService/MetadataReviewJob accept 1000–2100)
            // — a year stored outside it can never be re-proposed, so don't propose it either.
            y is >= 1000 and <= 2100)
        {
            year = y;
        }

        var title = string.IsNullOrWhiteSpace(release.Title) ? null : release.Title;
        return new MusicBrainzResult(
            release.Id ?? string.Empty,
            title ?? string.Empty,
            artist,
            title, // release search has no separate album field; the release title IS the album
            year,
            release.Score);
    }

    // --- Wire shapes (only the fields we use) --------------------------------

    private sealed class ReleaseSearchResponse
    {
        public List<Release>? Releases { get; set; }
    }

    private sealed class Release
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public int? Score { get; set; }
        public string? Date { get; set; }

        [JsonPropertyName("artist-credit")]
        public List<ArtistCredit>? ArtistCredit { get; set; }
    }

    private sealed class ArtistCredit
    {
        public string? Name { get; set; }
    }
}
