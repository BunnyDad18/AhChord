using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// ST-46 (bug-56): source files attached to a song — the original PDF/PNG/JPG the chart was taken
/// from. Stored as bytes in Postgres and served inline so it can be viewed on the song page. The
/// /api group is RequireAuthorization by default (see Program.cs); errors use the
/// { error: { code, message } } shape like the other endpoint files.
///
///   POST   /api/songs/{id}/source-files  — multipart upload (field "file"); pdf/png/jpg only; 10 MB cap
///   GET    /api/songs/{id}/source-files  — metadata list, oldest first
///   GET    /api/source-files/{id}        — raw bytes, inline disposition (no Content-Disposition)
///   DELETE /api/source-files/{id}        — remove
/// </summary>
public static class SongSourceFileEndpoints
{
    private const long MaxSizeBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>ST-63: transport-level cap = payload limit + multipart framing headroom.</summary>
    private const long MaxRequestBytes = 11 * 1024 * 1024;

    /// <summary>ST-63: attachments per song — without a cap the bytea column grows unbounded.</summary>
    private const int MaxFilesPerSong = 10;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Allowed extensions and their canonical content types — the extension is the source of truth.</summary>
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    public static RouteGroupBuilder MapSongSourceFileEndpoints(this RouteGroupBuilder api)
    {
        var songs = api.MapGroup("songs");

        // .DisableAntiforgery(): form-bound minimal-API endpoints get anti-forgery by default in
        // .NET 8+, which requires UseAntiforgery() middleware. This is a JWT API — the Bearer token
        // is the auth mechanism, so opt out (same as any other non-cookie client).
        // ST-63 (finding 4): the endpoint filter lowers Kestrel's body cap for this route
        // (11 MB = 10 MB file + multipart framing) so oversized uploads die in transport with
        // 413 — the old file.Length check ran only after Kestrel had fully spooled the request.
        // Files per song are capped so the bytea column can't grow unbounded.
        songs.MapPost("/{id:guid}/source-files", async (Guid id, IFormFile file, ApplicationDbContext db) =>
        {
            if (!await SongExistsAsync(db, id))
                return Error(StatusCodes.Status404NotFound, "not-found", "Song not found.");

            if (await db.SongSourceFiles.CountAsync(f => f.SongId == id) >= MaxFilesPerSong)
                return Error(StatusCodes.Status400BadRequest, "too-many-files",
                    $"A song can have at most {MaxFilesPerSong} attached source files.");

            var ext = Path.GetExtension(file.FileName);
            if (!AllowedTypes.TryGetValue(ext, out var contentType))
                return Error(StatusCodes.Status400BadRequest, "unsupported-type",
                    "Only PDF, PNG and JPG files are supported as source files.");

            if (file.Length > MaxSizeBytes)
                return Error(StatusCodes.Status400BadRequest, "too-large",
                    $"File is larger than the {MaxSizeBytes / 1024 / 1024} MB limit.");

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);

            var sourceFile = new SongSourceFile
            {
                Id = Guid.NewGuid(),
                SongId = id,
                FileName = Path.GetFileName(file.FileName),
                ContentType = contentType,
                SizeBytes = (long)ms.Length,
                Data = ms.ToArray(),
                CreatedAt = DateTime.UtcNow,
            };

            db.SongSourceFiles.Add(sourceFile);
            await db.SaveChangesAsync();

            // No explicit options: DTOs must serialize with the app's default (camelCase) config —
            // `Json` is only for the already-lowercase anonymous error objects.
            return Results.Json(ToDto(sourceFile), statusCode: StatusCodes.Status201Created);
        })
            .AddEndpointFilter((ctx, next) =>
            {
                // ST-63: per-route body cap — runs before the handler binds the form, so an
                // oversized body is killed by Kestrel (413) without being spooled.
                var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                    feature.MaxRequestBodySize = MaxRequestBytes;
                return next(ctx);
            })
            .DisableAntiforgery();

        songs.MapGet("/{id:guid}/source-files", async (Guid id, ApplicationDbContext db) =>
        {
            if (!await SongExistsAsync(db, id))
                return Error(StatusCodes.Status404NotFound, "not-found", "Song not found.");

            var files = await db.SongSourceFiles.AsNoTracking()
                .Where(f => f.SongId == id)
                .OrderBy(f => f.CreatedAt)
                .ThenBy(f => f.Id)
                .Select(f => new SongSourceFileDto(f.Id, f.SongId, f.FileName, f.ContentType, f.SizeBytes, f.CreatedAt))
                .ToListAsync();

            return Results.Json(files);
        });

        // No Content-Disposition header (no fileDownloadName): browsers render PDFs/images inline
        // from the content type when loaded in an <iframe>/<img>. Range processing for PDF viewers.
        api.MapGet("/source-files/{id:guid}", async (Guid id, HttpContext http, ApplicationDbContext db) =>
        {
            var file = await db.SongSourceFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
            if (file is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Source file not found.");

            // ST-65: the content type comes from the (allow-listed) file extension — tell the
            // browser not to sniff past it.
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // No fileDownloadName → no Content-Disposition header: browsers render PDFs/images
            // inline from the content type. Range processing for PDF viewers.
            return Results.File(file.Data, file.ContentType, enableRangeProcessing: true);
        });

        api.MapDelete("/source-files/{id:guid}", async (Guid id, HttpContext http, ApplicationDbContext db, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: deleting an attachment destroys a file no one else can
            // re-upload. Additive uploads stay open (the shared library is the product).
            if (await Authz.DenySharedDestroyAsync(http, policy, "delete a source file") is { } denied)
                return denied;

            var file = await db.SongSourceFiles.FirstOrDefaultAsync(f => f.Id == id);
            if (file is null)
                return Error(StatusCodes.Status404NotFound, "not-found", "Source file not found.");

            db.SongSourceFiles.Remove(file);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return api;
    }

    private static async Task<bool> SongExistsAsync(ApplicationDbContext db, Guid id)
        => await db.Songs.AsNoTracking().AnyAsync(s => s.Id == id);

    private static SongSourceFileDto ToDto(SongSourceFile f)
        => new(f.Id, f.SongId, f.FileName, f.ContentType, f.SizeBytes, f.CreatedAt);

    private static IResult Error(int statusCode, string code, string message)
        => Results.Json(new { error = new { code, message } }, statusCode: statusCode, options: Json);
}
