using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// POST /api/songs/import (ST-13): paste ChordPro text -> stored Song. The parser (ST-07) is the
/// validator — zero lines or zero chords means 400 "not-chordpro", never a 500. Metadata
/// precedence: body fields > ChordPro header directives > null. All [Authorize] via the /api
/// group (see Program.cs); errors use the { error: { code, message, fields? } } shape.
/// </summary>
public static class ImportEndpoints
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapImportEndpoints(this RouteGroupBuilder api)
    {
        var songs = api.MapGroup("songs");

        songs.MapPost("/import", async (ImportSongRequest request, HttpContext http, ApplicationDbContext db, IBackgroundJobClient jobs) =>
        {
            try
            {
                var (song, duplicate) = await ImportService.CreateSongAsync(
                    db, request.ChordProText, request.Title, request.Artist,
                    request.Genre, request.Year, request.Album, request.PlaylistId, request.SourceUrl);

                // ST-35 F3: new songs are scanned for missing metadata automatically (background).
                // Duplicates need no scan — the existing song was already reviewed.
                if (!duplicate)
                {
                    var userId = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
                    jobs.Enqueue<MetadataReviewJob>(job => job.ScanSongAsync(song.Id, userId));
                }

                // ST-49: 201 when a song was created, 200 + duplicate flag when it already existed.
                var parsed = ChordProParser.Parse(song.ChordProText);
                return Results.Json(new { song = ToDetailDto(song), parsed = parsed, duplicate },
                    statusCode: duplicate ? StatusCodes.Status200OK : StatusCodes.Status201Created);
            }
            catch (ImportException ex)
            {
                return Error(ex.StatusCode, ex.Code, ex.Message, ex.Fields);
            }
        });

        return api;
    }

    private static SongDetailDto ToDetailDto(Song s)
        => new(s.Id, s.Title, s.Artist, s.Album, s.Year, s.Key, s.Genre, s.Tempo, s.CapoFret, s.CreatedAt, s.UpdatedAt, s.ChordProText, s.SourceUrl);

    private static IResult Error(int statusCode, string code, string message, Dictionary<string, string?>? fields = null)
        => Results.Json(new { error = new { code, message, fields } }, statusCode: statusCode, options: Json);
}
