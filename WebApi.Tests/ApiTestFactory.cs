using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Endpoints;
using WebApi.Entities;
using Xunit;

namespace WebApi.Tests;

/// <summary>
/// ST-82: the collection that shares one <see cref="ApiTestFactory"/> across the four
/// endpoint-test classes. xUnit never parallelizes tests inside a collection, so every test
/// gets a predictable shared store, and the app boots once — a dozen booted apps exhaust
/// the system's inotify limit (every app watches its config files), and per-test usernames
/// must be unique (they share the database; see the per-class headers).
/// </summary>
[CollectionDefinition("ST-82 endpoint app")]
public class St82EndpointCollection : ICollectionFixture<ApiTestFactory>
{
}

/// <summary>
/// ST-82: shared endpoint-test fixture. Boots the real app on the TestServer — the full
/// /api pipeline (JWT bearer, RequireAuthorization-by-default, the { error: { code, ... } }
/// shape) — with the Npgsql database swapped for a private InMemory store and the Hangfire
/// job servers removed (they dial Postgres on startup) and its storage on the in-memory
/// implementation (ST-93: enqueues must not dial a database). Migration/seed are switched off
/// via configuration added in CreateHost, which also supplies a connection string SHAPE, so
/// the suite runs identically on a clean checkout with no appsettings.Development.json.
/// Shared by one test collection (boots once); tests in the collection run sequentially
/// against one store, so test-created users get unique names.
/// </summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"st82-{Guid.NewGuid():N}";
    private readonly Dictionary<string, string?> _config = new();

    /// <summary>
    /// Config knobs for one fixture (LLM_ALLOW_PRIVATE_NETWORK, Registration:Open, …) —
    /// xUnit's class fixture requires a single public constructor, so extra config is added
    /// fluently by tests that build their own factory, never through a second ctor.
    /// </summary>
    public ApiTestFactory WithConfig(string key, string? value)
    {
        _config[key] = value;
        return this;
    }

    /// <summary>
    /// Hangfire's static log provider (ST-117's sibling: the AddHangfire static side effect)
    /// binds to the booted host's service provider. When one fixture tears down, the static
    /// can outlive that host pointing at its disposed LoggerFactory — an enqueue from any
    /// later fixture's host then throws ObjectDisposedException inside
    /// CoreBackgroundJobFactory: a 500 dev-exception page from POST /api/songs, paid by
    /// every tag/conversion test that touches the client (the CI-only, host-ordering flake
    /// after the v0.4.19 tag, 2026-11-15). Releasing the binding on dispose sends the
    /// fallback chain to a null provider — logging goes quiet in tests, enqueues stay alive.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            Hangfire.Logging.LogProvider.SetCurrentLogProvider(null);
    }

    /// <summary>
    /// Config added here (after the app builder, before the host runs) lands in
    /// app.Configuration: switch off migrate/seed, and disable file-watching — every booted
    /// app takes inotify instances for config/static-file watchers, and a few dozen fixtures
    /// exhaust the system limit (128) and abort the test host. Nothing edits files on disk.
    /// The Npgsql connection string is never dialed — the store is InMemory and the Hangfire
    /// servers are removed in ConfigureWebHost.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Database:AutoMigrate"] = "false",
                ["hosting:FileWatchingExtensionLevel"] = "Disabled",
                // Program.cs calls UseNpgsql(GetConnectionString("DefaultConnection")) eagerly
                // at DI setup, so the key must exist even though nothing dials it (the store is
                // InMemory and the Hangfire servers are removed below). Without it the suite
                // passes only on a machine that has the untracked appsettings.Development.json:
                // a clean checkout (CI, a stranger's clone) throws ArgumentNullException
                // ("connectionString") in every fixture that boots the app — 121 tests.
                // This value is a shape, never a credential, and Timeout=1 bounds any dial: the
                // app's Hangfire Postgres storage is dropped before DI can resolve it (ST-93, see
                // ConfigureWebHost), so nothing here opens a socket.
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=127.0.0.1;Port=5432;Database=ahchord_tests;Username=ahchord;Password=not-a-real-credential;Timeout=1",
                // The suite registers a user per test, so one fixture makes hundreds of
                // /api/auth calls in seconds — exactly what the login limiter exists to stop.
                // Run the suite on a generous budget; PublicDemoGateTests lowers it to exercise
                // the limiter itself (AddInMemoryCollection below lets a test override).
                ["RATE_LIMIT_AUTH_PERMIT"] = "100000",
                ["RATE_LIMIT_IMPORT_PERMIT"] = "100000",
            };
            foreach (var pair in _config)
                values[pair.Key] = pair.Value;
            config.AddInMemoryCollection(values);
        });
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // ST-93: Hangfire's storage, before anything resolves it. Program.cs registers
            // AddHangfire(UsePostgreSqlStorage(opts => …)), which defers building the
            // PostgreSqlStorage object to DI, and the endpoints enqueue through an injected
            // IBackgroundJobClient (AHC-9, ST-117) — the client resolves its storage from DI,
            // so a PostgreSqlStorage left registered gets dialled on the first enqueue.
            // Against the fixture's SHAPE string that is three connect retries, ~60 s, and a
            // 500 from POST /api/songs, paid by every booted host. Drop the Postgres
            // registrations and register Hangfire on Hangfire's in-memory storage (test-only
            // package): enqueues become fast local rows, and no job runs (the hosted servers
            // are removed below).
            services.RemoveAll<Hangfire.PostgreSql.PostgreSqlStorage>();
            services.RemoveAll<JobStorage>();
            services.AddHangfire(cfg => cfg.UseMemoryStorage());

            // Swap EF over to InMemory fully: the Npgsql provider's registrations must go
            // (EF refuses a provider mix), not just the DbContextOptions.
            foreach (var d in services
                         .Where(d => d.ImplementationType?.Assembly?.GetName().Name
                                     is "Npgsql.EntityFrameworkCore.PostgreSQL")
                         .ToList())
            {
                services.Remove(d);
            }
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            // EF 10's AddDbContext registers the options-builder lambda as an
            // IDbContextOptionsConfiguration<T> — the Npgsql one is aggregated into every
            // DbContextOptions, so it must go before the InMemory AddDbContext is added.
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(_dbName));

            // The two AddHangfireServer hosted services connect to Postgres when the host
            // starts and fail its stop — no jobs run in tests. They register through factory
            // descriptors (the framework services are typed), so drop exactly those.
            foreach (var d in services
                         .Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationFactory is not null
                             && d.ImplementationInstance is null)
                         .ToList())
            {
                services.Remove(d);
            }
        });
    }

    // ---------- client helpers ----------

    /// <summary>An authenticated client: registers a user via POST /api/auth/register and sends its bearer token.</summary>
    /// <summary>Register via POST /api/auth/register, then a client sending its bearer token.
    /// The username defaults to a unique one — tests share the collection store.</summary>
    public async Task<RegisteredUser> RegisterUserAsync(
        string? username = null, string? email = null, string password = "Str0ngPass!", string? displayName = null)
    {
        username ??= UniqueName("user");
        var response = await CreateClient().PostAsync("/api/auth/register",
            Json(new RegisterRequest(username, email ?? $"{username}@studio.test", password, displayName)));
        response.EnsureSuccessStatusCode();
        var auth = await ReadJsonAsync<AuthResponse>(response);
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        return new RegisteredUser(client, auth);
    }

    public HttpClient AuthedClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A login name unique within the shared collection store (within the 32-char cap).</summary>
    public static string UniqueName(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    // ---------- direct-store helpers (seeding rows no endpoint can create) ----------

    /// <summary>ST-61: promotion is the seeder's job — tests apply it directly to the store.</summary>
    public async Task PromoteAdminAsync(string userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.IsAdmin = true;
        await db.SaveChangesAsync();
    }

    public async Task<Song> SeedSongAsync(string title = "Test Song")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var song = NewSong(title);
        db.Songs.Add(song);
        await db.SaveChangesAsync();
        return song;
    }

    /// <summary>A playlist, optionally in a collection, with <paramref name="songCount"/> fresh linked songs.</summary>
    public async Task<Playlist> SeedPlaylistAsync(string name, Guid? collectionId = null, int songCount = 0)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var playlist = new Playlist
        {
            Id = Guid.NewGuid(),
            Name = name,
            CollectionId = collectionId,
            IsSetlist = false,
            CreatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Playlists.Add(playlist);
        for (var i = 0; i < songCount; i++)
        {
            var song = NewSong($"{name} song {i + 1}");
            db.Songs.Add(song);
            db.PlaylistSongs.Add(new PlaylistSong { PlaylistId = playlist.Id, SongId = song.Id, Position = i + 1 });
        }
        await db.SaveChangesAsync();
        return playlist;
    }

    public async Task<Playlist?> GetPlaylistAsync(Guid id)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task SeedWishlistItemAsync(
        string userId, string title, DateTime createdAt, string status = "waiting", Guid? convertedSongId = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.WishlistItems.Add(new WishlistItem
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = title,
            Status = status,
            ConvertedSongId = convertedSongId,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Fill a song's source-file quota straight into the store (upload-quota tests).</summary>
    public async Task SeedSourceFilesAsync(Guid songId, int count)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.SongSourceFiles.Add(new SongSourceFile
            {
                Id = Guid.NewGuid(),
                SongId = songId,
                FileName = $"seed-{i + 1}.png",
                ContentType = "image/png",
                SizeBytes = 4,
                Data = [1, 2, 3, 4],
                CreatedAt = new DateTime(2026, 11, 1, 0, 0, i, DateTimeKind.Utc),
            });
        }
        await db.SaveChangesAsync();
    }

    private static Song NewSong(string title)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Artist = "Artist",
            CapoFret = 0,
            ChordProText = "[C]test",
            CreatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    // ---------- JSON helpers (the app serializes camelCase) ----------

    public static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body, WebJson), Encoding.UTF8, "application/json");

    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(text, WebJson)
            ?? throw new InvalidOperationException($"Expected JSON for {typeof(T).Name}, got: {text}");
    }

    /// <summary>Parses the app's { error: { code, message, fields? } } body together with the status code.</summary>
    public static async Task<(int Status, string Code, string Message, Dictionary<string, string?> Fields)> ReadErrorAsync(
        HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        var fields = new Dictionary<string, string?>();
        if (error.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in f.EnumerateObject())
            {
                fields[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();
            }
        }
        return ((int)response.StatusCode,
            error.GetProperty("code").GetString()!,
            error.GetProperty("message").GetString()!,
            fields);
    }

    public sealed record RegisteredUser(HttpClient Client, AuthResponse Auth)
    {
        public string Token => Auth.Token;
        public string Id => Auth.User.Id;
        public string Username => Auth.User.Username!;
        public string Email => Auth.User.Email;
    }
}
