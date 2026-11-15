using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using WebApi.Data;
using WebApi.Entities;

namespace WebApi.Services;

/// <summary>
/// ST-35 F3: metadata auto-review. A background job works through the library (or a single
/// song), asks the LLM which derivable metadata is missing or wrong, and stores the answers
/// as pending proposals on <see cref="MetadataProposal"/> — the user accepts/rejects them from
/// the songs list. Proposals for fields that are currently empty are applied directly; only
/// changes to existing values go through review (ST-51 #11).
/// ST-58: scans run on the dedicated single-worker "llm" Hangfire queue (serializes them
/// against the LLM without parking default workers on a semaphore); [AutomaticRetry(Attempts=0)]
/// — a systematic LLM quirk must not trigger Hangfire's 10× whole-library re-scan.
/// New songs are scanned automatically after import.
/// </summary>
public sealed class MetadataReviewJob
{
    // ST-53: album is reviewable like the other text fields (MusicBrainz's release title is the album).
    /// <summary>
    /// ST-69: shared with the endpoints — a category accept validates the field against this list.
    /// ST-106: "key" is REMOVED — the key is derived from the chart's chords (KeyAnalyzer);
    /// online metadata may not write it.
    /// </summary>
    internal static readonly string[] ValidFields = { "title", "artist", "album", "genre", "year", "tempo" };

