using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// /api/musicbrainz endpoints (ST-12): proxy track search to the MusicBrainz API so the editor
/// can suggest title/artist/album/year. MusicBrainz is metadata-only (no chords). The /api group
/// is RequireAuthorization by default (see Program.cs).
/// </summary>
public static class MusicBrainzEndpoints
{
    private const int MinQueryLength = 2;
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    /// <summary>ST-45: search scopes the /import Search tab can request. "artists" restricts the
    /// query to releases by matching artists (MusicBrainz field-scoped `artist:"…"` term); the
    /// others are plain release queries.</summary>
    private static readonly HashSet<string> Scopes = new(StringComparer.OrdinalIgnoreCase)
        { "everything", "songs", "artists", "albums" };

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapMusicBrainzEndpoints(this RouteGroupBuilder api)
    {
        var musicbrainz = api.MapGroup("musicbrainz");

        musicbrainz.MapGet("/search", async (string? q, int limit = DefaultLimit, string? scope = null, MusicBrainzClient? client = null) =>
        {
            var query = q?.Trim();
            if (query is null || query.Length < MinQueryLength)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.",
                    new Dictionary<string, string?> { ["q"] = $"Search query must be at least {MinQueryLength} characters." });
            if (!string.IsNullOrEmpty(scope) && !Scopes.Contains(scope))
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.",
                    new Dictionary<string, string?> { ["scope"] = "Scope must be one of: everything, songs, artists, albums." });
            if (client is null)
                return Error(StatusCodes.Status503ServiceUnavailable, "upstream-error", "MusicBrainz client is not configured.");

            // ST-45: artist scope → field-scoped MusicBrainz query so results are releases BY matching artists.
            // ST-65: escape quotes — an unescaped " in the query closed the Lucene clause early
            // (injection into the upstream query syntax).
            var upstreamQuery = string.Equals(scope, "artists", StringComparison.OrdinalIgnoreCase)
                ? $"artist:\"{query.Replace("\"", "\\\"")}\""
                : query;

            limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
            try
            {
                var results = await client.SearchAsync(upstreamQuery, limit);
                return Results.Ok(new { results });
            }
            catch (MusicBrainzException ex)
            {
                return Error(ex.HttpStatus, ex.Code, ex.Message);
            }
        });

        return api;
    }

    private static IResult Error(int statusCode, string code, string message, Dictionary<string, string?>? fields = null)
        => Results.Json(new { error = new { code, message, fields } }, statusCode: statusCode, options: Json);
}
