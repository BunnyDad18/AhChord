using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WebApi.Data;
using WebApi.Endpoints;
using WebApi.Entities;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// ST-68: metadata-scan progress reporting. The full-library scan maintains a single
/// MetadataScanProgress row (updated per song) that the Songs page polls for its live
/// strip (current song, n/total, elapsed, ETA). These tests run the real job against an
/// in-memory DB with a fake LLM + settings provider.
/// </summary>
public class St68ScanProgressTests
{
    // --- fakes ------------------------------------------------------------------

    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;
        public Action? OnCall;

        public Task<string> ConvertAsync(
            LlmEffectiveSettings settings, string systemPrompt, string userContent, CancellationToken ct = default)
        {
            Calls++;
            OnCall?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("""{ "proposals": [ { "field": "genre", "value": "rock" } ] }""");
        }

        public Task<string> ConvertStreamingAsync(
            LlmEffectiveSettings settings, string systemPrompt, string userContent,
            Func<string, Task>? onChunk = null, CancellationToken ct = default)
            => ConvertAsync(settings, systemPrompt, userContent, ct);
    }

    private sealed class FakeSettings : ILlmSettingsProvider
    {
        public Task<LlmEffectiveSettings> ResolveAsync(string? userId, CancellationToken ct = default)
            => Task.FromResult(Effective());

        public Task<LlmEffectiveSettings> ResolveForJobAsync(string? userId, CancellationToken ct = default)
            => Task.FromResult(Effective());

        private static LlmEffectiveSettings Effective() =>
            new("http://llm.test/v1", "m", "k", 4096, "user");
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private static (MetadataReviewJob Job, ServiceProvider Provider, TestLifetime Lifetime) NewHarness(
        Func<TestLifetime, Action>? onLlmCall = null)
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var lifetime = new TestLifetime();
        var job = new MetadataReviewJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeLlm { OnCall = onLlmCall?.Invoke(lifetime) }, new FakeSettings(),
            NullLogger<MetadataReviewJob>.Instance,
            lifetime);
        return (job, provider, lifetime);
    }

    private static void SeedSongs(ApplicationDbContext db, int n)
    {
        for (var i = 1; i <= n; i++)
            db.Songs.Add(new Song
            {
                Id = Guid.NewGuid(),
                Title = $"Song {i}",
                Artist = "A",
                ChordProText = "{title: Song $i}\n[C] lyric [G] line",
                CreatedAt = DateTime.UtcNow.AddMinutes(-n + i)
            });
        db.SaveChanges();
    }

    // --- the scan writes progress ------------------------------------------------------------------

    [Fact]
    public async Task ScanLibrary_MaintainsProgressRow_ThroughToDone()
    {
        var (job, provider, _) = NewHarness();
        using (var scope = provider.CreateScope())
            SeedSongs(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), 3);

        await job.ScanLibraryAsync("u1");

        using var check = provider.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = Assert.Single(await db.MetadataScanProgresses.AsNoTracking().ToListAsync());

        Assert.Equal("library", row.Key);
        Assert.Equal("done", row.Status);
        Assert.Equal(3, row.Total);
        Assert.Equal(3, row.Index); // every song passed through the loop
        Assert.NotNull(row.FinishedAt);
        Assert.Null(row.CurrentTitle); // cleared at the finish
        Assert.Equal(3, row.ProposalsAdded); // one genre proposal auto-applied per song
    }

    [Fact]
    public async Task ScanSong_DoesNotTouchProgressRow()
    {
        var (job, provider, _) = NewHarness();
        Guid songId;
        using (var scope = provider.CreateScope())
        {
            var seeded = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            SeedSongs(seeded, 1);
            songId = seeded.Songs.Single().Id;
        }

        await job.ScanSongAsync(songId, "u1");

        using var check = provider.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        // Single-song scans (post-import) are instant at UI scale — no progress row.
        Assert.Empty(await db.MetadataScanProgresses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ScanLibrary_OverwritesPreviousRun()
    {
        var (job, provider, _) = NewHarness();
        using (var scope = provider.CreateScope())
            SeedSongs(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), 2);

        await job.ScanLibraryAsync("u1");
        await job.ScanLibraryAsync("u1");

        using var check = provider.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.MetadataScanProgresses.AsNoTracking().ToListAsync();

        var row = Assert.Single(rows); // one row, reused — never one per scan
        Assert.Equal("done", row.Status);
        Assert.Equal(2, row.Index);
    }

    // ST-74: shutdown (host stop) cancels the job through the host lifetime — Hangfire 1.8
    // never cancels the job token itself. The scan must mark the row "interrupted" and
    // RETURN NORMALLY: a throwing job gets re-queued, so every redeploy restarts the scan
    // from song 1 (the strip-fill loop).
    [Fact]
    public async Task ScanLibrary_ShutdownCancellation_MarksInterrupted_AndReturns()
    {
        var (job, provider, _) = NewHarness(lt => () => lt.StopApplication()); // stop mid-first-song
        using (var scope = provider.CreateScope())
            SeedSongs(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), 5);

        await job.ScanLibraryAsync("u1"); // must NOT throw

        using var check = provider.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = Assert.Single(await db.MetadataScanProgresses.AsNoTracking().ToListAsync());

        Assert.Equal("interrupted", row.Status);
        Assert.Equal(1, row.Index); // stopped on the song that was cancelled
        Assert.NotNull(row.FinishedAt);
        Assert.Null(row.CurrentTitle);
    }

    // --- ETA math (endpoint helper) ------------------------------------------------------------------

    [Theory]
    [InlineData(10.0, 1, 3, 20)]   // 10s per song, 2 songs left → 20s
    [InlineData(60.0, 6, 8, 20)]   // 10s/song, 2 left → 20s
    [InlineData(10.0, 0, 3, -1)]   // nothing processed → no rate → null (-1 stands for null)
    [InlineData(10.0, 3, 3, -1)]   // nothing left → null
    [InlineData(10.0, 1, 1, -1)]   // single-song library → null
    public void ComputeEta_RateBased_CoarseAndHonest(double elapsed, int index, int total, int expected)
    {
        var eta = MetadataReviewEndpoints.ComputeEtaSeconds(
            TimeSpan.FromSeconds(elapsed), index, total, "running");

        Assert.Equal(expected < 0 ? (int?)null : expected, eta);
    }

    [Fact]
    public void ComputeEta_NotRunning_IsNull()
        => Assert.Null(MetadataReviewEndpoints.ComputeEtaSeconds(TimeSpan.FromSeconds(60), 1, 100, "done"));
}
