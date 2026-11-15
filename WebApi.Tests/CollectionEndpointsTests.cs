using WebApi.Dtos;

namespace WebApi.Tests;

/// <summary>
/// ST-82: the /api/collections endpoints (ST-11) — CRUD with the locked category list
/// (canonical form, case-insensitive input), the live playlistCount, the detail view
/// (playlists ordered by name with per-playlist song counts), delete keeping its playlists
/// (the CollectionId unassign is the Postgres FK's job, noted at the test), and authz: the
/// /api group requires authentication, and the /api group requires authentication, and the
/// collection CRUD itself has NO admin gate — a plain band member may create, edit and
/// delete collections (verified for both a regular user and an admin).
/// The ?category= filter is a Npgsql ILIKE query and is not runnable on the InMemory
/// store, so it is not covered here.
/// Runs in the shared ST-82 collection store: usernames are unique per test.
/// </summary>
[Collection("ST-82 endpoint app")]
public class CollectionEndpointsTests
{
    private readonly ApiTestFactory _app;

    public CollectionEndpointsTests(ApiTestFactory app) => _app = app;

    private HttpClient Anonymous => _app.CreateClient();

    // ---------- POST /api/collections ----------

    [Fact]
    public async Task Create_201_CategoryDefaultsToOther()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/collections",
            ApiTestFactory.Json(new CreateCollectionRequest("Sunday Songs", "Hymns", null)));

        Assert.Equal(201, (int)response.StatusCode);
        var dto = await ApiTestFactory.ReadJsonAsync<CollectionDto>(response);
        Assert.Equal("Sunday Songs", dto.Name);
        Assert.Equal("Hymns", dto.Description);
        Assert.Equal("Other", dto.Category);
        Assert.Equal(0, dto.PlaylistCount);
    }

    [Fact]
    public async Task Create_CanonicalizesCategory_CaseInsensitive()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/collections",
            ApiTestFactory.Json(new CreateCollectionRequest("Sunday Songs", null, "worship")));

        Assert.Equal("Worship", (await ApiTestFactory.ReadJsonAsync<CollectionDto>(response)).Category);
    }

    [Fact]
    public async Task Create_MissingName_400()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/collections",
            ApiTestFactory.Json(new CreateCollectionRequest("  ", null, "Other")));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
        Assert.True(fields.ContainsKey("name"));
    }

    [Fact]
    public async Task Create_UnknownCategory_400()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/collections",
            ApiTestFactory.Json(new CreateCollectionRequest("Sunday Songs", null, "Concerts")));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
        Assert.True(fields.ContainsKey("category"));
    }

    [Fact]
    public async Task Create_Anonymous_Unauthorized()
    {
        var response = await Anonymous.PostAsync("/api/collections",
            ApiTestFactory.Json(new CreateCollectionRequest("Free", null, null)));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    // ---------- GET /api/collections ----------

    [Fact]
    public async Task List_IncludesLivePlaylistCount()
    {
        var user = await _app.RegisterUserAsync();
        var created = await CreateAsync(user.Client, "Gigs");
        await _app.SeedPlaylistAsync("Set A", created.Id);
        await _app.SeedPlaylistAsync("Set B", created.Id);
        await _app.SeedPlaylistAsync("Loose set"); // outside the collection

        var response = await user.Client.GetAsync("/api/collections");

        Assert.Equal(200, (int)response.StatusCode);
        var list = await ApiTestFactory.ReadJsonAsync<List<CollectionDto>>(response);
        Assert.Equal(2, Assert.Single(list, c => c.Id == created.Id).PlaylistCount);
    }

    [Fact]
    public async Task List_Anonymous_Unauthorized()
    {
        var response = await Anonymous.GetAsync("/api/collections");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    // ---------- GET /api/collections/{id} ----------

    [Fact]
    public async Task Detail_200_PlaylistsOrderedByName_WithPerPlaylistSongCounts()
    {
        var user = await _app.RegisterUserAsync();
        var created = await CreateAsync(user.Client, "Gigs");
        await _app.SeedPlaylistAsync("B set", created.Id, songCount: 1);
        await _app.SeedPlaylistAsync("A set", created.Id, songCount: 3);

        var response = await user.Client.GetAsync($"/api/collections/{created.Id}");

        Assert.Equal(200, (int)response.StatusCode);
        var detail = await ApiTestFactory.ReadJsonAsync<CollectionDetailDto>(response);
        Assert.Equal(2, detail.PlaylistCount);
        Assert.Equal(new[] { "A set", "B set" }, detail.Playlists.Select(p => p.Name));
        Assert.Equal(new[] { 3, 1 }, detail.Playlists.Select(p => p.SongCount));
    }

    [Fact]
    public async Task Detail_MissingId_404()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.GetAsync($"/api/collections/{Guid.NewGuid()}");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }

    // ---------- PUT /api/collections/{id} ----------

    [Fact]
    public async Task Update_PartialReplacement_KeepsUnsetFields_AndReportsPlaylistCount()
    {
        var user = await _app.RegisterUserAsync();
        var created = await CreateAsync(user.Client, "Sunday Songs", description: "Hymns", category: "Worship");
        await _app.SeedPlaylistAsync("Set A", created.Id);

        var response = await user.Client.PutAsync($"/api/collections/{created.Id}",
            ApiTestFactory.Json(new UpdateCollectionRequest("Sunday Night", null, null)));

        Assert.Equal(200, (int)response.StatusCode);
        var dto = await ApiTestFactory.ReadJsonAsync<CollectionDto>(response);
        Assert.Equal("Sunday Night", dto.Name);
        Assert.Equal("Hymns", dto.Description);  // null fields are left unchanged
        Assert.Equal("Worship", dto.Category);
        Assert.Equal(1, dto.PlaylistCount);
    }

    [Fact]
    public async Task Update_EmptyName_400()
    {
        var user = await _app.RegisterUserAsync();
        var created = await CreateAsync(user.Client, "Sunday Songs");

        var response = await user.Client.PutAsync($"/api/collections/{created.Id}",
            ApiTestFactory.Json(new UpdateCollectionRequest("   ", null, null)));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
        Assert.True(fields.ContainsKey("name"));
    }

    [Fact]
    public async Task Update_UnknownCategory_400()
    {
        var user = await _app.RegisterUserAsync();
        var created = await CreateAsync(user.Client, "Sunday Songs");

        var response = await user.Client.PutAsync($"/api/collections/{created.Id}",
            ApiTestFactory.Json(new UpdateCollectionRequest(null, null, "Concerts")));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
        Assert.True(fields.ContainsKey("category"));
    }

    [Fact]
    public async Task Update_MissingId_404()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PutAsync($"/api/collections/{Guid.NewGuid()}",
            ApiTestFactory.Json(new UpdateCollectionRequest("Ghost", null, null)));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }

    // ---------- DELETE /api/collections/{id} ----------

    [Fact]
    public async Task Delete_204_KeepsItsPlaylists()
    {
        var user = await _app.RegisterUserAsync();
        var created = await CreateAsync(user.Client, "Gigs");
        var playlist = await _app.SeedPlaylistAsync("Set A", created.Id);

        var response = await user.Client.DeleteAsync($"/api/collections/{created.Id}");
        Assert.Equal(204, (int)response.StatusCode);

        // ST-11: deleting a collection never deletes its playlists. The unassignment
        // (CollectionId → null) is enforced by the database FK (ON DELETE SET NULL), which
        // the InMemory store does not apply to untracked rows — so what is verifiable here
        // is that the playlist SURVIVES; the unassign is exercised by Postgres in staging.
        var kept = await _app.GetPlaylistAsync(playlist.Id);
        Assert.NotNull(kept);
    }

    [Fact]
    public async Task Delete_MissingId_404()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.DeleteAsync($"/api/collections/{Guid.NewGuid()}");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }

    [Fact]
    public async Task Delete_Anonymous_Unauthorized()
    {
        var response = await Anonymous.DeleteAsync($"/api/collections/{Guid.NewGuid()}");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    // ---------- authz: the /api group requires auth; collection CRUD has no admin gate ----------

    [Fact]
    public async Task RegularUser_CanCrud_Collections_NoAdminRequired()
    {
        var user = await _app.RegisterUserAsync();
        Assert.False(user.Auth.User.IsAdmin);

        var created = await CreateAsync(user.Client, "Gigs");

        var update = await user.Client.PutAsync($"/api/collections/{created.Id}",
            ApiTestFactory.Json(new UpdateCollectionRequest("Sunday Gigs", null, null)));
        Assert.Equal(200, (int)update.StatusCode);

        var delete = await user.Client.DeleteAsync($"/api/collections/{created.Id}");
        Assert.Equal(204, (int)delete.StatusCode);
    }

    [Fact]
    public async Task AdminUser_CanCrud_Collections()
    {
        var admin = await _app.RegisterUserAsync("admin");
        await _app.PromoteAdminAsync(admin.Id);

        var created = await CreateAsync(admin.Client, "Gigs");
        Assert.Equal(204, (int)(await admin.Client.DeleteAsync($"/api/collections/{created.Id}")).StatusCode);
    }

    private static async Task<CollectionDto> CreateAsync(
        HttpClient client, string name, string? description = null, string? category = null)
    {
        var response = await client.PostAsync("/api/collections",
            ApiTestFactory.Json(new CreateCollectionRequest(name, description, category)));
        response.EnsureSuccessStatusCode();
        return await ApiTestFactory.ReadJsonAsync<CollectionDto>(response);
    }
}
