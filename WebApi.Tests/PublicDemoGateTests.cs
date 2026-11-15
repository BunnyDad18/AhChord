using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WebApi.Data;
using WebApi.Dtos;
using WebApi.Endpoints;
using WebApi.Identity;
using WebApi.Services;

namespace WebApi.Tests;

/// <summary>
/// The public-demo gates (SECURITY.md, "Hardening a public demo"). Each gate is one startup
/// config read, so every test boots its own fixture with the flag set — the defaults are the
/// LAN product, and these tests are what keep the internet-facing posture honest.
/// Gate 5 (the Android network security config) is a mobile build concern, not an endpoint.
/// </summary>
public class PublicDemoGateTests
{
    /// <summary>A fixture with extra config — the flags are read at startup, so one per test.</summary>
    private static ApiTestFactory Boot(params (string Key, string Value)[] config)
    {
        var factory = new ApiTestFactory();
        foreach (var (key, value) in config)
            factory.WithConfig(key, value);
        return factory;
    }

    private static MultipartFormDataContent FilePart(string fileName, byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent();
        form.Add(part, "file", fileName);
        return form;
    }

    /// <summary>Status code of a request, for assertions that only care about the status.</summary>
    private static async Task<int> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return (int)response.StatusCode;
    }

    /// <summary>Runs the startup seed the way Program.cs does (it needs a scope).</summary>
    private static async Task<string?> SeedAdminAsync(
        ApiTestFactory app, string username, string? email, string password)
    {
        using var scope = app.Services.CreateScope();
        return await DbSeeder.SeedAdminAsync(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(), username, email, password);
    }

    // ---------- gate 1: REGISTRATION_OPEN ----------

    [Fact]
    public async Task Registration_Closed_403_With_Code()
    {
        using var app = Boot(("REGISTRATION_OPEN", "false"));

        var response = await app.CreateClient().PostAsync("/api/auth/register",
            ApiTestFactory.Json(new RegisterRequest("whoever", "whoever@studio.test", "Str0ngPass!", null)));

        var (status, code, message, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(403, status);
        Assert.Equal("registration-closed", code);
        Assert.Contains("admin", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registration_Is_Open_By_Default()
    {
        using var app = Boot();

        var user = await app.RegisterUserAsync();

        Assert.False(string.IsNullOrEmpty(user.Token));
        Assert.False(user.Auth.User.IsAdmin);
    }

    [Fact]
    public async Task Seeded_Admin_Logs_In_When_Registration_Is_Closed()
    {
        using var app = Boot(("REGISTRATION_OPEN", "false"));

        // Program.cs runs this seed at startup (with the migrations, which tests skip), so the
        // test runs the same call the deployment does.
        var note = await SeedAdminAsync(app, "demo-admin", "admin@example.com", "Demo-Admin-Pass1!");
        Assert.NotNull(note);

        var response = await app.CreateClient().PostAsync("/api/auth/login",
            ApiTestFactory.Json(new LoginRequest("demo-admin", null, "Demo-Admin-Pass1!")));

        var auth = await ApiTestFactory.ReadJsonAsync<AuthResponse>(response);
        Assert.True(auth.User.IsAdmin);
        Assert.Equal("admin@example.com", auth.User.Email);

        // Registration stays closed behind that door.
        Assert.Equal(403, await StatusAsync(app.CreateClient().PostAsync("/api/auth/register",
            ApiTestFactory.Json(new RegisterRequest("intruder", "intruder@studio.test", "Str0ngPass!", null)))));
    }

    [Fact]
    public async Task Seeded_Admin_Is_Idempotent_And_Never_Restores_A_Known_Password()
    {
        using var app = Boot();

        var created = await SeedAdminAsync(app, "twice-admin", null, "Demo-Admin-Pass1!");
        Assert.NotNull(created);

        // A second "seed" (as on every restart) does nothing: the admin exists, its password stands.
        var again = await SeedAdminAsync(app, "twice-admin", null, "Other-Pass-123!");
        Assert.Null(again);

        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var existing = await users.FindByNameAsync("twice-admin");
        Assert.NotNull(existing);
        Assert.True(existing.IsAdmin);
        Assert.False(await users.CheckPasswordAsync(existing, "Other-Pass-123!"));
        Assert.True(await users.CheckPasswordAsync(existing, "Demo-Admin-Pass1!"));
    }

    [Fact]
    public async Task Seeding_With_No_Username_Is_Nothing_To_Do()
    {
        using var app = Boot();

        Assert.Null(await SeedAdminAsync(app, "   ", null, "Demo-Admin-Pass1!"));
        Assert.Null(await SeedAdminAsync(app, "nobody", null, "  "));
    }

    // ---------- gate 2: rate limiting ----------

    [Fact]
    public async Task Auth_Endpoints_Reject_Over_Budget_With_429()
    {
        using var app = Boot(
            ("RATE_LIMIT_AUTH_PERMIT", "3"),
            ("RATE_LIMIT_AUTH_WINDOW_SECONDS", "120"));
        var client = app.CreateClient();

        HttpResponseMessage? rejected = null;
        var statuses = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var response = await client.PostAsync("/api/auth/login",
                ApiTestFactory.Json(new LoginRequest("nobody", null, "not-the-password")));
            statuses.Add((int)response.StatusCode);
            if ((int)response.StatusCode == 429)
                rejected = response;
        }

        Assert.Equal(401, statuses[0]);
        Assert.Contains(429, statuses);
        Assert.NotNull(rejected);

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(rejected);
        Assert.Equal(429, status);
        Assert.Equal("rate-limited", code);
        Assert.True(rejected.Headers.TryGetValues("Retry-After", out var retry));
        Assert.True(int.Parse(Assert.Single(retry!)) > 0);
    }

    [Fact]
    public async Task Auth_Budget_Is_Generous_For_A_Real_Client()
    {
        // The LAN product's default must not throttle an app that logs in, loads, re-loads.
        using var app = Boot();
        var client = app.CreateClient();

        for (var i = 0; i < 25; i++)
        {
            var response = await client.PostAsync("/api/auth/login",
                ApiTestFactory.Json(new LoginRequest("nobody", null, "not-the-password")));
            Assert.Equal(401, (int)response.StatusCode);
        }
    }

    [Fact]
    public void Rate_Limit_Partition_Keys_Are_Per_User_Then_Per_Address()
    {
        var anon = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.9") } };
        Assert.Equal("ip:203.0.113.9", RateLimitKey.Partition(anon));

        // JwtBearer maps the JWT "sub" to ClaimTypes.NameIdentifier — the key has to read the
        // mapped name, or every signed-in user shares the anonymous bucket.
        var authed = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.9") } };
        authed.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], "Bearer"));
        Assert.Equal("u:user-1", RateLimitKey.Partition(authed));

        Assert.Equal("anon:unknown", RateLimitKey.Partition(new DefaultHttpContext()));
    }

    // ---------- gate 3: LIBRARY_WRITES_ADMIN_ONLY ----------

    [Fact]
    public async Task Source_File_Delete_Is_Admin_Only_When_Flagged()
    {
        using var app = Boot(("LIBRARY_WRITES_ADMIN_ONLY", "true"));
        var member = await app.RegisterUserAsync();
        var admin = await app.RegisterUserAsync();
        await app.PromoteAdminAsync(admin.Id);

        var song = await app.SeedSongAsync("Gate Song");
        var upload = await member.Client.PostAsync($"/api/songs/{song.Id}/source-files",
            FilePart("chart.pdf", [0x25, 0x50, 0x44, 0x46]));
        upload.EnsureSuccessStatusCode();
        var uploaded = await ApiTestFactory.ReadJsonAsync<SongSourceFileDto>(upload);

        var denied = await member.Client.DeleteAsync($"/api/source-files/{uploaded.Id}");
        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(denied);
        Assert.Equal(403, status);
        Assert.Equal("forbidden", code);

        Assert.Equal(204, await StatusAsync(admin.Client.DeleteAsync($"/api/source-files/{uploaded.Id}")));
    }

    [Fact]
    public async Task Collection_Rename_Is_Admin_Only_Delete_Too_Additive_Create_Is_Open()
    {
        using var app = Boot(("LIBRARY_WRITES_ADMIN_ONLY", "true"));
        var member = await app.RegisterUserAsync();

        var created = await member.Client.PostAsync("/api/collections",
            ApiTestFactory.Json(new CreateCollectionRequest("Gate Collection", null, null)));
        Assert.Equal(201, (int)created.StatusCode);
        var collection = await ApiTestFactory.ReadJsonAsync<CollectionDto>(created);

        var renamed = await member.Client.PutAsync($"/api/collections/{collection.Id}",
            ApiTestFactory.Json(new UpdateCollectionRequest("Renamed By Member", null, null)));
        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(renamed);
        Assert.Equal(403, status);
        Assert.Equal("forbidden", code);

        var deleted = await member.Client.DeleteAsync($"/api/collections/{collection.Id}");
        Assert.Equal(403, (int)deleted.StatusCode);
    }

    [Fact]
    public async Task Bug_Report_Triage_Is_Admin_Only()
    {
        using var app = Boot(("LIBRARY_WRITES_ADMIN_ONLY", "true"));
        var member = await app.RegisterUserAsync();

        // The gate runs before the report file is touched, so this asserts the 403 without
        // writing to the real /data/bug_reports.md.
        var denied = await member.Client.PostAsync("/api/bug-reports/1/status",
            ApiTestFactory.Json(new BugReportEndpoints.BugReportStatusRequest("resolved", null)));
        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(denied);
        Assert.Equal(403, status);
        Assert.Equal("forbidden", code);

        var update = await member.Client.PostAsync("/api/bug-reports/1",
            ApiTestFactory.Json(new BugReportEndpoints.BugReportUpdateRequest("resolved", null, "triage")));
        Assert.Equal(403, (int)update.StatusCode);
    }

    [Fact]
    public async Task Shared_Writes_Are_Open_To_Members_By_Default()
    {
        // The flag off is the LAN product: a member renames and deletes shared structure.
        using var app = Boot();
        var member = await app.RegisterUserAsync();

        var created = await member.Client.PostAsync("/api/collections",
            ApiTestFactory.Json(new CreateCollectionRequest("Open Collection", null, null)));
        var collection = await ApiTestFactory.ReadJsonAsync<CollectionDto>(created);

        var renamed = await member.Client.PutAsync($"/api/collections/{collection.Id}",
            ApiTestFactory.Json(new UpdateCollectionRequest("Renamed By Member", null, null)));
        Assert.Equal(200, (int)renamed.StatusCode);

        Assert.Equal(204, await StatusAsync(member.Client.DeleteAsync($"/api/collections/{collection.Id}")));
    }

    // ---------- gate 4: JWT security-stamp revocation ----------

    [Fact]
    public async Task Password_Change_Revokes_Tokens_Issued_Before_It()
    {
        using var app = Boot(("JWT_STAMP_CHECK", "true"));
        var user = await app.RegisterUserAsync();

        Assert.Equal(200, await StatusAsync(user.Client.GetAsync("/api/auth/me")));

        var changed = await user.Client.PostAsync("/api/auth/change-password",
            ApiTestFactory.Json(new ChangePasswordRequest("Str0ngPass!", "Str0ngPass!2")));
        Assert.Equal(200, (int)changed.StatusCode);

        // The endpoint hands the caller a replacement token (the stamp rotated, so the one that
        // made the request is dead): the device that changed the password keeps working.
        var rotated = await ApiTestFactory.ReadJsonAsync<ChangePasswordResult>(changed);
        Assert.False(string.IsNullOrEmpty(rotated.Token));
        Assert.Equal(200, await StatusAsync(app.AuthedClient(rotated.Token!).GetAsync("/api/auth/me")));

        // The old 30-day token is dead — that is the whole point of the claim: every OTHER
        // device signs out at the next request.
        Assert.Equal(401, await StatusAsync(app.AuthedClient(user.Token).GetAsync("/api/auth/me")));

        var fresh = await app.CreateClient().PostAsync("/api/auth/login",
            ApiTestFactory.Json(new LoginRequest(user.Username, null, "Str0ngPass!2")));
        var auth = await ApiTestFactory.ReadJsonAsync<AuthResponse>(fresh);
        Assert.Equal(200, await StatusAsync(app.AuthedClient(auth.Token).GetAsync("/api/auth/me")));
    }

    [Fact]
    public async Task Stamp_Check_Off_Keeps_The_Old_Token()
    {
        // The default, i.e. the LAN product's posture.
        using var app = Boot();
        var user = await app.RegisterUserAsync();

        var changed = await user.Client.PostAsync("/api/auth/change-password",
            ApiTestFactory.Json(new ChangePasswordRequest("Str0ngPass!", "Str0ngPass!2")));
        Assert.Equal(200, (int)changed.StatusCode);

        Assert.Equal(200, await StatusAsync(app.AuthedClient(user.Token).GetAsync("/api/auth/me")));
    }

    [Fact]
    public async Task A_Token_Without_A_Stamp_Claim_Is_Rejected()
    {
        using var app = Boot(("JWT_STAMP_CHECK", "true"));
        var user = await app.RegisterUserAsync();

        // A token minted with the running app's own signing key (read from its bearer options),
        // as a pre-gate client would present: the locked claim set, minus the sstamp claim this
        // control requires.
        var key = app.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .TokenValidationParameters!.IssuerSigningKey!;
        var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: new[]
            {
                new Claim("sub", user.Id),
                new Claim("email", user.Email),
                new Claim("name", user.Username!),
                new Claim("is_admin", "false")
            },
            expires: DateTime.UtcNow.AddMinutes(60),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));

        Assert.Equal(401, await StatusAsync(app.AuthedClient(forged).GetAsync("/api/auth/me")));
    }

    // ---------- gate 3 (cont.): the destructive shared writes ----------

    [Fact]
    public async Task Song_Delete_Is_Admin_Only_Edit_And_Create_Are_Open()
    {
        using var app = Boot(("LIBRARY_WRITES_ADMIN_ONLY", "true"));
        var member = await app.RegisterUserAsync();
        var admin = await app.RegisterUserAsync();
        await app.PromoteAdminAsync(admin.Id);

        var song = await app.SeedSongAsync("Shared Song");

        var denied = await member.Client.DeleteAsync($"/api/songs/{song.Id}");
        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(denied);
        Assert.Equal(403, status);
        Assert.Equal("forbidden", code);

        // Creating and editing a chart is the collaborative product — stays open.
        var created = await member.Client.PostAsync("/api/songs",
            ApiTestFactory.Json(new CreateSongRequest(
                "Member Added", null, null, null, null, null, null, "# Demo\n[A]Text", null)));
        Assert.Equal(201, (int)created.StatusCode);

        Assert.Equal(204, await StatusAsync(admin.Client.DeleteAsync($"/api/songs/{song.Id}")));
    }

    [Fact]
    public async Task Playlist_Structure_Is_Admin_Only_Adding_A_Song_Is_Open()
    {
        using var app = Boot(("LIBRARY_WRITES_ADMIN_ONLY", "true"));
        var member = await app.RegisterUserAsync();
        var admin = await app.RegisterUserAsync();
        await app.PromoteAdminAsync(admin.Id);

        var created = await member.Client.PostAsync("/api/playlists",
            ApiTestFactory.Json(new CreatePlaylistRequest("Gate Setlist", null, null, null, null)));
        Assert.Equal(201, (int)created.StatusCode);
        var playlist = await ApiTestFactory.ReadJsonAsync<PlaylistDto>(created);

        var renamed = await member.Client.PutAsync($"/api/playlists/{playlist.Id}",
            ApiTestFactory.Json(new UpdatePlaylistRequest("Renamed", null, null, null, null)));
        Assert.Equal(403, (int)renamed.StatusCode);

        Assert.Equal(403, await StatusAsync(member.Client.DeleteAsync($"/api/playlists/{playlist.Id}")));
        Assert.Equal(403, await StatusAsync(member.Client.PutAsync($"/api/playlists/{playlist.Id}/songs/reorder",
            ApiTestFactory.Json(new ReorderRequest([])))));

        // Adding a song to a setlist is additive → open.
        var song = await app.SeedSongAsync("Add Me");
        Assert.NotEqual(403, await StatusAsync(member.Client.PostAsync($"/api/playlists/{playlist.Id}/songs",
            ApiTestFactory.Json(new AddSongRequest(song.Id, null)))));
        Assert.Equal(403, await StatusAsync(member.Client.DeleteAsync($"/api/playlists/{playlist.Id}/songs/{song.Id}")));

        Assert.Equal(204, await StatusAsync(admin.Client.DeleteAsync($"/api/playlists/{playlist.Id}")));
    }

    // ---------- gate 2: the budget is on the GPU surface, not on reading ----------

    [Fact]
    public async Task Import_Queue_Reads_Are_Not_Throttled_By_The_Enqueue_Budget()
    {
        // The UI polls GET /api/import/convert at boot, on progress events and on reconnects; the
        // 10/60s budget belongs to the routes that queue GPU work (review finding).
        using var app = Boot(("RATE_LIMIT_IMPORT_PERMIT", "2"), ("RATE_LIMIT_IMPORT_WINDOW_SECONDS", "120"));
        var member = await app.RegisterUserAsync();

        for (var i = 0; i < 25; i++)
            Assert.NotEqual(429, await StatusAsync(member.Client.GetAsync("/api/import/convert")));

        // The enqueue POST shares the small budget (the LLM surface answers 503 unconfigured,
        // 400 on validation — 429 is what the policy adds, and it shows up after the permits).
        var statuses = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            statuses.Add(await StatusAsync(member.Client.PostAsync("/api/import/convert",
                ApiTestFactory.Json(new { content = "Verse 1: [G]some pasted chord page text that is long enough" }))));
        }
        Assert.Contains(429, statuses);
    }

    // ---------- gate 2b: TRUSTED_PROXY_IPS parsing ----------

    [Fact]
    public void Trusted_Proxies_Accepts_Addresses_And_Cidr()
    {
        var options = new ForwardedHeadersOptions();
        // The options arrive with loopback pre-trusted (the framework's default) — the test
        // measures what the entries ADDED, which is what TRUSTED_PROXY_IPS controls.
        var proxyBase = options.KnownProxies.Count;
        var networkBase = options.KnownIPNetworks.Count;
        var bad = new List<string>();
        TrustedProxies.Apply(options,
            ["10.0.0.5", "172.18.0.0/16", "fd00::/64", " 192.168.1.1 ", "nonsense", "10.0.0.5/40", "/16"],
            bad.Add);

        var proxies = options.KnownProxies.Select(p => p.ToString()).ToList();
        Assert.Equal(proxyBase + 2, proxies.Count);
        Assert.Contains("10.0.0.5", proxies);
        Assert.Contains("192.168.1.1", proxies);
        Assert.Equal(networkBase + 2, options.KnownIPNetworks.Count);
        Assert.Equal(["nonsense", "10.0.0.5/40", "/16"], bad);
    }

    // ---------- gate 1: startup ordering (the seed wins over the promotion) ----------

    [Fact]
    public async Task Config_Admin_Seeded_Before_Promotion_Wins_Over_The_Oldest_User()
    {
        // Program.cs seeds the config admin BEFORE DbSeeder.Seed runs SeedFirstUserIsAdmin
        // (which promotes the oldest user whenever no admin exists). Seeded after, an existing
        // non-admin is admin by then and SEED_ADMIN_USERNAME is silently ignored.
        using var app = Boot();
        var older = await app.RegisterUserAsync();

        var note = await SeedAdminAsync(app, "the-admin", null, "Demo-Admin-Pass1!");
        Assert.NotNull(note);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        DbSeeder.SeedFirstUserIsAdmin(db);

        Assert.False((await db.Users.AsNoTracking().FirstAsync(u => u.Id == older.Id)).IsAdmin);
        Assert.True((await db.Users.AsNoTracking().FirstAsync(u => u.UserName == "the-admin")).IsAdmin);
    }

    private sealed record ChangePasswordResult(string? Message, string? Token, int ExpiresIn, AuthUserDto? User);
}
