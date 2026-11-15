using System.Text.RegularExpressions;
using Hangfire;
using WebApi.Data;
using WebApi.Entities;

namespace WebApi.Services;

/// <summary>
/// ST-14: the chord-page→ChordPro conversion pipeline, one Hangfire job per draft.
///
/// Resolve only serializable arguments at enqueue time (<see cref="RunAsync(Guid)"/> takes the
/// draft id); everything else comes from DI at execution time so Hangfire's job serializer
/// never sees interfaces or delegates:
///
///   input (pasted chord-page text)
///     → LLM (system prompt from docs/prompts/chord-page-to-chordpro.md, one retry on failure)
///     → ChordProParser validation (0 lines / 0 chords ⇒ failed with a readable reason)
///     → draft row: succeeded + ChordProText, or failed + Error
///
/// ST-58: conversions run on a **dedicated single-worker Hangfire queue** ("llm", registered
/// in Program) — that serializes them against the LLM without parking a pool of default
/// workers on a semaphore, and [AutomaticRetry(Attempts=0)] stops Hangfire's 10× re-run storm
/// (the job implements its own single LLM retry). A shutdown mid-conversion returns the draft
/// to "queued" and re-enqueues the job. While the LLM generates, streamed chunks and status
/// transitions are pushed to the requester via <see cref="IImportProgressReporter"/>.
/// </summary>
public sealed class ChordProConversionJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILlmClient _llm;
    private readonly ILlmSettingsProvider _settings;
    private readonly IImportProgressReporter _progress;
    private readonly ILogger<ChordProConversionJob> _logger;
    private readonly IBackgroundJobClient _jobs;

    public ChordProConversionJob(
        IServiceScopeFactory scopes, ILlmClient llm, ILlmSettingsProvider settings,
        IImportProgressReporter progress, ILogger<ChordProConversionJob> logger,
        IBackgroundJobClient jobs)
    {
        _scopes = scopes;
        _llm = llm;
        _settings = settings;
        _progress = progress;
        _logger = logger;
        _jobs = jobs;
    }

    /// <summary>
    /// Hangfire entry point. Never throws into the queue — failures are recorded on the draft.
    /// ST-58: [Queue("llm")] routes conversions to the dedicated single-worker queue (serial
    /// against the GPU, no default workers parked); [AutomaticRetry(Attempts=0)] because the
    /// LLM loop below already retries once — Hangfire's default 10× re-run on top (300 s each,
    /// serialized) is a retry storm whenever anything escapes. `ct` is Hangfire's shutdown token.
    /// </summary>
    [Queue("llm")]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid draftId, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // The draft may have been deleted (cancelled) while we waited for our turn.
            var draft = await db.ImportDrafts.FindAsync(draftId);
            if (draft is null)
                return;

            var startedAt = DateTime.UtcNow;
            int attempt = 1;

            draft.Status = "running";
            draft.Error = null;
            await db.SaveChangesAsync();
            await _progress.ReportStatusAsync(draft.Id, draft.UserId, "running", 0, TimeSpan.Zero, attempt);

            async Task Fail(string reason)
            {
                draft.Status = "failed";
                draft.Error = reason;
                draft.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                await _progress.ReportStatusAsync(draft.Id, draft.UserId, "failed", 0, DateTime.UtcNow - startedAt, attempt);
            }

            // 1. Obtain the input text — pasted chord-page text only (the server fetches
            // no URLs; paste covers every source).
            string input;
            if (!string.IsNullOrWhiteSpace(draft.InputText))
            {
                input = draft.InputText;
            }
            else
            {
                await Fail("The draft has no pasted content.");
                return;
            }

            if (input.Length < 20)
            {
                await Fail("The pasted text is too short to convert — paste the full chord page (lyrics + chord lines).");
                return;
            }

            // 2. The system prompt lives in docs/prompts/chord-page-to-chordpro.md — single source of truth.
            // An explicitly pinned LLM_PROMPT_FILE that does not exist is a misconfiguration: fail fast.
            var pinned = Environment.GetEnvironmentVariable("LLM_PROMPT_FILE");
            if (!string.IsNullOrWhiteSpace(pinned) && !File.Exists(pinned))
            {
                await Fail($"LLM_PROMPT_FILE is set but the file does not exist: {pinned}");
                return;
            }
            var promptFile = ResolvePromptFile(pinned);
            if (promptFile is null)
            {
                await Fail("The chord-page→ChordPro prompt file (docs/prompts/chord-page-to-chordpro.md) was not found on this server.");
                return;
            }
            var systemPrompt = File.ReadAllText(promptFile);

            // 3. Resolve the requester's LLM settings at execution time (ST-30: user → global → .env)
            //    and convert — one retry on failure (spec: retry once on LLM timeout).
            var llmSettings = await _settings.ResolveAsync(draft.UserId);
            if (string.IsNullOrWhiteSpace(llmSettings.BaseUrl))
            {
                await Fail("No LLM is configured for your account. Set one up in Settings → LLM conversion.");
                return;
            }

            string output = string.Empty;
            string lastError = "unknown LLM error";
            var chars = 0;
            for (var a = 1; a <= 2; a++)
            {
                attempt = a;
                try
                {
                    // ST-33: stream the generation and push each chunk to the requester's clients.
                    output = await _llm.ConvertStreamingAsync(llmSettings, systemPrompt, input, onChunk: async chunk =>
                    {
                        chars += chunk.Length;
                        await _progress.ReportChunkAsync(draft.Id, draft.UserId, chunk);
                    }, ct: ct);
                    lastError = null!;
                    break;
                }
                catch (LlmException ex)
                {
                    lastError = ex.Message;
                    chars = 0; // the retry regenerates from scratch — clients reset their preview on attempt=2
                    _logger.LogWarning(ex, "LLM attempt {Attempt} failed for draft {DraftId}", a, draftId);
                    if (a < 2)
                        await _progress.ReportStatusAsync(draft.Id, draft.UserId, "running", 0, DateTime.UtcNow - startedAt, a + 1);
                }
            }
            if (lastError is not null)
            {
                await Fail(lastError);
                return;
            }

            // 4. Normalize LLM quirks (code fences, "=== FILE: x.cho ===" headers,
            //    MuseScore-style section directives) before validating.
            output = SanitizeLlmOutput(output);

            // 5. Validate with the same rules as the ST-13 paste flow.
            var parsed = ChordProParser.Parse(output);
            if (!ImportService.IsChordPro(parsed))
            {
                await Fail("The LLM output could not be parsed as ChordPro (no lyric lines or no chords found). Retry, or convert in a chat UI using the saved prompt.");
                return;
            }

            draft.Status = "succeeded";
            draft.ChordProText = output.Trim();
            // ST-35 E1: remember the extracted name so queue rows can show it without shipping ChordPro text.
            draft.ExtractedTitle = ImportService.Clean(parsed.Header?.Title);
            draft.ExtractedArtist = ImportService.Clean(parsed.Header?.Artist);
            draft.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await _progress.ReportStatusAsync(draft.Id, draft.UserId, "succeeded", output.Trim().Length, DateTime.UtcNow - startedAt, attempt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ST-58: server shutting down mid-conversion (a 300 s LLM call outlives the
            // graceful-shutdown window). Return the draft to "queued" and re-enqueue the job,
            // so the conversion resumes after restart instead of sticking at "running" forever
            // (which used to be undeletable — DELETE answers 409 for running drafts).
            _logger.LogInformation("Conversion {DraftId} interrupted by shutdown — returning to the queue.", draftId);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var draft = await db.ImportDrafts.FindAsync(draftId);
            if (draft is not null)
            {
                draft.Status = "queued";
                draft.Error = null;
                draft.FinishedAt = null;
                await db.SaveChangesAsync();
            }
            // AHC-9: the injected client — the shutdown re-enqueue must not dial JobStorage.Current.
            _jobs.Enqueue<ChordProConversionJob>(job => job.RunAsync(draftId));
        }
    }

    /// <summary>
    /// Locate the prompt file. Candidates, in order: LLM_PROMPT_FILE env var,
    /// {baseDir}/prompts/chord-page-to-chordpro.md (container layout), then a walk up from the
    /// base directory looking for the repo layout {dir}/docs/prompts/chord-page-to-chordpro.md (dev).
    /// </summary>
    public static string? ResolvePromptFile(string? envOverride = null, string? baseDirectory = null)
    {
        var baseDir = baseDirectory ?? AppContext.BaseDirectory;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(envOverride))
            candidates.Add(envOverride);
        candidates.Add(Path.Combine(baseDir, "prompts", "chord-page-to-chordpro.md"));
        candidates.Add(Path.Combine(baseDir, "docs", "prompts", "chord-page-to-chordpro.md"));
        for (var dir = Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar));
             dir is not null && dir != Path.GetPathRoot(dir);
             dir = Path.GetDirectoryName(dir))
        {
            candidates.Add(Path.Combine(dir, "docs", "prompts", "chord-page-to-chordpro.md"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Normalizes common LLM output quirks so the saved song is clean ChordPro:
    /// markdown code fences, "=== FILE: x.cho ===" banner lines, and MuseScore-style
    /// {start_of_verse}/{end_of_verse} directives (start_of_* with a label becomes the
    /// section label; bare/end variants are dropped).
    /// </summary>
    public static string SanitizeLlmOutput(string text)
    {
        var s = text.Trim();

        // Markdown code fence: keep the content of the first fenced block.
        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var contentStart = s.IndexOf('\n', fence);
            var contentEnd = contentStart >= 0 ? s.IndexOf("```", contentStart) : -1;
            if (contentStart >= 0 && contentEnd > contentStart)
                s = s.Substring(contentStart + 1, contentEnd - contentStart - 1).Trim();
        }

        // Banner lines like "=== FILE: ode-to-joy.cho ==="
        s = Regex.Replace(s, @"(?m)^[ \t]*===.*?===\s*$", "");

        // MuseScore-style section markers.
        s = Regex.Replace(s, "(?im)^[ \t]*\\{start_of_\\w+\\s+label=\"([^\"]*)\"\\}[ \t]*$", "$1");
        s = Regex.Replace(s, "(?im)^[ \t]*\\{start_of_\\w+[^}]*\\}[ \t]*$", "");
        s = Regex.Replace(s, "(?im)^[ \t]*\\{end_of_\\w+[^}]*\\}[ \t]*$", "");

        s = Regex.Replace(s, "\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>
    /// Basic HTML → text, kept for the paste path only: a pasted page can carry markup,
    /// so drop script/style/noscript, turn block-level tags into
    /// newlines, decode the common entities, collapse blank runs. Not a full parser —
    /// chord pages are plain enough that this is stable and dependency-free.
    /// </summary>
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, "(?is)<(script|style|noscript|svg|head)[^>]*>.*?</\\1>", " ");
        s = Regex.Replace(s, "(?i)<br\\s*/?>", "\n");
        s = Regex.Replace(s, "(?i)</(p|div|li|tr|h[1-6]|td|section|article)>", "\n");
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = s
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'");
        s = Regex.Replace(s, "[ \t\r\f\v]+", " ");
        s = Regex.Replace(s, " *\n", "\n"); // no spaces before line ends
        s = Regex.Replace(s, "\n +", "\n"); // no leading spaces on lines (tags became spaces)
        s = Regex.Replace(s, "\n{3,}", "\n\n"); // collapse blank runs to one blank line
        return s.Trim();
    }
}
