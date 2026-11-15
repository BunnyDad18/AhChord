using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WebApi.Data;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Tests;

public class ChordProConversionJobTests
{
    private const string ValidChordPro = """
        {title: Test Song}
        {artist: Test Artist}
        {key: G}

        VERSE
        [G]Test lyric line one
        [Em]Test lyric line two
        [C]Final line with chords
        """;

    private sealed class StubLlm : ILlmClient
    {
        private readonly Func<string, string, Task<string>> _converter;
        public int Calls { get; private set; }
        public LlmEffectiveSettings? LastSettings { get; private set; }

        public StubLlm(Func<string, string, Task<string>> converter) => _converter = converter;

        public Task<string> ConvertAsync(LlmEffectiveSettings settings, string systemPrompt, string userContent, CancellationToken ct = default)
            => ConvertStreamingAsync(settings, systemPrompt, userContent);

        // ST-33: the job uses the streaming path — deliver the whole result as one chunk so
        // tests can assert chunk forwarding.
        public async Task<string> ConvertStreamingAsync(
            LlmEffectiveSettings settings, string systemPrompt, string userContent,
            Func<string, Task>? onChunk = null, CancellationToken ct = default)
        {
            Calls++;
            LastSettings = settings;
            var result = await _converter(systemPrompt, userContent);
            if (onChunk is not null && result.Length > 0)
                await onChunk(result);
            return result;
        }
    }

    /// <summary>ST-33: records the progress events the job pushes, in order.</summary>
    private sealed class RecordingProgress : IImportProgressReporter
    {
        public readonly List<(string Kind, Guid DraftId, string? Status, int Chars, int Attempt)> Events = new();

        public Task ReportStatusAsync(Guid draftId, string? userId, string status, int chars, TimeSpan elapsed, int attempt)
        {
            lock (Events) { Events.Add(("status", draftId, status, chars, attempt)); }
            return Task.CompletedTask;
        }

        public Task ReportChunkAsync(Guid draftId, string? userId, string text)
        {
            lock (Events) { Events.Add(("chunk", draftId, null, text.Length, 0)); }
            return Task.CompletedTask;
        }
    }

