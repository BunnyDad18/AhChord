using System.Security.Claims;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// ST-35 F3: metadata auto-review.
///
///   POST /api/songs/metadata-review                    → 202 — enqueue a full-library scan (background)
///   GET  /api/songs/metadata-review/progress           → ST-68: live scan progress (current song, n/total, ETA)
///   GET  /api/songs/{id}/proposals                     → the song's pending proposals
///   POST /api/songs/{id}/proposals/{pid}/accept        → apply the value to the song, mark accepted
///   POST /api/songs/{id}/proposals/{pid}/reject        → discard (no change)
///   POST /api/songs/{id}/proposals/accept-all          → ST-67: accept every pending proposal (mass-approve)
///   GET  /api/songs/proposals/pending                  → ST-69: library-wide pending proposals, grouped by field+value
///   GET  /api/songs/proposals/pending/count            → ST-70g: cheap pending count for the activity box
///   POST /api/songs/proposals/accept-category          → ST-69: accept every pending proposal matching {field, value}
///   POST /api/songs/proposals/accept-field             → ST-71: accept every pending proposal for a field, all values
///
/// All [Authorize] via the /api group; errors use the { error: { code, message } } shape.
/// </summary>
public static class MetadataReviewEndpoints
{
    public static RouteGroupBuilder MapMetadataReviewEndpoints(this RouteGroupBuilder api)
    {
        var songs = api.MapGroup("songs");

        // Trigger a full-library scan. ST-58 coalescing: while a scan sits queued (AppMeta
        // sentinel, cleared by the job at start), repeat clicks are a no-op — K clicks used to
        // enqueue K full-library serial LLM scans (days of backlog, no cancel).
        songs.MapPost("/metadata-review", async (HttpContext http, ApplicationDbContext db, IBackgroundJobClient jobs) =>
        {
            var userId = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (await db.AppMeta.FindAsync("scan-pending") is not null)
                return Results.Json(new { started = false, alreadyQueued = true });

            db.AppMeta.Add(new AppMeta { Key = "scan-pending", Value = DateTime.UtcNow.ToString("O") });
            await db.SaveChangesAsync();
            jobs.Enqueue<MetadataReviewJob>(job => job.ScanLibraryAsync(userId));
            return Results.Json(new { started = true }, statusCode: StatusCodes.Status202Accepted);
        });

        // ST-68: live scan progress for the Songs page strip — what it's working on, how far,
        // elapsed, coarse ETA. "running" is the live signal; a finished/interrupted row is
        // reported so the UI sees the terminal state (toast on the running→not-running edge).
        songs.MapGet("/metadata-review/progress", async (ApplicationDbContext db) =>
        {
            var row = await db.MetadataScanProgresses.AsNoTracking().FirstOrDefaultAsync(m => m.Key == "library");
            if (row is null)
                return Results.Ok(new { running = false, status = "none" });

            var elapsed = (row.FinishedAt ?? DateTime.UtcNow) - row.StartedAt;
            return Results.Ok(new
            {
                running = row.Status == "running",
                status = row.Status,
                current = row.CurrentTitle,
                index = row.Index,
                total = row.Total,
                startedAt = row.StartedAt,
                elapsedSeconds = (int)elapsed.TotalSeconds,
                etaSeconds = ComputeEtaSeconds(elapsed, row.Index, row.Total, row.Status),
                proposalsAdded = row.ProposalsAdded
            });
        });

        // The song's pending proposals (newest first).
        songs.MapGet("/{id:guid}/proposals", async (Guid id, ApplicationDbContext db) =>
        {
            if (!await db.Songs.AnyAsync(s => s.Id == id))
                return Error(404, "not-found", "Song not found.");

            var proposals = await db.MetadataProposals.AsNoTracking()
                .Where(p => p.SongId == id && p.Status == "pending")
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new { p.Id, p.Field, p.CurrentValue, p.ProposedValue, p.CreatedAt })
                .ToListAsync();

            return Results.Ok(proposals);
        });

        // Accept: apply the proposed value to the song and mark the proposal resolved.
        songs.MapPost("/{id:guid}/proposals/{pid:guid}/accept", async (Guid id, Guid pid, ApplicationDbContext db) =>
        {
            var (song, proposal) = await FindProposalAsync(db, id, pid);
            if (song is null || proposal is null)
                return Error(404, "not-found", "No pending proposal with that id.");

            if (!MetadataReviewJob.TryApply(song!, proposal!.Field, proposal.ProposedValue))
                return Error(409, "unapplyable", "This proposed value can no longer be applied.");

            proposal.Status = "accepted";
            proposal.ResolvedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Json(new { ok = true });
        });

