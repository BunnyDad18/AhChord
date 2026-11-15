using WebApi.Dtos;

namespace WebApi.Tests;

/// <summary>
/// ST-82: the /api/wishlist endpoints (ST-44) — the per-user wishlist: add (201, "waiting"),
/// own list newest-first, the status lifecycle waiting→found→converted (converted requires a
/// saved song in the library), delete (409 once converted — it stays as a record), 404 for
/// missing and foreign ids (the list is per-user), and auth required on every route.
/// Runs in the shared ST-82 collection store: usernames are unique per test.
/// </summary>
[Collection("ST-82 endpoint app")]
public class WishlistEndpointsTests
{
    private readonly ApiTestFactory _app;

    public WishlistEndpointsTests(ApiTestFactory app) => _app = app;

    private HttpClient Anonymous => _app.CreateClient();

    // ---------- POST /api/wishlist ----------

    [Fact]
    public async Task Add_Returns201_WaitingItem_TitleTrimmed_ArtistNullWhenBlank()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/wishlist",
            ApiTestFactory.Json(new CreateWishlistRequest("  Amazing Grace  ", "  ", null)));

        Assert.Equal(201, (int)response.StatusCode);
        var item = await ApiTestFactory.ReadJsonAsync<WishlistItemDto>(response);
        Assert.Equal("Amazing Grace", item.Title);
        Assert.Null(item.Artist);
        Assert.Equal("waiting", item.Status);
        Assert.Null(item.ConvertedSongId);
    }

    [Fact]
    public async Task Add_BlankTitle_400()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/wishlist",
            ApiTestFactory.Json(new CreateWishlistRequest("   ")));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
    }

    [Fact]
    public async Task Add_LongTitle_ClippedTo200()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/wishlist",
            ApiTestFactory.Json(new CreateWishlistRequest(new string('x', 250))));

        var item = await ApiTestFactory.ReadJsonAsync<WishlistItemDto>(response);
        Assert.Equal(200, item.Title.Length);
    }

    [Fact]
    public async Task Add_Anonymous_Unauthorized()
    {
        var response = await Anonymous.PostAsync("/api/wishlist",
            ApiTestFactory.Json(new CreateWishlistRequest("Free for all")));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    // ---------- GET /api/wishlist ----------

    [Fact]
    public async Task List_ReturnsOwnItemsNewestFirst_OtherUsersExcluded()
    {
        var james = await _app.RegisterUserAsync();
        var other = await _app.RegisterUserAsync();
        // A seeded older item so the newest-first ordering is observable.
        await _app.SeedWishlistItemAsync(james.Id, "Old item", DateTime.UtcNow.AddDays(-1));
        await james.Client.PostAsync("/api/wishlist", ApiTestFactory.Json(new CreateWishlistRequest("New item")));
        await other.Client.PostAsync("/api/wishlist", ApiTestFactory.Json(new CreateWishlistRequest("Not james")));

        var response = await james.Client.GetAsync("/api/wishlist");

        Assert.Equal(200, (int)response.StatusCode);
        var items = await ApiTestFactory.ReadJsonAsync<List<WishlistItemDto>>(response);
        Assert.Equal(new[] { "New item", "Old item" }, items.Select(i => i.Title));
    }

    [Fact]
    public async Task List_Anonymous_Unauthorized()
    {
        var response = await Anonymous.GetAsync("/api/wishlist");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    // ---------- PATCH /api/wishlist/{id} ----------

    [Fact]
    public async Task Patch_StatusToFound_200()
    {
        var user = await _app.RegisterUserAsync();
        var item = await AddAsync(user.Client, "Amazing Grace");

        var response = await user.Client.PatchAsync($"/api/wishlist/{item.Id}",
            ApiTestFactory.Json(new UpdateWishlistRequest("FOUND"))); // case-insensitive

        Assert.Equal(200, (int)response.StatusCode);
        var updated = await ApiTestFactory.ReadJsonAsync<WishlistItemDto>(response);
        Assert.Equal("found", updated.Status);
    }

    [Fact]
    public async Task Patch_InvalidStatus_400()
    {
        var user = await _app.RegisterUserAsync();
        var item = await AddAsync(user.Client, "Amazing Grace");

        var response = await user.Client.PatchAsync($"/api/wishlist/{item.Id}",
            ApiTestFactory.Json(new UpdateWishlistRequest("nonsense")));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
    }

    [Fact]
    public async Task Patch_ConvertedWithoutSongId_400()
    {
        var user = await _app.RegisterUserAsync();
        var item = await AddAsync(user.Client, "Amazing Grace");

        var response = await user.Client.PatchAsync($"/api/wishlist/{item.Id}",
            ApiTestFactory.Json(new UpdateWishlistRequest("converted")));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
    }

    [Fact]
    public async Task Patch_ConvertedUnknownSong_400()
    {
        var user = await _app.RegisterUserAsync();
        var item = await AddAsync(user.Client, "Amazing Grace");

        var response = await user.Client.PatchAsync($"/api/wishlist/{item.Id}",
            ApiTestFactory.Json(new UpdateWishlistRequest("converted", Guid.NewGuid())));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
    }

    [Fact]
    public async Task Patch_ConvertedWithSavedSong_200_ThenLinkOnlyUpdate()
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync("Saved song");
        var item = await AddAsync(user.Client, "Amazing Grace");

        var convert = await user.Client.PatchAsync($"/api/wishlist/{item.Id}",
            ApiTestFactory.Json(new UpdateWishlistRequest("converted", song.Id)));
        Assert.Equal(200, (int)convert.StatusCode);
        var converted = await ApiTestFactory.ReadJsonAsync<WishlistItemDto>(convert);
        Assert.Equal("converted", converted.Status);
        Assert.Equal(song.Id, converted.ConvertedSongId);

        // The endpoint's convertedSongId-only branch: relink without touching the status.
        var replacement = await _app.SeedSongAsync("Replacement song");
        var relink = await user.Client.PatchAsync($"/api/wishlist/{item.Id}",
            ApiTestFactory.Json(new UpdateWishlistRequest(ConvertedSongId: replacement.Id)));

        var updated = await ApiTestFactory.ReadJsonAsync<WishlistItemDto>(relink);
        Assert.Equal("converted", updated.Status);
        Assert.Equal(replacement.Id, updated.ConvertedSongId);
    }

    [Fact]
    public async Task Patch_MissingId_404()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PatchAsync($"/api/wishlist/{Guid.NewGuid()}",
            ApiTestFactory.Json(new UpdateWishlistRequest("found")));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }

    [Fact]
    public async Task Patch_OtherUsersItem_404()
    {
        var james = await _app.RegisterUserAsync();
        var other = await _app.RegisterUserAsync();
        var item = await AddAsync(james.Client, "James's item");

        var response = await other.Client.PatchAsync($"/api/wishlist/{item.Id}",
            ApiTestFactory.Json(new UpdateWishlistRequest("found")));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }

    // ---------- DELETE /api/wishlist/{id} ----------

    [Fact]
    public async Task Delete_Waiting_204_ItemGoneAfterwards()
    {
        var user = await _app.RegisterUserAsync();
        var item = await AddAsync(user.Client, "Amazing Grace");

        var delete = await user.Client.DeleteAsync($"/api/wishlist/{item.Id}");
        Assert.Equal(204, (int)delete.StatusCode);

        var again = await user.Client.DeleteAsync($"/api/wishlist/{item.Id}");
        Assert.Equal(404, (int)again.StatusCode);
    }

    [Fact]
    public async Task Delete_Converted_Conflict_ItemStaysOnWishlist()
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync("Saved song");
        var item = await AddAsync(user.Client, "Amazing Grace");
        await user.Client.PatchAsync($"/api/wishlist/{item.Id}",
            ApiTestFactory.Json(new UpdateWishlistRequest("converted", song.Id)));

        var response = await user.Client.DeleteAsync($"/api/wishlist/{item.Id}");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(409, status);
        Assert.Equal("already-converted", code);

        var list = await ApiTestFactory.ReadJsonAsync<List<WishlistItemDto>>(
            await user.Client.GetAsync("/api/wishlist"));
        Assert.Contains(list, i => i.Id == item.Id);
    }

    [Fact]
    public async Task Delete_MissingId_404()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.DeleteAsync($"/api/wishlist/{Guid.NewGuid()}");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }

    [Fact]
    public async Task Delete_Anonymous_Unauthorized()
    {
        var response = await Anonymous.DeleteAsync($"/api/wishlist/{Guid.NewGuid()}");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    private static async Task<WishlistItemDto> AddAsync(HttpClient client, string title)
    {
        var response = await client.PostAsync("/api/wishlist", ApiTestFactory.Json(new CreateWishlistRequest(title)));
        response.EnsureSuccessStatusCode();
        return await ApiTestFactory.ReadJsonAsync<WishlistItemDto>(response);
    }
}
