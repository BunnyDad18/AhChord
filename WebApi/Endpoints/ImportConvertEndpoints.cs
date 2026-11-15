using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// ST-14: in-app chord-page→ChordPro conversion (Hangfire job queue + OpenAI-compatible LLM).
///
///   POST /api/import/convert           → 202 { id, status:"queued" }   (503 llm-not-configured when no LLM resolves for the user)
///   GET  /api/import/convert           → the caller's drafts, newest first (ST-33 queue view; light — no chordProText)
///   GET  /api/import/convert/{id}      → draft status + chordProText/error        (owner)
///   DELETE /api/import/convert/{id}    → remove a draft: queued = cancel (the job exits when its turn comes),
///                                   finished = cleanup; running → 409 in-progress (ST-33)   (owner)
///   DELETE /api/import/convert/imported → remove ALL imported drafts of the caller (report 2026-11-12)
///   POST /api/import/convert/{id}/save → 201 { song, parsed }          (reuses ImportService.CreateSongAsync — same rules as ST-13)   (owner)
///   POST /api/import/convert/{id}/retry → re-enqueue a failed conversion (ST-35 E2)              (owner)
///   POST /api/import/convert/import-all → ST-73: save every converted draft as a song in one pass  (caller's drafts)
///
/// ST-56: every id-addressed route checks draft ownership (foreign ids → 404, not 403).
/// All [Authorize] via the /api group; errors use the { error: { code, message, fields? } } shape.
/// </summary>
public static class ImportConvertEndpoints
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static RouteGroupBuilder MapImportConvertEndpoints(this RouteGroupBuilder api)
    {
        var convert = api.MapGroup("import/convert");
        // Public-demo gate 2: the budget is on the routes that QUEUE work on the one GPU —
        // the enqueue POST, the retry, the bulk import.
        // NOT on the group: the UI reads GET /api/import/convert at boot, on every progress event
        // and on each SignalR reconnect, so a group-level policy spends the 10/60s budget on
        // polling and answers a real user's 11th paste with 429 (review finding).

        var enqueue = convert.MapPost("", async (ConvertRequest request, HttpContext http, ApplicationDbContext db, ILlmSettingsProvider settings, IBackgroundJobClient jobs) =>
        {
            if (string.IsNullOrWhiteSpace(request.Content))
                return Error(400, "validation-failed",
                    "Provide 'content' with the chord-page text to convert.");

            // ST-57: pasted content feeds the LLM prompt and a Postgres row — cap it.
            // ST-122: the cap is BYTES (the LLM prompt and the row are byte-bounded), so
            // count the UTF-8 length — string.Length would pass a CJK paste at ~3× the bound.
            if (Encoding.UTF8.GetByteCount(request.Content) > InputLimits.MaxConversionInputBytes)
                return Error(400, "validation-failed",
                    $"'content' is too large (max {InputLimits.MaxConversionInputBytes / 1024} KB).");

            // ST-30: resolve the requester's LLM settings (user → global → .env); 503 when none.
            var userId = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var effective = await settings.ResolveAsync(userId);
            if (string.IsNullOrWhiteSpace(effective.BaseUrl))
                return Error(503, "llm-not-configured",
                    "The in-app LLM converter is not configured. Set it up in Settings → LLM conversion — or use the paste flow (Tab 1) which needs no LLM.");

            var draft = new Entities.ImportDraft
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Status = "queued",
                InputText = request.Content,
                CreatedAt = DateTime.UtcNow
            };

            // ST-37 A1: find the song name at queue time so cards don't just say "tab".
            draft.InputPreview = FindTitle(request.Content);

            db.ImportDrafts.Add(draft);
            // ST-65: async EF — sync SaveChanges/Find pinned request threads.
            await db.SaveChangesAsync();

            // AHC-9: enqueue through the injected client — never the static BackgroundJob.
            jobs.Enqueue<ChordProConversionJob>(job => job.RunAsync(draft.Id));
            return Results.Json(new { id = draft.Id, status = draft.Status }, statusCode: StatusCodes.Status202Accepted);
        });
        // The GPU-cost surface: 10 conversions per user per minute (Program.cs).
        enqueue.RequireRateLimiting("convert");

        // ST-33: the caller's conversion queue (newest first). Light on purpose — no chordProText;
        // GET /{id} fetches that when a row is reviewed. Live updates arrive over SignalR.
        convert.MapGet("", async (HttpContext http, ApplicationDbContext db) =>
        {
            var userId = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var drafts = await db.ImportDrafts.AsNoTracking()
                .Where(d => d.UserId == userId)
                .OrderByDescending(d => d.CreatedAt)
                .Take(50)
                .ToListAsync();

            // ST-35 E4: duplicate signal — does the converted title+artist already exist in the library?
            // The songs table is small (self-hosted band app), so match in memory.
            var songPairs = await db.Songs.AsNoTracking()
                .Select(s => new { s.Title, s.Artist })
                .ToListAsync();

            bool MatchesLibrary(ImportDraft d)
            {
                if (d.Status != "succeeded" || string.IsNullOrWhiteSpace(d.ExtractedTitle))
                    return false;
                var t = d.ExtractedTitle.Trim();
                var a = string.IsNullOrWhiteSpace(d.ExtractedArtist) ? null : d.ExtractedArtist.Trim();
                return songPairs.Any(sp =>
                    sp.Title.Equals(t, StringComparison.OrdinalIgnoreCase)
                    && (a is null || (sp.Artist != null && sp.Artist.Equals(a, StringComparison.OrdinalIgnoreCase))));
            }

            return Results.Json(drafts.Select(d => InputSummary(d, MatchesLibrary(d))));
        });

        // ST-33: cancel (queued) or clean up (finished). Running conversions can't be stopped
        // mid-generation — the LLM call has no cancellation path through Hangfire.
        // ST-56: drafts are per-user — foreign drafts report 404 (not 403) to avoid id probing.
        convert.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ApplicationDbContext db) =>
        {
            var draft = await db.ImportDrafts.FindAsync(id);
            if (draft is null || !IsOwner(draft, CurrentUserId(http)))
                return Error(404, "not-found", "No conversion with that id.");
            if (draft.Status == "running")
                return Error(409, "in-progress",
                    "This conversion is running — wait for it to finish (it can't be stopped mid-generation).");

            db.ImportDrafts.Remove(draft);
            await db.SaveChangesAsync();
            // A queued draft's Hangfire job simply finds no row when its turn comes and exits.
            return Results.NoContent();
        });

        // Bug report (2026-11-12, /queue): "clear all imported" — one click retires the
        // imported rows (succeeded with a saved song) instead of binning them one by one.
        // Imported means spent: the song is in the library, the draft has no future action.
        // Converted-but-unimported and failed rows stay (Review/Retry are live on those).
        convert.MapDelete("/imported", async (HttpContext http, ApplicationDbContext db) =>
        {
            var removed = await ClearImportedAsync(db, CurrentUserId(http));
            return Results.Json(new { ok = true, removed });
        });

        // ST-35 E2: retry a failed conversion — reset the draft and re-enqueue it in the serial queue.
        // ST-56: owner only (a retry burns the owner's LLM quota and re-fetches URLs server-side).
        var retry = convert.MapPost("/{id:guid}/retry", async (Guid id, HttpContext http, ApplicationDbContext db, IBackgroundJobClient jobs) =>
        {
            var draft = await db.ImportDrafts.FindAsync(id);
            if (draft is null || !IsOwner(draft, CurrentUserId(http)))
                return Error(404, "not-found", "No conversion with that id.");
            if (draft.Status != "failed")
                return Error(409, "not-retryable", $"Only failed conversions can be retried (this one is {draft.Status}).");

            draft.Status = "queued";
            draft.Error = null;
            draft.ChordProText = null;
            draft.ExtractedTitle = null;
            draft.ExtractedArtist = null;
            draft.FinishedAt = null;
            await db.SaveChangesAsync();

            jobs.Enqueue<ChordProConversionJob>(job => job.RunAsync(draft.Id));
            return Results.Json(new { id = draft.Id, status = draft.Status }, statusCode: StatusCodes.Status202Accepted);
        });
        // Re-enqueues a model run — same budget as the enqueue POST.
        retry.RequireRateLimiting("convert");

        convert.MapGet("/{id:guid}", async (Guid id, HttpContext http, ApplicationDbContext db) =>
        {
            var draft = await db.ImportDrafts.FindAsync(id);
            if (draft is null || !IsOwner(draft, CurrentUserId(http)))
                return Error(404, "not-found", "No conversion with that id.");
            return Results.Json(new
            {
                id = draft.Id,
                status = draft.Status,
                chordProText = draft.ChordProText,
                error = draft.Error,
                createdAt = draft.CreatedAt,
                finishedAt = draft.FinishedAt,
                savedSongId = draft.SavedSongId
            }, options: Json);
        });

        // ST-73: Import All — every converted draft of the caller that hasn't been imported
        // yet becomes a song in one pass, with the same rules as the per-draft save
        // (ChordPro-header metadata, duplicate detection, auto metadata scan for new songs).
        // Drafts that fail to import stay in the queue (Review them individually).
        var importAll = convert.MapPost("/import-all", async (HttpContext http, ApplicationDbContext db, IBackgroundJobClient jobs) =>
        {
            var userId = CurrentUserId(http);
            var result = await ImportAllAsync(db, userId,
                song => jobs.Enqueue<MetadataReviewJob>(job => job.ScanSongAsync(song.Id, userId)));

            return Results.Json(new
            {
                ok = true,
                imported = result.Imported,
                duplicates = result.Duplicates,
                failed = result.Failures.Count,
                saved = result.Saved.Select(s => new { id = s.Id, songId = s.SongId, duplicate = s.Duplicate }),
                failures = result.Failures.Select(f => new { id = f.Id, title = f.Title, error = f.Error }),
            });
        });
        // Bulk import queues a metadata scan per song — it shares the queue budget.
        importAll.RequireRateLimiting("convert");

        convert.MapPost("/{id:guid}/save", async (Guid id, SaveDraftRequest request, HttpContext http, ApplicationDbContext db, IBackgroundJobClient jobs) =>
        {
            var draft = await db.ImportDrafts.FindAsync(id);
            // ST-56: saving someone else's draft as a song is the worst cross-user action
            // in this file — ownership first, before anything mutates.
            if (draft is null || !IsOwner(draft, CurrentUserId(http)))
                return Error(404, "not-found", "No conversion with that id.");
            if (draft.Status != "succeeded" || string.IsNullOrWhiteSpace(draft.ChordProText))
                return Error(409, "draft-not-ready",
                    $"This draft is {draft.Status}{(draft.Error is null ? "" : $": {draft.Error}")}. Wait for it to finish, or start a new conversion.");

            try
            {
                // ST-53: the conversion save flow has no album field — the metadata review job fills it in later.
                var (song, duplicate) = await ImportService.CreateSongAsync(
                    db, draft.ChordProText, request.Title, request.Artist,
                    request.Genre, request.Year, null, request.PlaylistId, draft.InputUrl);

                // ST-35 F3: new songs are scanned for missing metadata automatically (background).
                // Duplicates need no scan — the existing song was already reviewed.
                if (!duplicate)
                {
                    var userId = http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
                    jobs.Enqueue<MetadataReviewJob>(job => job.ScanSongAsync(song.Id, userId));
                }

                // ST-49: the draft links to the existing song when it was a duplicate.
                draft.SavedSongId = song.Id;
                await db.SaveChangesAsync();
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

    /// <summary>ST-73: per-draft outcome rows for the Import All response.</summary>
    internal sealed record ImportedDraft(Guid Id, Guid SongId, bool Duplicate);
    internal sealed record FailedDraft(Guid Id, string? Title, string Error);

    /// <summary>
    /// ST-73: the Import All pass — every succeeded, not-yet-imported draft of the user
    /// (oldest first, capped at 50 per pass) becomes a song via the single
    /// <see cref="ImportService.CreateSongAsync"/> chokepoint (title/artist from the
    /// conversion's extracted values, falling back to the ChordPro headers; duplicates
    /// link to the existing song). A draft that fails a validation-class check (ImportException:
    /// empty/broken chart, input limits) is recorded and left in the queue — one bad conversion
    /// never stops the batch. A DB-level fault aborts the pass (same precedent as the per-draft
    /// save); drafts already committed stay linked, so a re-run finishes the rest.
    /// Concurrency: two simultaneous passes for one user (two tabs) can race the duplicate
    /// check and create a copy — accepted risk at self-hosted scale, same window as the
    /// per-draft save, just N drafts wide.
    /// onNewSong: called for each genuinely new song (the endpoint enqueues the metadata scan;
    /// tests pass null, keeping Hangfire out of the unit tests).
    /// </summary>
    internal static async Task<(int Imported, int Duplicates, List<ImportedDraft> Saved, List<FailedDraft> Failures)>
        ImportAllAsync(ApplicationDbContext db, string? userId, Action<Song>? onNewSong = null)
    {
        var drafts = await db.ImportDrafts
            .Where(d => d.UserId == userId && d.Status == "succeeded" && d.SavedSongId == null)
            .OrderBy(d => d.CreatedAt)
            // ST-73: cap at 50 per pass (the queue's page size) — bounds request time;
            // repeated clicks walk the backlog oldest-first. The UI's (N) label counts the
            // newest 50 rows, so with >50 unimported drafts it undercounts (accepted —
            // self-hosted queues stay far below the cap).
            .Take(50)
            .ToListAsync();

        var saved = new List<ImportedDraft>();
        var failures = new List<FailedDraft>();
        int imported = 0, duplicates = 0;

        foreach (var d in drafts)
        {
            try
            {
                var (song, duplicate) = await ImportService.CreateSongAsync(
                    db, d.ChordProText, d.ExtractedTitle, d.ExtractedArtist,
                    null, null, null, null, d.InputUrl);

                if (duplicate) duplicates++;
                else
                {
                    imported++;
                    onNewSong?.Invoke(song);
                }
                d.SavedSongId = song.Id;
                await db.SaveChangesAsync();
                saved.Add(new ImportedDraft(d.Id, song.Id, duplicate));
            }
            catch (ImportException ex)
            {
                failures.Add(new FailedDraft(d.Id, d.ExtractedTitle ?? d.InputPreview, ex.Message));
            }
        }

        return (imported, duplicates, saved, failures);
    }

    /// <summary>
    /// Report (2026-11-12, /queue): "clear all imported" — deletes every imported draft of
    /// the caller (succeeded with a saved song). Imported means spent: the song is in the
    /// library, the draft has no future action. Converted-but-unimported and failed rows
    /// stay (Review/Retry are live on those). Owner-scoped by the same UserId key as the list.
    /// </summary>
    internal static async Task<int> ClearImportedAsync(ApplicationDbContext db, string? userId)
    {
        // Owner-scoped by the same key as IsOwner: a caller without a user id owns nothing.
        if (userId is null)
            return 0;
        var drafts = await db.ImportDrafts
            .Where(d => d.UserId == userId && d.Status == "succeeded" && d.SavedSongId != null)
            .ToListAsync();
        db.ImportDrafts.RemoveRange(drafts);
        await db.SaveChangesAsync();
        return drafts.Count;
    }

    private static SongDetailDto ToDetailDto(Song s)
        => new(s.Id, s.Title, s.Artist, s.Album, s.Year, s.Key, s.Genre, s.Tempo, s.CapoFret, s.CreatedAt, s.UpdatedAt, s.ChordProText, s.SourceUrl);

    private static string? CurrentUserId(HttpContext http)
        => http.User.FindFirstValue("sub") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>
    /// ST-56: drafts are per-user — the list endpoint always filtered by UserId; the id-addressed
    /// routes must use the same key. Rows with a null UserId are nobody's (the list filter drops
    /// them too), so they are not addressable. Callers report 404, not 403, to avoid id probing.
    /// </summary>
    internal static bool IsOwner(ImportDraft draft, string? userId)
        => draft.UserId is not null && draft.UserId == userId;

    /// <summary>Queue-row summary: enough to identify a draft without shipping its full input/output.</summary>
    private static object InputSummary(ImportDraft d, bool matchesLibrary) => new
    {
        id = d.Id,
        status = d.Status,
        inputUrl = d.InputUrl,
        // ST-37 A1: the title found at queue time; older drafts fall back to searching their input.
        inputPreview = d.InputPreview ?? FindTitle(d.InputText) ?? PreviewOf(d.InputText),
        // ST-35 E1: the converted song's name (from its ChordPro header) — preferred over the raw preview.
        extractedTitle = d.ExtractedTitle,
        extractedArtist = d.ExtractedArtist,
        // ST-35 E4: true when title+artist already exist in the library (duplicate signal).
        matchesLibrary,
        error = d.Error,
        createdAt = d.CreatedAt,
        finishedAt = d.FinishedAt,
        savedSongId = d.SavedSongId
    };

    /// <summary>First line of the pasted text (usually title-ish), capped at 80 chars.</summary>
    private static string? PreviewOf(string? inputText)
    {
        if (string.IsNullOrWhiteSpace(inputText))
            return null;
        var t = inputText.Trim();
        var nl = t.IndexOf('\n');
        if (nl > 0)
            t = t[..nl];
        return t.Length > 80 ? t[..80] + "…" : t;
    }

    // ST-37 A1: find a song name in raw input — the HTML <title> first, then a scan of the
    // opening lines for something title-like (skips nav junk like "tab" / "home").
    private static readonly HashSet<string> TitleStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "tab", "tabs", "chord", "chords", "home", "menu", "search", "login", "sign in", "sign up",
        "register", "about", "contact", "help", "faq", "news", "videos", "lessons", "artists",
        "songs", "browse", "popular", "latest", "random", "play", "share", "download", "print",
        "more", "all", "the", "a", "an"
    };

    private static string? FindTitle(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var t = content.Trim();

        // 1. HTML: pull the <title> tag and strip site suffixes (" - Chords & Tabs", …).
        if (t.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                t, "<title[^>]*>(.*?)</title>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success)
            {
                var title = CleanTitle(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(title)) return title;
            }
        }

        // 2. Plain text: the first title-like line in the opening of the paste.
        var lines = t.Split('\n');
        for (var i = 0; i < Math.Min(lines.Length, 40); i++)
        {
            var line = CleanTitle(lines[i]);
            if (IsTitleLike(line)) return line;
        }
        return null;
    }

    /// <summary>Trim a candidate title and strip common site suffixes from &lt;title&gt; tags.</summary>
    private static string CleanTitle(string s)
    {
        s = s.Trim();
        foreach (var sep in new[] { " | ", " - ", " – " })
        {
            var idx = s.LastIndexOf(sep, StringComparison.Ordinal);
            if (idx > 0)
            {
                var tail = s[(idx + sep.Length)..].Trim();
                if (tail.Equals("chords & tabs", StringComparison.OrdinalIgnoreCase)
                    || tail.Equals("chords and tabs", StringComparison.OrdinalIgnoreCase)
                    || tail.Equals("tabs & chords", StringComparison.OrdinalIgnoreCase)
                    || tail.Equals("chords", StringComparison.OrdinalIgnoreCase)
                    || tail.Equals("tabs", StringComparison.OrdinalIgnoreCase))
                {
                    s = s[..idx].Trim();
                }
            }
        }
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    /// <summary>Does this line look like a song title rather than nav junk ("tab", "home", …)?</summary>
    private static bool IsTitleLike(string line)
    {
        if (line.Length < 2 || line.Length > 80) return false;
        if (!line.Any(char.IsLetter)) return false;
        // All-caps nav labels ("TAB", "HOME") are not titles.
        if (line.All(c => !char.IsLetter(c) || char.IsUpper(c))) return false;
        if (line.StartsWith('<') || line.StartsWith("http")) return false;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 10) return false;
        if (words.Length == 1 && TitleStopWords.Contains(line)) return false;
        return true;
    }

    private static IResult Error(int statusCode, string code, string message, Dictionary<string, string?>? fields = null)
        => Results.Json(new { error = new { code, message, fields } }, statusCode: statusCode, options: Json);
}