        // ST-67: mass-approve — accept every pending proposal for one song in a single pass.
        // Newest-first (the panel's order); proposals that fail TryApply stay pending, matching
        // the per-proposal "unapplyable" semantics. Library-wide category approval: ST-69 below.
        songs.MapPost("/{id:guid}/proposals/accept-all", async (Guid id, ApplicationDbContext db) =>
        {
            var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == id);
            if (song is null)
                return Error(404, "not-found", "Song not found.");

            var proposals = await db.MetadataProposals
                .Where(p => p.SongId == id && p.Status == "pending")
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            int applied = 0, skipped = 0;
            foreach (var p in proposals)
            {
                if (!MetadataReviewJob.TryApply(song, p.Field, p.ProposedValue))
                {
                    skipped++;
                    continue;
                }
                p.Status = "accepted";
                p.ResolvedAt = DateTime.UtcNow;
                applied++;
            }
            await db.SaveChangesAsync();

            return Results.Json(new { ok = true, applied, skipped });
        });

        // ST-69: library-wide review — every pending proposal grouped by (field, proposed value),
        // e.g. "genre → Rock, 12 songs". Grouping on the exact ProposedValue the job generated,
        // so identical proposals always collapse into one category. Library scale is small —
        // flat fetch + in-memory grouping (an EF GroupBy projecting group members won't translate).
        songs.MapGet("/proposals/pending", async (ApplicationDbContext db) =>
        {
            var rows = await db.MetadataProposals.AsNoTracking()
                .Where(p => p.Status == "pending")
                .Select(p => new
                {
                    p.Field,
                    p.ProposedValue,
                    p.CurrentValue,
                    p.SongId,
                    Title = p.Song!.Title,
                    Artist = p.Song!.Artist
                })
                .ToListAsync();

            var groups = rows
                .GroupBy(r => new { r.Field, r.ProposedValue })
                .Select(g => new
                {
                    field = g.Key.Field,
                    value = g.Key.ProposedValue,
                    count = g.Count(),
                    songs = g
                        .Select(r => new { songId = r.SongId, r.Title, r.Artist, r.CurrentValue })
                        .OrderBy(s => s.Title)
                        .ToList()
                })
                .OrderByDescending(g => g.count)
                .ThenBy(g => g.field)
                .ThenBy(g => g.value)
                .ToList();

            return Results.Ok(groups);
        });

        // ST-70g: cheap count for the top-bar activity box — "items that need
        // reviewing" is pending work, so the box polls this (full groups endpoint
        // above would pull every proposal row on each tick).
        songs.MapGet("/proposals/pending/count", async (ApplicationDbContext db) =>
            Results.Ok(new
            {
                count = await db.MetadataProposals.AsNoTracking()
                    .CountAsync(p => p.Status == "pending"),
            }));

        // ST-69: mass-approve one category — apply every pending proposal with this field+value.
        // Same TryApply validation as the per-proposal/per-song paths; proposals that can no
        // longer be applied stay pending.
        songs.MapPost("/proposals/accept-category", async (AcceptCategoryRequest request, ApplicationDbContext db) =>
        {
            var field = (request.Field ?? "").Trim().ToLowerInvariant();
            if (!MetadataReviewJob.ValidFields.Contains(field))
                return Error(400, "validation-failed",
                    $"'field' must be one of: {string.Join(", ", MetadataReviewJob.ValidFields)}.");
            if (string.IsNullOrWhiteSpace(request.Value))
                return Error(400, "validation-failed", "'value' is required.");

            var result = await ApplyProposalsAsync(db, field, request.Value);
            if (result is null)
                return Error(404, "not-found", "No pending proposals with that field and value.");

            var (applied, skipped, songs) = result.Value;
            return Results.Json(new { ok = true, applied, skipped, songs });
        });

        // ST-71 (in-app report 2026-11-01 15:49): mass-approve a whole FIELD — every pending
        // proposal for it, across all its values ("accept all year changes", not just year→1985).
        // ST-69's per-value categories are too fine for fields that spread over many values
        // (31 distinct years, 18 keys); this is the one-click "field done" button.
        songs.MapPost("/proposals/accept-field", async (AcceptFieldRequest request, ApplicationDbContext db) =>
        {
            var field = (request.Field ?? "").Trim().ToLowerInvariant();
            if (!MetadataReviewJob.ValidFields.Contains(field))
                return Error(400, "validation-failed",
                    $"'field' must be one of: {string.Join(", ", MetadataReviewJob.ValidFields)}.");

            var result = await ApplyProposalsAsync(db, field, value: null);
            if (result is null)
                return Error(404, "not-found", "No pending proposals for that field.");

            var (applied, skipped, songs) = result.Value;
            return Results.Json(new { ok = true, applied, skipped, songs });
        });

        // Reject: discard the proposal without touching the song.
        songs.MapPost("/{id:guid}/proposals/{pid:guid}/reject", async (Guid id, Guid pid, ApplicationDbContext db) =>
        {
            var (_, proposal) = await FindProposalAsync(db, id, pid);
            if (proposal is null)
                return Error(404, "not-found", "No pending proposal with that id.");

            proposal.Status = "rejected";
            proposal.ResolvedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Json(new { ok = true });
        });

        return api;
    }

    private static async Task<(Song? Song, MetadataProposal? Proposal)> FindProposalAsync(
        ApplicationDbContext db, Guid songId, Guid proposalId)
    {
        var proposal = await db.MetadataProposals.FirstOrDefaultAsync(p => p.Id == proposalId && p.SongId == songId);
        if (proposal is null || proposal.Status != "pending")
            return (null, null);

        var song = await db.Songs.FirstOrDefaultAsync(s => s.Id == songId);
        return (song, proposal);
    }

    private static IResult Error(int statusCode, string code, string message)
        => Results.Json(new { error = new { code, message } }, statusCode: statusCode);

    /// <summary>
    /// ST-69/ST-71: apply every pending proposal for a field — optionally narrowed to one
    /// proposed value (category). Same TryApply validation as the per-proposal/per-song paths;
    /// proposals that can no longer be applied (and proposals whose song was deleted) stay
    /// pending. Null result when nothing pending matches (the endpoints 404 on that).
    /// </summary>
    internal static async Task<(int Applied, int Skipped, int Songs)?> ApplyProposalsAsync(
        ApplicationDbContext db, string field, string? value)
    {
        var query = db.MetadataProposals.Where(p => p.Status == "pending" && p.Field == field);
        if (value is not null)
            query = query.Where(p => p.ProposedValue == value);

        var proposals = await query.OrderByDescending(p => p.CreatedAt).ToListAsync();
        if (proposals.Count == 0)
            return null;

        var songIds = proposals.Select(p => p.SongId).Distinct().ToList();
        var songsById = await db.Songs
            .Where(s => songIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id);

        int applied = 0, skipped = 0;
        foreach (var p in proposals)
        {
            // A proposal whose song was deleted under the job skips like an unapplyable one.
            if (!songsById.TryGetValue(p.SongId, out var song) || !MetadataReviewJob.TryApply(song, p.Field, p.ProposedValue))
            {
                skipped++;
                continue;
            }
            p.Status = "accepted";
            p.ResolvedAt = DateTime.UtcNow;
            applied++;
        }
        await db.SaveChangesAsync();

        return (applied, skipped, songsById.Count);
    }

    /// <summary>
    /// ST-68: coarse ETA — elapsed × remaining / processed. Coarse by design: skip-vs-LLM
    /// per-song cost varies a lot, and songs with a pending proposal round are near-instant,
    /// so the rate only stabilizes over a scan. Null until there is a meaningful rate
    /// (running, ≥1 processed, songs left).
    /// </summary>
    internal static int? ComputeEtaSeconds(TimeSpan elapsed, int index, int total, string status)
    {
        if (status != "running" || index < 1 || index >= total)
            return null;
        return (int)Math.Round(elapsed.TotalSeconds * (total - index) / index);
    }
}

/// <summary>ST-69: body of POST /api/songs/proposals/accept-category.</summary>
public record AcceptCategoryRequest(string? Field, string? Value);

/// <summary>ST-71: body of POST /api/songs/proposals/accept-field.</summary>
public record AcceptFieldRequest(string? Field);
