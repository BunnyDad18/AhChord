using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// /api/songsterr endpoints (ST-44): proxy song search to Songsterr's public API so the import
/// screen can suggest songs that have chords. Search only — no chord text is fetched here. The
/// /api group is RequireAuthorization by default (see Program.cs).
/// </summary>
public static class SongsterrEndpoints
{
    private const int MinQueryLength = 2;
    private const int DefaultLimit = 10;
    // ST-45: raised from 20 so the /import Search tab can fetch a longer, scrollable results list.
    private const int MaxLimit = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapSongsterrEndpoints(this RouteGroupBuilder api)
    {
        var songsterr = api.MapGroup("songsterr");

        songsterr.MapGet("/search", async (string? q, int limit = DefaultLimit, SongsterrClient? client = null) =>
        {
            var query = q?.Trim();
            if (query is null || query.Length < MinQueryLength)
                return Error(StatusCodes.Status400BadRequest, "validation-failed", "Invalid request.",
                    new Dictionary<string, string?> { ["q"] = $"Search query must be at least {MinQueryLength} characters." });
            if (client is null)
                return Error(StatusCodes.Status503ServiceUnavailable, "upstream-error", "Songsterr client is not configured.");

            limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
            try
            {
                var results = await client.SearchAsync(query, limit);
                return Results.Ok(new { results });
            }
            catch (SongsterrException ex)
            {
                return Error(ex.HttpStatus, ex.Code, ex.Message);
            }
        });

        return api;
    }

    private static IResult Error(int statusCode, string code, string message, Dictionary<string, string?>? fields = null)
        => Results.Json(new { error = new { code, message, fields } }, statusCode: statusCode, options: Json);
}
