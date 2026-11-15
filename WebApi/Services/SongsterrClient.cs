using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace WebApi.Services;

/// <summary>One Songsterr search hit (song level).</summary>
public record SongsterrResult(
    int SongId,
    string Title,
    string? Artist,
    bool HasChords,
    IReadOnlyList<SongsterrTrack> Tracks);

/// <summary>A track/arrangement of a song on Songsterr (instrument + difficulty).</summary>
public record SongsterrTrack(string Instrument, int? Difficulty);

/// <summary>
/// Carries the HTTP status + stable error code the endpoint should return, so callers can
/// translate failures into the standard { error: { code, message } } shape.
/// </summary>
public class SongsterrException(int httpStatus, string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public int HttpStatus { get; } = httpStatus;
    public string Code { get; } = code;
}

/// <summary>
/// Thin client for Songsterr's public song-search API (ST-44). No key required. Search only —
/// the chord notation itself lives behind the HTML view page and is NOT fetched here (license
/// check first, see docs/tasks/ST-44.md). A light client-side throttle (1 request / 0.5s) keeps
/// us polite; requests queue, never drop.
/// </summary>
public class SongsterrClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>No documented limit from Songsterr — a modest courtesy throttle.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _http;
    // ST-65: mutable — a 429's Retry-After bumps the spacing for all queued callers.
    private TimeSpan _minInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public SongsterrClient(HttpClient http, TimeSpan? minInterval = null)
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
    /// Searches songs for <paramref name="pattern"/> (max <paramref name="limit"/> results).
    /// Waits its turn in the throttle queue; never drops a request.
    /// </summary>
    public async Task<IReadOnlyList<SongsterrResult>> SearchAsync(string pattern, int limit, CancellationToken ct = default)
    {
        // Relative (no leading slash) so it resolves against the base address https://www.songsterr.com/
        var url = $"api/songs?pattern={Uri.EscapeDataString(pattern)}&limit={limit}";

        await AcquireSlotAsync(ct);
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SongsterrException(502, "upstream-error", "The Songsterr request timed out.", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException)
        {
            throw new SongsterrException(502, "upstream-error", "Songsterr is unreachable.", ex);
        }

        try
        {
            // ST-65: 429 — honor Retry-After by widening the queue spacing for all callers.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                BumpMinInterval(response.Headers.RetryAfter?.Delta);
            if (!response.IsSuccessStatusCode)
                throw new SongsterrException(502, "upstream-error", $"Songsterr returned HTTP {(int)response.StatusCode}.", null);

            List<SongWire>? songs;
            try
            {
                songs = await response.Content.ReadFromJsonAsync<List<SongWire>>(JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                throw new SongsterrException(502, "upstream-error", "Unexpected response from Songsterr.", ex);
            }
            // ST-65: entries without a usable id are dropped (the old `?? 0` surfaced a fake
            // "song 0" the moment the upstream wire shape changed).
            return (songs ?? new List<SongWire>())
                .Where(s => (s.SongId ?? 0) > 0)
                .Select(Map)
                .Take(limit)
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

    private static SongsterrResult Map(SongWire song) => new(
        song.SongId ?? 0,
        string.IsNullOrWhiteSpace(song.Title) ? string.Empty : song.Title,
        string.IsNullOrWhiteSpace(song.Artist) ? null : song.Artist,
        song.HasChords,
        (song.Tracks ?? new List<TrackWire>())
            .Select(t => new SongsterrTrack(
                string.IsNullOrWhiteSpace(t.Instrument) ? "unknown" : t.Instrument,
                t.Difficulty))
            .ToList());

    // --- Wire shapes (only the fields we use) --------------------------------

    private sealed class SongWire
    {
        public int? SongId { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public bool HasChords { get; set; }
        public List<TrackWire>? Tracks { get; set; }
    }

    private sealed class TrackWire
    {
        public string? Instrument { get; set; }
        public int? Difficulty { get; set; }
    }
}