    private static (ApplicationDbContext db, ChordProConversionJob job, StubLlm llm, RecordingProgress progress) NewFixture(
        Func<string, string, Task<string>>? llmConverter = null,
        StubLlm? existingLlm = null,
        string? llmBaseUrl = "http://llm.test/v1")
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);

        // ST-30: the job resolves LLM settings at execution time (user → global → .env) —
        // seed a global row so the fixture is "configured" unless llmBaseUrl is null.
        if (llmBaseUrl is not null)
            db.LlmSettings.Add(new LlmSettings { UserId = null, BaseUrl = llmBaseUrl, Model = "test-model" });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var llm = existingLlm ?? new StubLlm(llmConverter ?? ((_, _) => Task.FromResult(ValidChordPro)));
        var progress = new RecordingProgress();
        var services = new ServiceCollection();
        services.AddSingleton(db);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var job = new ChordProConversionJob(scopeFactory, llm, new LlmSettingsProvider(db, config), progress, NullLogger<ChordProConversionJob>.Instance, new StubJobClient());
        return (db, job, llm, progress);
    }

    /// <summary>AHC-9: the job takes an injected IBackgroundJobClient (shutdown re-enqueue).
    /// The fixture records the enqueues; no storage, no server.</summary>
    private sealed class StubJobClient : IBackgroundJobClient
    {
        public List<Job> Enqueued { get; } = [];
        public string Create(Job job, IState initialState)
        {
            Enqueued.Add(job);
            return "stub-" + Enqueued.Count;
        }
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private static ImportDraft NewDraft(string? content = null) => new()
    {
        Id = Guid.NewGuid(),
        Status = "queued",
        InputText = content ?? "This is a chord page with [G]chords and enough length to pass the guard."
    };

    private static void WriteTempPrompt(string path) => File.WriteAllText(path, "You are a chord tab conversion specialist.\n");

    [Fact]
    public async Task RunAsync_ValidLlmOutput_DraftSucceedsWithChordPro()
    {
        var (db, job, llm, _) = NewFixture();
        var promptFile = Path.Combine(Path.GetTempPath(), $"sfm-prompt-{Guid.NewGuid():N}.md");
        WriteTempPrompt(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", promptFile);
        var draft = NewDraft();
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        await job.RunAsync(draft.Id);

        var saved = await db.ImportDrafts.SingleAsync(d => d.Id == draft.Id);
        Assert.Equal("succeeded", saved.Status);
        Assert.Equal(ValidChordPro.Trim(), saved.ChordProText);
        Assert.Null(saved.Error);
        Assert.NotNull(saved.FinishedAt);
        Assert.Equal(1, llm.Calls);
        File.Delete(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", null);
    }

    [Fact]
    public async Task RunAsync_LlmFailsOnce_ThenSucceeds_RetriesOnce()
    {
        var attempts = 0;
        var llm = new StubLlm((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new LlmException("transient timeout");
            return Task.FromResult(ValidChordPro);
        });
        var (db, job, _, _) = NewFixture(existingLlm: llm);
        var promptFile = Path.Combine(Path.GetTempPath(), $"sfm-prompt-{Guid.NewGuid():N}.md");
        WriteTempPrompt(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", promptFile);
        var draft = NewDraft();
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        await job.RunAsync(draft.Id);

        var saved = await db.ImportDrafts.SingleAsync(d => d.Id == draft.Id);
        Assert.Equal("succeeded", saved.Status);
        Assert.Equal(2, llm.Calls);
        File.Delete(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", null);
    }

    [Fact]
    public async Task RunAsync_LlmAlwaysFails_DraftFailsWithReadableError()
    {
        var (db, job, llm, _) = NewFixture((_, _) => throw new LlmException("LLM returned 500: model exploded"));
        var promptFile = Path.Combine(Path.GetTempPath(), $"sfm-prompt-{Guid.NewGuid():N}.md");
        WriteTempPrompt(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", promptFile);
        var draft = NewDraft();
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        await job.RunAsync(draft.Id);

        var saved = await db.ImportDrafts.SingleAsync(d => d.Id == draft.Id);
        Assert.Equal("failed", saved.Status);
        Assert.Contains("model exploded", saved.Error);
        Assert.Null(saved.ChordProText);
        Assert.Equal(2, llm.Calls); // one retry
        File.Delete(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", null);
    }

    [Fact]
    public async Task RunAsync_GarbageLlmOutput_DraftFailsWithParserReason()
    {
        var (db, job, _, _) = NewFixture((_, _) => Task.FromResult("Sorry, I cannot do that."));
        var promptFile = Path.Combine(Path.GetTempPath(), $"sfm-prompt-{Guid.NewGuid():N}.md");
        WriteTempPrompt(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", promptFile);
        var draft = NewDraft();
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        await job.RunAsync(draft.Id);

        var saved = await db.ImportDrafts.SingleAsync(d => d.Id == draft.Id);
        Assert.Equal("failed", saved.Status);
        Assert.Contains("ChordPro", saved.Error);
        File.Delete(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", null);
    }

    [Fact]
    public async Task RunAsync_MissingPromptFile_DraftFailsWithPromptError()
    {
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", Path.Combine(Path.GetTempPath(), "does-not-exist-42.md"));
        var (db, job, llm, _) = NewFixture();
        var draft = NewDraft();
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        await job.RunAsync(draft.Id);

        var saved = await db.ImportDrafts.SingleAsync(d => d.Id == draft.Id);
        Assert.Equal("failed", saved.Status);
        Assert.Contains("LLM_PROMPT_FILE", saved.Error);
        Assert.Equal(0, llm.Calls); // never reaches the LLM
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", null);
    }

    [Fact]
    public async Task RunAsync_NoInputAtAll_DraftFails()
    {
        var (db, job, llm, _) = NewFixture();
        var draft = new ImportDraft { Id = Guid.NewGuid(), Status = "queued" }; // no content
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        await job.RunAsync(draft.Id);

        var saved = await db.ImportDrafts.SingleAsync(d => d.Id == draft.Id);
        Assert.Equal("failed", saved.Status);
        Assert.Contains("no pasted content", saved.Error);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task RunAsync_TooShortInput_DraftFails()
    {
        var (db, job, llm, _) = NewFixture();
        var draft = NewDraft(content: "short");
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        await job.RunAsync(draft.Id);

        var saved = await db.ImportDrafts.SingleAsync(d => d.Id == draft.Id);
        Assert.Equal("failed", saved.Status);
        Assert.Contains("too short", saved.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, llm.Calls);
    }

    // --- EmptyInputReason (JS-rendered pages) -----------------------------------
    // (removed with the URL-fetch path — conversion input is pasted text only)

    [Fact]
    public async Task RunAsync_NoLlmConfigured_DraftFailsWithSettingsPointer()
    {
        var (db, job, llm, _) = NewFixture(llmBaseUrl: null); // no global row, no env values
        var draft = NewDraft();
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        await job.RunAsync(draft.Id);

        var saved = await db.ImportDrafts.SingleAsync(d => d.Id == draft.Id);
        Assert.Equal("failed", saved.Status);
        Assert.Contains("Settings → LLM conversion", saved.Error);
        Assert.Equal(0, llm.Calls); // never reaches the LLM
    }

    [Fact]
    public async Task RunAsync_UsesRequestersOwnSettings_NotTheGlobalRow()
    {
        var (db, job, llm, _) = NewFixture(); // global row: http://llm.test/v1
        db.LlmSettings.Add(new LlmSettings { UserId = "u1", BaseUrl = "http://mine.test/v1" });
        var draft = NewDraft();
        draft.UserId = "u1";
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        await job.RunAsync(draft.Id);

        Assert.Equal("succeeded", (await db.ImportDrafts.SingleAsync(d => d.Id == draft.Id)).Status);
        Assert.Equal("http://mine.test/v1", llm.LastSettings!.BaseUrl);
    }

    // --- ST-33: live progress + serial queue -------------------------------------

    [Fact]
    public async Task RunAsync_PushesProgressEvents_RunningChunksThenSucceeded()
    {
        var (db, job, _, progress) = NewFixture();
        var promptFile = Path.Combine(Path.GetTempPath(), $"sfm-prompt-{Guid.NewGuid():N}.md");
        WriteTempPrompt(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", promptFile);
        var draft = NewDraft();
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        try
        {
            await job.RunAsync(draft.Id);

            var statuses = progress.Events.Where(e => e.Kind == "status").Select(e => e.Status).ToList();
            Assert.Equal(new[] { "running", "succeeded" }, statuses);
            Assert.Contains(progress.Events, e => e.Kind == "chunk"); // streamed output was forwarded
        }
        finally
        {
            File.Delete(promptFile);
            Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", null);
        }
    }

    [Fact]
    public async Task RunAsync_Retry_SendsAttemptTwoProgress()
    {
        var attempts = 0;
        var llm = new StubLlm((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new LlmException("transient timeout");
            return Task.FromResult(ValidChordPro);
        });
        var (db, job, _, progress) = NewFixture(existingLlm: llm);
        var promptFile = Path.Combine(Path.GetTempPath(), $"sfm-prompt-{Guid.NewGuid():N}.md");
        WriteTempPrompt(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", promptFile);
        var draft = NewDraft();
        db.ImportDrafts.Add(draft);
        await db.SaveChangesAsync();

        try
        {
            await job.RunAsync(draft.Id);

            // Clients use the attempt number to reset their preview buffer on retry.
            var runningAttempts = progress.Events
                .Where(e => e.Kind == "status" && e.Status == "running")
                .Select(e => e.Attempt)
                .ToList();
            Assert.Equal(new[] { 1, 2 }, runningAttempts);
        }
        finally
        {
            File.Delete(promptFile);
            Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", null);
        }
    }

    // ST-58: serialization moved from the in-code semaphore to Hangfire's dedicated
    // single-worker "llm" queue, so RunAsync no longer queues itself — verified live
    // (job rows land in queue "llm", one worker). What these tests cover now:
    // concurrent runs are safe, and a vanished draft exits silently.

    [Fact]
    public async Task RunAsync_TwoConcurrentDrafts_BothSucceed()
    {
        // ST-58: the static semaphore is gone — RunAsync carries no process-wide state, so
        // two runs (each with its own scope, as Hangfire hands out) complete independently.
        var llm = new StubLlm(async (_, _) =>
        {
            await Task.Delay(150);
            return ValidChordPro;
        });
        var promptFile = Path.Combine(Path.GetTempPath(), $"sfm-prompt-{Guid.NewGuid():N}.md");
        WriteTempPrompt(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", promptFile);

        var (db1, job1, _, _) = NewFixture(existingLlm: llm);
        var (db2, job2, _, _) = NewFixture(existingLlm: llm);
        var d1 = NewDraft();
        var d2 = NewDraft();
        db1.ImportDrafts.Add(d1);
        db2.ImportDrafts.Add(d2);
        await Task.WhenAll(db1.SaveChangesAsync(), db2.SaveChangesAsync());

        try
        {
            await Task.WhenAll(job1.RunAsync(d1.Id), job2.RunAsync(d2.Id));

            Assert.Equal("succeeded", (await db1.ImportDrafts.SingleAsync(d => d.Id == d1.Id)).Status);
            Assert.Equal("succeeded", (await db2.ImportDrafts.SingleAsync(d => d.Id == d2.Id)).Status);
        }
        finally
        {
            File.Delete(promptFile);
            Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", null);
        }
    }

    [Fact]
    public async Task RunAsync_DraftRowAlreadyGone_ExitsSilently()
    {
        // A queued draft cancelled via DELETE is gone by the time its Hangfire job runs —
        // the job must find no row and exit without throwing (ST-33 contract, kept).
        var llm = new StubLlm((_, _) => Task.FromResult(ValidChordPro));
        var (db, job, _, _) = NewFixture(existingLlm: llm);
        var promptFile = Path.Combine(Path.GetTempPath(), $"sfm-prompt-{Guid.NewGuid():N}.md");
        WriteTempPrompt(promptFile);
        Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", promptFile);
        var d = NewDraft();
        db.ImportDrafts.Add(d);
        await db.SaveChangesAsync();
        db.ImportDrafts.Remove(d);
        await db.SaveChangesAsync();

        try
        {
            await job.RunAsync(d.Id); // must not throw; nothing left to assert on
        }
        finally
        {
            File.Delete(promptFile);
            Environment.SetEnvironmentVariable("LLM_PROMPT_FILE", null);
        }
    }

    // --- HtmlToText -------------------------------------------------------------

    [Fact]
    public void HtmlToText_StripsTagsScriptsAndEntities()
    {
        var html = """
            <html><head><style>.x{color:red}</style><script>var x=1;</script></head>
            <body><div><p>[G]Strum a [D]G, [G]then you play an A</p>
            <div class="tabs">[G] 3 2 0 0 0 0</div>
            <span>&amp; &lt;tags&gt; &quot;quoted&quot; &#39;single&#39;</span>
            <br/>after break</div></body></html>
            """;
        var text = ChordProConversionJob.HtmlToText(html);

        Assert.DoesNotContain("var x=1", text);
        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("div", text);
        Assert.Contains("[G]Strum a [D]G, [G]then you play an A", text);
        Assert.Contains("& <tags> \"quoted\" 'single'", text);
    }

    [Fact]
    public void HtmlToText_CollapsesBlankRuns()
    {
        var text = ChordProConversionJob.HtmlToText("<p>a</p>\n\n\n\n<p>b</p>");
        Assert.Equal("a\n\nb", text);
    }

    // --- SanitizeLlmOutput -------------------------------------------------------

    [Fact]
    public void SanitizeLlmOutput_StripsCodeFenceAndBanner()
    {
        var llmOutput = """
            === FILE: ode-to-joy.cho ===
            ```
            {title: Ode to Joy}
            {artist: Beethoven}

            [G]Ode to joy, lovely star
            ```
            Hope you enjoyed it!
            """;
        var cleaned = ChordProConversionJob.SanitizeLlmOutput(llmOutput);

        Assert.DoesNotContain("```", cleaned);
        Assert.DoesNotContain("=== FILE", cleaned);
        Assert.DoesNotContain("Hope you enjoyed", cleaned);
        Assert.Contains("{title: Ode to Joy}", cleaned);
        Assert.Contains("[G]Ode to joy, lovely star", cleaned);
    }

    [Fact]
    public void SanitizeLlmOutput_MapsMuseScoreSectionMarkers()
    {
        var llmOutput = """
            [G]Verse line one
            {start_of_verse label="Verse 1"}
            [C]Verse line two
            {end_of_verse}
            {start_of_chorus label="Chorus"}
            [G]Chorus line
            {end_of_chorus}
            """;
        var cleaned = ChordProConversionJob.SanitizeLlmOutput(llmOutput);

        Assert.Contains("Verse 1", cleaned);
        Assert.Contains("Chorus", cleaned);
        Assert.DoesNotContain("start_of", cleaned);
        Assert.DoesNotContain("end_of", cleaned);
    }

    [Fact]
    public void SanitizeLlmOutput_PassesCleanChordProThroughUnchanged()
    {
        var clean = """
            {title: Clean Song}

            VERSE
            [G]Line one
            [C]Line two
            """;
        Assert.Equal(clean, ChordProConversionJob.SanitizeLlmOutput(clean));
    }

    // --- prompt resolution -------------------------------------------------------

    [Fact]
    public void ResolvePromptFile_FindsRepoLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sfm-repo-{Guid.NewGuid():N}");
        var prompts = Path.Combine(root, "docs", "prompts");
        Directory.CreateDirectory(prompts);
        var file = Path.Combine(prompts, "chord-page-to-chordpro.md");
        File.WriteAllText(file, "prompt");

        try
        {
            var baseDir = Path.Combine(root, "WebApi", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(baseDir);
            Assert.Equal(file, ChordProConversionJob.ResolvePromptFile(baseDirectory: baseDir));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolvePromptFile_PrefersEnvOverride()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sfm-prompt-{Guid.NewGuid():N}.md");
        File.WriteAllText(file, "prompt");
        try
        {
            Assert.Equal(file, ChordProConversionJob.ResolvePromptFile(envOverride: file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