    private readonly IServiceScopeFactory _scopes;
    private readonly ILlmClient _llm;
    private readonly ILlmSettingsProvider _settings;
    private readonly ILogger<MetadataReviewJob> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public MetadataReviewJob(
        IServiceScopeFactory scopes, ILlmClient llm, ILlmSettingsProvider settings,
        ILogger<MetadataReviewJob> logger, IHostApplicationLifetime lifetime)
    {
        _scopes = scopes;
        _llm = llm;
        _settings = settings;
        _logger = logger;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Hangfire entry point: scan the whole library. Never throws into the queue.
    /// ST-74: these job method SIGNATURES are part of the serialized job (type name + parameter
    /// types are stored in the queue) — changing a signature strands every queued job with
    /// "target method was not found" and Hangfire's default 10× retry storm. Evolve parameters
    /// by OVERLOAD, never by editing the parameter list of an entry point.
    /// </summary>
    [Queue("llm")] // ST-58: dedicated single-worker LLM queue (see Program)
    [AutomaticRetry(Attempts = 0)] // ST-58: the per-song loop is the retry unit; 10× whole-library rescans are a storm.
    public Task ScanLibraryAsync(string? userId = null, CancellationToken ct = default) => ScanSongsAsync(null, userId, ct);

    /// <summary>Hangfire entry point: scan a single song (enqueued after each import).</summary>
    [Queue("llm")]
    [AutomaticRetry(Attempts = 0)]
    public Task ScanSongAsync(Guid songId, string? userId = null, CancellationToken ct = default) => ScanSongsAsync(songId, userId, ct);

    private async Task ScanSongsAsync(Guid? songId, string? userId, CancellationToken callerCt)
    {
        // ST-74: Hangfire 1.8 never cancels the job token on server shutdown (ShutdownTimeout
        // just force-stops the server, leaving the job "Processing" — which the watchdog re-queues
        // 30 min later, restarting the whole scan: the loop). The job watches the host's own
        // stop signal: SIGTERM becomes a clean cancellation handled below.
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(callerCt, _lifetime.ApplicationStopping);
        var ct = shutdown.Token;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // ST-58 coalescing sentinel: clear the "scan queued" flag as the scan starts (set by
        // POST /metadata-review) — K rapid clicks enqueue ONE scan, later clicks can queue the
        // next one only once this has begun.
        var flag = await db.AppMeta.FindAsync("scan-pending");
        if (flag is not null)
        {
            db.AppMeta.Remove(flag);
            await db.SaveChangesAsync();
        }

        // Band-wide resolution: triggerer's row → global → any member's row (.env last in the merge),
        // so a scan works even when only one member has configured the LLM.
        var llmSettings = await _settings.ResolveForJobAsync(userId);
        if (string.IsNullOrWhiteSpace(llmSettings.BaseUrl))
        {
            _logger.LogInformation("Metadata review skipped — no LLM is configured for any user.");
            return;
        }

        var promptFile = ResolvePromptFile();
        if (promptFile is null)
        {
            _logger.LogWarning("Metadata review skipped — docs/prompts/metadata-review.md was not found on this server.");
            return;
        }
        var systemPrompt = File.ReadAllText(promptFile);

        // ST-68: full-library scans report progress (one row, updated per song) so the Songs
        // page can show what the scan is doing, how far along, and a coarse ETA. Single-song
        // scans (post-import) are instant at UI scale — they don't touch the row.
        MetadataScanProgress? progress = null;
        if (songId is null)
        {
            progress = await db.MetadataScanProgresses.FindAsync(new object[] { "library" }, ct);
            if (progress is null)
            {
                progress = new MetadataScanProgress { Key = "library" };
                db.MetadataScanProgresses.Add(progress);
            }
            progress.Status = "running";
            progress.CurrentTitle = null;
            progress.Index = 0;
            progress.Total = await db.Songs.CountAsync(ct);
            progress.StartedAt = DateTime.UtcNow;
            progress.FinishedAt = null;
            progress.ProposalsAdded = 0;
            await db.SaveChangesAsync(ct);
        }

        // ST-58: page through the library (stable order) — materializing every Song with its
        // full ChordProText tracked in one query ballooned memory + the change tracker over a
        // multi-hour scan.
        const int PageSize = 25;
        try
        {
            for (var page = 0; ; page++)
            {
                var songs = songId is { } id
                    ? await db.Songs.AsNoTracking().Where(s => s.Id == id).ToListAsync(ct)
                    : await db.Songs.AsNoTracking()
                        .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
                        .Skip(page * PageSize).Take(PageSize)
                        .ToListAsync(ct);
                if (songs.Count == 0)
                    break;

                foreach (var song in songs)
                {
                    ct.ThrowIfCancellationRequested();

                    // ST-68: publish the song the scan is about to work on (one UPDATE per song —
                    // trivial against the LLM call it precedes).
                    if (progress is not null)
                    {
                        progress.CurrentTitle = song.Title;
                        progress.Index += 1;
                        await db.SaveChangesAsync(ct);
                    }

                    try
                    {
                        var added = await ScanOneAsync(db, llmSettings, systemPrompt, song, ct);
                        if (added > 0)
                        {
                            _logger.LogInformation("Metadata review: {Count} proposal(s) for “{Title}”", added, song.Title);
                            if (progress is not null)
                                progress.ProposalsAdded += added; // persisted by the next per-song update / the finish write
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                    {
                        // One bad song must not stop the rest of the library.
                        _logger.LogWarning(ex, "Metadata review failed for song {SongId}", song.Id);
                    }
                }

                // ST-68 (bug found by the ST-68 tests): the single-song branch ignores the page,
                // so it returns the same song on EVERY iteration — the loop only exits on an
                // empty page, so post-import scans spun forever (each spin = one full LLM call
                // for the same song), permanently pinning the serial "llm" queue and starving
                // every library scan behind it. One song, one page: done.
                if (songId is not null)
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ST-74: server shutdown (SIGTERM) cancels the token. Mark the row interrupted and
            // RETURN NORMALLY: Hangfire records the job as succeeded, so the shutdown reaper /
            // watchdog never re-queues it — a redeploy no longer auto-restarts the scan from
            // song 1 (the UI's "interrupted — start it again to resume" toast is the recovery).
            // A throwing job here = the strip-fill loop James watched across today's deploys.
            if (progress is not null)
            {
                progress.Status = "interrupted";
                progress.CurrentTitle = null;
                progress.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            _logger.LogInformation(
                "Metadata scan cancelled at {Index}/{Total} (server shutdown) — marked interrupted, not resuming.",
                progress?.Index ?? 0, progress?.Total ?? 0);
            return;
        }

        if (progress is not null)
        {
            progress.Status = "done";
            progress.CurrentTitle = null;
            progress.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<int> ScanOneAsync(
        ApplicationDbContext db, LlmEffectiveSettings llmSettings, string systemPrompt, Song song, CancellationToken ct)
    {
        // A pending round already exists — don't stack duplicate proposals on top of it.
        if (await db.MetadataProposals.AnyAsync(p => p.SongId == song.Id && p.Status == "pending", ct))
            return 0;

        var output = await _llm.ConvertAsync(llmSettings, systemPrompt, BuildUserContent(song), ct);
        var proposals = ParseProposals(output);
        if (proposals.Count == 0)
            return 0;

        // ST-51 #11: proposals for empty fields are applied directly — review is only needed
        // when an existing value would change. The list song was loaded AsNoTracking, so get a
        // tracked copy before mutating it (lazily, only if there is something to apply).
        Song? live = null;
        var added = 0;
        var autoApplied = 0;
        foreach (var (field, value) in proposals)
        {
            var current = CurrentValueOf(song, field);

            // Skip fields the song already has this exact value for.
            if (string.Equals(current?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(current))
            {
                live ??= await db.Songs.FirstOrDefaultAsync(s => s.Id == song.Id);
                if (live is not null && TryApply(live, field, value))
                    autoApplied++;
                continue;
            }

            db.MetadataProposals.Add(new MetadataProposal
            {
                Id = Guid.NewGuid(),
                SongId = song.Id,
                Field = field,
                CurrentValue = current,
                ProposedValue = value,
                Status = "pending",
                CreatedAt = DateTime.UtcNow
            });
            added++;
        }

        if (added > 0 || autoApplied > 0)
            await db.SaveChangesAsync();
        return added + autoApplied;
    }

    /// <summary>The chart + current metadata, formatted for the prompt.</summary>
    private static string BuildUserContent(Song song)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Current metadata:");
        sb.AppendLine($"Title: {song.Title}");
        sb.AppendLine($"Artist: {(string.IsNullOrWhiteSpace(song.Artist) ? "(none)" : song.Artist)}");
        // ST-53: the LLM needs to see the current album value to propose a fix for it.
        sb.AppendLine($"Album: {(string.IsNullOrWhiteSpace(song.Album) ? "(none)" : song.Album)}");
        sb.AppendLine($"Genre: {(string.IsNullOrWhiteSpace(song.Genre) ? "(none)" : song.Genre)}");
        sb.AppendLine($"Year: {(song.Year is null ? "(none)" : song.Year.Value.ToString())}");
        sb.AppendLine($"Key: {(string.IsNullOrWhiteSpace(song.Key) ? "(none)" : song.Key)}");
        sb.AppendLine($"Tempo: {(song.Tempo is null ? "(none)" : $"{song.Tempo} BPM")}");
        sb.AppendLine();
        sb.AppendLine("Chart (ChordPro):");
        sb.AppendLine(song.ChordProText);
        return sb.ToString();
    }

    /// <summary>
    /// Lenient parse of the LLM's JSON answer: strips code fences, finds the first object,
    /// and keeps only entries with a valid field + plausible value. Never throws.
    /// </summary>
    internal static List<(string Field, string Value)> ParseProposals(string output)
    {
        var result = new List<(string, string)>();

        var s = output.Trim();
        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = s.IndexOf('\n', fence);
            var end = start >= 0 ? s.IndexOf("```", start) : -1;
            if (start >= 0 && end > start)
                s = s[(start + 1)..end].Trim();
        }

        var firstBrace = s.IndexOf('{');
        var lastBrace = s.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
            return result;

        try
        {
            using var doc = JsonDocument.Parse(s[firstBrace..(lastBrace + 1)]);
            if (!doc.RootElement.TryGetProperty("proposals", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return result;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                // ST-58: ValueKind check before GetString() — the LLM sometimes answers with
                // nested objects/arrays ("value": {"genre": "rock"}); GetString() throws
                // InvalidOperationException on those, which used to escape the JsonException-only
                // catch and fail the job into Hangfire's retry storm. Such entries are dropped.
                var field = item.TryGetProperty("field", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String
                    ? f.GetString()?.Trim().ToLowerInvariant() : null;
                var value = item.TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                    ? v.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value))
                    continue;
                if (!ValidFields.Contains(field!))
                    continue;

                var normalized = NormalizeValue(field!, value!);
                if (normalized is not null)
                    result.Add((field!, normalized));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Unparseable answer → no proposals (the caller logs nothing; this is a normal LLM miss).
            _ = ex;
        }

        return result;
    }

    /// <summary>Validates + canonicalizes a proposed value per field; null when implausible.</summary>
    internal static string? NormalizeValue(string field, string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0 || v.Length > 200)
            return null;

        return field switch
        {
            "year" => int.TryParse(v, out var y) && y is > 1000 and < 2100 ? y.ToString() : null,
            "tempo" => int.TryParse(v, out var t) && t is >= 30 and <= 300 ? t.ToString() : null,
            "genre" => ConsolidateGenre(v),
            _ => v
        };
    }

    /// <summary>
    /// ST-72 (in-app report 2026-11-01 15:50): church music labels consolidate to one
    /// generic "worship" genre. The prompt already steers this, but the LLM keeps proposing
    /// "gospel" / "hymn" / "spiritual" variants, which scatter the library's biggest genre
    /// across a dozen review categories. Code is the source of truth: any family label
    /// becomes "worship"; anything else passes through untouched.
    /// </summary>
    internal static readonly string[] WorshipFamilyLabels =
    {
        "worship", "gospel", "gospels", "hymn", "hymns", "spiritual", "spirituals",
        "christian", "ccm", "contemporary christian", "contemporary christian music",
        "praise", "praise and worship", "praise & worship", "christian worship", "gospel worship"
    };

    internal static string ConsolidateGenre(string v) =>
        WorshipFamilyLabels.Contains(v.Trim().ToLowerInvariant()) ? "worship" : v;

    /// <summary>The song's current value for a field (null when unset).</summary>
    internal static string? CurrentValueOf(Song song, string field) => field switch
    {
        "title" => song.Title,
        "artist" => song.Artist,
        // ST-53: album is reviewable like the other text fields.
        "album" => song.Album,
        "genre" => song.Genre,
        "key" => song.Key,
        "year" => song.Year?.ToString(),
        "tempo" => song.Tempo?.ToString(),
        _ => null
    };

    /// <summary>Applies a proposed value to the song; false when the field/value is invalid.</summary>
    internal static bool TryApply(Song song, string field, string rawValue)
    {
        switch (field)
        {
            case "title":
                if (string.IsNullOrWhiteSpace(rawValue))
                    return false;
                song.Title = rawValue.Trim();
                break;
            case "artist":
                song.Artist = ImportService.Clean(rawValue);
                break;
            case "genre":
                // ST-72: consolidate on apply too — proposals made before the mapping (and any
                // direct-apply path) land in the same single "worship" bucket.
                song.Genre = ImportService.Clean(ConsolidateGenre(rawValue));
                break;
            // ST-53: album is reviewable like the other text fields.
            case "album":
                song.Album = ImportService.Clean(rawValue);
                break;
            case "key":
                // ST-106: Key is derived from the chart's chords — a legacy pending field=key
                // proposal is not applicable (creation of key suggestions is also gone, see
                // ValidFields). Applying false rejects it at accept time.
                return false;
            case "year":
                if (!int.TryParse(rawValue, out var y) || y is <= 1000 or >= 2100)
                    return false;
                song.Year = y;
                break;
            case "tempo":
                if (!int.TryParse(rawValue, out var t) || t is < 30 or > 300)
                    return false;
                song.Tempo = t;
                break;
            default:
                return false;
        }

        song.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Locate the prompt file. Candidates: {baseDir}/prompts/metadata-review.md (container),
    /// then a walk up from the base directory for the repo layout docs/prompts/metadata-review.md (dev).
    /// </summary>
    private static string? ResolvePromptFile()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "prompts", "metadata-review.md"),
            Path.Combine(baseDir, "docs", "prompts", "metadata-review.md")
        };
        for (var dir = Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar));
             dir is not null && dir != Path.GetPathRoot(dir);
             dir = Path.GetDirectoryName(dir))
        {
            candidates.Add(Path.Combine(dir, "docs", "prompts", "metadata-review.md"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
