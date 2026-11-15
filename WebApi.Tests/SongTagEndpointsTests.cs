using System.Text.Json;
using WebApi.Dtos;

namespace WebApi.Tests;

/// <summary>
/// ST-106 S4: the tag endpoints. Songs carry artist/genre/tag LISTS; filters match
/// ANY artist (Matt Redman finds the five-artist songs), genres canonicalise to
/// title case (pop=Pop), tag= repeats AND-narrowing, the grouped list groups by
/// title, autocomplete reads /api/tags, and Key is the chord-derived key with the
/// manual-override rules. The shared ST-82 store spans tests — titles/artists here
/// are unique per test.
/// </summary>
[Collection("ST-82 endpoint app")]
public class SongTagEndpointsTests
{
    private readonly ApiTestFactory _app;

    public SongTagEndpointsTests(ApiTestFactory app) => _app = app;

    private const string GChart = """
        {start_of_verse}
        [G]A-maz-ing [G]Grace,
        [D]how [G]sweet
        the [G]sound!
        {end_of_verse}
        {start_of_chorus}
        [G] [C] [D] [G]
        {end_of_chorus}
        """;

    /// <summary>GET /api/songs/{id} returns the { song, parsed } wrapper (ST-08) — read the song half.</summary>
    private static async Task<SongDetailDto> ReadDetailAsync(HttpClient client, Guid id)
    {
        using var doc = await ApiTestFactory.ReadJsonAsync<System.Text.Json.JsonDocument>(
            await client.GetAsync($"/api/songs/{id}"));
        return doc.RootElement.GetProperty("song").Deserialize<SongDetailDto>(ApiTestFactory.WebJson)!;
    }

    // ---------- song create/update with the tag lists ----------

    [Fact]
    public async Task Create_TagLists_LandInDetail_AndTitleCaseGenres()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Tagged Song", null, null, null, null, null, null, GChart, null,
                null,
                ["Alpha One", "Beta Two"], ["pop", "Pop Rock"], ["worship-openers"])));
        Assert.Equal(201, (int)response.StatusCode);

        var dto = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(response);
        Assert.Equal(["Alpha One", "Beta Two"], dto.Artists);
        Assert.Equal(["Pop", "Pop Rock"], dto.Genres); // title-cased, merged spellings
        Assert.Equal(["worship-openers"], dto.Tags);
        // The flat columns follow as the one-release cache.
        Assert.Equal("Alpha One, Beta Two", dto.Artist);
        Assert.Equal("Pop", dto.Genre);
    }

    [Fact]
    public async Task Create_FlatArtistString_StillSplitsIntoArtistTags()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Five Writers Song",
                "Gemma Green, Hugo Hill, Iris Ivy, Matt Redman, Pete Price",
                null, null, null, null, null, GChart, null)));
        Assert.Equal(201, (int)response.StatusCode);

        var dto = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(response);
        // Null-forgiving: Artists is a nullable DTO shape; the response always carries the list.
        Assert.Contains("Matt Redman", dto.Artists!);
        Assert.Equal(5, dto.Artists!.Count);
    }

    [Fact]
    public async Task Create_TooManyArtists_400()
    {
        var user = await _app.RegisterUserAsync();

        var many = Enumerable.Range(1, 21).Select(i => $"Writer {i}").ToArray();
        var response = await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Too Many Artists", null, null, null, null, null, null, GChart, null,
                null, many, null, null)));

        var (status, code, _, fields) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("validation-failed", code);
        Assert.True(fields.ContainsKey("artists"));
    }

    [Fact]
    public async Task Update_ArtistsList_ReplacesWholesale_AndRejoinsCache()
    {
        var user = await _app.RegisterUserAsync();
        var created = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(await user.Client.PostAsync("/api/songs",
            ApiTestFactory.Json(new CreateSongRequest("Replace Artists Song", "Ada Early, Ben Later",
                null, null, null, null, null, GChart, null))));

        var response = await user.Client.PutAsync($"/api/songs/{created.Id}", ApiTestFactory.Json(
            new UpdateSongRequest(null, null, null, null, null, null, null, null, null, null,
                ["Ada Early", "Cleo Now", ""], null, null)));
        Assert.Equal(200, (int)response.StatusCode);

        var dto = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(response);
        Assert.Equal(["Ada Early", "Cleo Now"], dto.Artists); // "" dropped, Ben gone
        Assert.Equal("Ada Early, Cleo Now", dto.Artist);
    }

    [Fact]
    public async Task Update_ArtistStringOnly_KeepsCacheInSync_TouchesNoLinks()
    {
        var user = await _app.RegisterUserAsync();
        var created = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(await user.Client.PostAsync("/api/songs",
            ApiTestFactory.Json(new CreateSongRequest("Flat Cache Song", "Zoe Zebra",
                null, null, null, null, null, GChart, null))));

        await user.Client.PutAsync($"/api/songs/{created.Id}", ApiTestFactory.Json(
            new UpdateSongRequest(null, "Zoe Zebra, Yvonne Yak", null, null, null, null, null, null, null)));

        var detail = await ReadDetailAsync(user.Client, created.Id);
        Assert.Equal("Zoe Zebra, Yvonne Yak", detail.Artist);
        // By design: a flat-string-only write syncs the cache, NOT the link tables —
        // the truth is edited through the artists list (the editor sends artists[]).
        Assert.Equal(["Zoe Zebra"], detail.Artists);
    }

    // ---------- filters ----------

    [Fact]
    public async Task Filter_ArtistMatchesAnyOfMany_MattRedmanFindsBuildMyLife()
    {
        var user = await _app.RegisterUserAsync();
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Any Match Song A", "Nina New, Matt Redman, Otis Old",
                null, null, null, null, null, GChart, null)));
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Any Match Song B", "Matt Redman",
                null, null, null, null, null, GChart, null)));

        var items = await ApiTestFactory.ReadJsonAsync<List<SongDto>>(
            await user.Client.GetAsync("/api/songs?artist=Matt Redman&pageSize=100"));

        Assert.Contains(items, s => s.Title == "Any Match Song A");
        Assert.Contains(items, s => s.Title == "Any Match Song B");
    }

    [Fact]
    public async Task Filter_GenreIsCaseInsensitiveAcrossSpellings()
    {
        var user = await _app.RegisterUserAsync();
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Case Genre Song", null, null, null, null, "pop", null, GChart, null)));
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Case Genre Song 2", null, null, null, null, null, null, GChart, null,
                null, null, ["Pop"], null)));

        var byLower = await ApiTestFactory.ReadJsonAsync<List<SongDto>>(
            await user.Client.GetAsync("/api/songs?genre=pop&pageSize=100"));
        Assert.Contains(byLower, s => s.Title == "Case Genre Song");
        Assert.Contains(byLower, s => s.Title == "Case Genre Song 2");

        var byMixed = await ApiTestFactory.ReadJsonAsync<List<SongDto>>(
            await user.Client.GetAsync("/api/songs?genre=POP&pageSize=100"));
        Assert.Contains(byMixed, s => s.Title == "Case Genre Song");
    }

    [Fact]
    public async Task Filter_TagRepeats_NarrowAndStyle()
    {
        var user = await _app.RegisterUserAsync();
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Both Tags Song", null, null, null, null, null, null, GChart, null,
                null, null, null, ["Sunday", "Closing"])));
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("One Tag Song", null, null, null, null, null, null, GChart, null,
                null, null, null, ["Sunday"])));

        var both = await ApiTestFactory.ReadJsonAsync<List<SongDto>>(
            await user.Client.GetAsync("/api/songs?tag=Sunday&tag=Closing&pageSize=100"));
        Assert.Contains(both, s => s.Title == "Both Tags Song");
        Assert.DoesNotContain(both, s => s.Title == "One Tag Song");

        var one = await ApiTestFactory.ReadJsonAsync<List<SongDto>>(
            await user.Client.GetAsync("/api/songs?tag=Sunday&pageSize=100"));
        Assert.Contains(one, s => s.Title == "Both Tags Song");
        Assert.Contains(one, s => s.Title == "One Tag Song");
    }

    [Fact]
    public async Task Filter_SearchFindsSongsByTagNames()
    {
        var user = await _app.RegisterUserAsync();
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Searchable Chart", "Quentin Quest", null, null, null, null, null, GChart, null,
                null, null, null, ["acoustic-set"])));

        var byArtistTag = await ApiTestFactory.ReadJsonAsync<List<SongDto>>(
            await user.Client.GetAsync("/api/songs?search=Quentin&pageSize=100"));
        Assert.Contains(byArtistTag, s => s.Title == "Searchable Chart");

        var byFreeTag = await ApiTestFactory.ReadJsonAsync<List<SongDto>>(
            await user.Client.GetAsync("/api/songs?search=acoustic&pageSize=100"));
        Assert.Contains(byFreeTag, s => s.Title == "Searchable Chart");
    }

    [Fact]
    public async Task Grouped_ListGroupsByTitle_EvenWithDifferentArtists()
    {
        var user = await _app.RegisterUserAsync();
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Shared Title Song", "Alpha One", null, null, null, null, null, GChart, null)));
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Shared Title Song", "Beta Two", null, null, null, null, null, GChart, null)));

        var groups = await ApiTestFactory.ReadJsonAsync<List<SongGroupDto>>(
            await user.Client.GetAsync("/api/songs?grouped=true&pageSize=100"));

        var group = Assert.Single(groups, g => g.Title == "Shared Title Song");
        Assert.Equal(2, group.VersionCount);
    }

    // ---------- facets + autocomplete ----------

    [Fact]
    public async Task Facets_ComeFromTagTables_TitleCasedGenres_TagList()
    {
        var user = await _app.RegisterUserAsync();
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Facet Source Song", "Fiona Field", null, null, null, "rock",
                null, GChart, null, null, null, null, ["facet-freetag"])));
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Facet Source Song 2", "Fiona Field", null, null, null, "Rock",
                null, GChart, null)));

        var facets = await ApiTestFactory.ReadJsonAsync<SongFacetsDto>(
            await user.Client.GetAsync("/api/songs/facets"));

        Assert.Contains("Fiona Field", facets.Artists);
        Assert.Contains("Rock", facets.Genres);
        Assert.DoesNotContain("rock", facets.Genres); // one canonical row
        Assert.Contains("facet-freetag", facets.Tags!);
    }

    [Fact]
    public async Task TagsEndpoint_CountsAndPrefixSearch()
    {
        var user = await _app.RegisterUserAsync();
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Autocomplete Song One", "Wally Weekday", null, null, null, null,
                null, GChart, null)));
        await user.Client.PostAsync("/api/songs", ApiTestFactory.Json(
            new CreateSongRequest("Autocomplete Song Two", "Wally Weekday", null, null, null, null,
                null, GChart, null)));

        var tags = await ApiTestFactory.ReadJsonAsync<List<TagDto>>(
            await user.Client.GetAsync("/api/tags?kind=artist&q=wally"));

        var wally = Assert.Single(tags, t => t.Name == "Wally Weekday");
        Assert.Equal("artist", wally.Kind);
        Assert.True(wally.SongCount >= 2);
    }

    // ---------- key derivation ----------

    [Fact]
    public async Task Create_NoKey_ChordDerivedKeyWins()
    {
        var user = await _app.RegisterUserAsync();

        var dto = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(await user.Client.PostAsync("/api/songs",
            ApiTestFactory.Json(new CreateSongRequest("Derived Key Song", null, null, null, null, null,
                null, GChart, null))));

        Assert.Equal("G", dto.Key);
        Assert.False(dto.KeyOverride);
        Assert.NotNull(dto.KeyConfidence);
    }

    [Fact]
    public async Task Create_HandSetKey_OverrideStopsReDerivation()
    {
        var user = await _app.RegisterUserAsync();

        var dto = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(await user.Client.PostAsync("/api/songs",
            ApiTestFactory.Json(new CreateSongRequest("Override Key Song", null, null, null, "Eb", null,
                null, GChart, null))));
        Assert.Equal("Eb", dto.Key);
        Assert.True(dto.KeyOverride);

        // A text change must NOT touch an override key...
        await user.Client.PutAsync($"/api/songs/{dto.Id}", ApiTestFactory.Json(
            new UpdateSongRequest(null, null, null, null, null, null, null, GChart + "\n", null)));
        var afterText = await ReadDetailAsync(user.Client, dto.Id);
        Assert.Equal("Eb", afterText.Key);
        Assert.True(afterText.KeyOverride);

        // ...and "" clears it — the chords take over immediately.
        var cleared = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(await user.Client.PutAsync(
            $"/api/songs/{dto.Id}", ApiTestFactory.Json(
                new UpdateSongRequest(null, null, null, null, "", null, null, null, null))));
        Assert.Equal("G", cleared.Key);
        Assert.False(cleared.KeyOverride);
    }

    [Fact]
    public async Task Update_TextChange_ReDerivesTheKey()
    {
        var user = await _app.RegisterUserAsync();
        var created = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(await user.Client.PostAsync("/api/songs",
            ApiTestFactory.Json(new CreateSongRequest("ReDerive Key Song", null, null, null, null, null,
                null, GChart, null))));
        Assert.Equal("G", created.Key);

        var cChart = """
            {start_of_verse}
            [C] [Am] [F] [G]
            [C] [Am] [F] [G]
            {end_of_chorus}
            """;
        var updated = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(await user.Client.PutAsync(
            $"/api/songs/{created.Id}", ApiTestFactory.Json(
                new UpdateSongRequest(null, null, null, null, null, null, null, cChart, null))));

        Assert.Equal("C", updated.Key);
    }

    [Fact]
    public async Task DetectKey_ReDerivesClearsOverride_NoChords422()
    {
        var user = await _app.RegisterUserAsync();
        var dto = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(await user.Client.PostAsync("/api/songs",
            ApiTestFactory.Json(new CreateSongRequest("Detect Key Song", null, null, null, "Eb", null,
                null, GChart, null))));

        var response = await user.Client.PostAsync($"/api/songs/{dto.Id}/detect-key", null);
        Assert.Equal(200, (int)response.StatusCode);
        var detected = await ApiTestFactory.ReadJsonAsync<DetectedKeyDto>(response);
        Assert.Equal("G", detected.Key);
        Assert.True(detected.Confidence > 0);

        var after = await ReadDetailAsync(user.Client, dto.Id);
        Assert.Equal("G", after.Key);
        Assert.False(after.KeyOverride);

        var lyricOnly = await ApiTestFactory.ReadJsonAsync<SongDetailDto>(await user.Client.PostAsync("/api/songs",
            ApiTestFactory.Json(new CreateSongRequest("No Chords Detect", null, null, null, "Eb", null,
                null, "{start_of_verse}\nJust words.\n{end_of_verse}", null))));
        var resp = await user.Client.PostAsync($"/api/songs/{lyricOnly.Id}/detect-key", null);
        Assert.Equal(422, (int)resp.StatusCode);
    }

    // ---------- playlist tags ----------

    [Fact]
    public async Task Playlist_TagsOnCreate_FilterByTag_ReplaceOnUpdate()
    {
        var user = await _app.RegisterUserAsync();

        var created = await ApiTestFactory.ReadJsonAsync<PlaylistDto>(await user.Client.PostAsync(
            "/api/playlists", ApiTestFactory.Json(
                new CreatePlaylistRequest("Tagged Setlist", null, null, null, null, ["Sunday 9 Nov", "kids4u"]))));
        Assert.Equal(["kids4u", "Sunday 9 Nov"], created.Tags); // alphabetical — setlist tag order carries no meaning

        var byTag = await ApiTestFactory.ReadJsonAsync<List<PlaylistDto>>(
            await user.Client.GetAsync("/api/playlists?tag=kids4u"));
        Assert.Contains(byTag, p => p.Name == "Tagged Setlist");

        var byUnknown = await ApiTestFactory.ReadJsonAsync<List<PlaylistDto>>(
            await user.Client.GetAsync("/api/playlists?tag=no-such-tag-xyz"));
        Assert.DoesNotContain(byUnknown, p => p.Name == "Tagged Setlist");

        var updated = await ApiTestFactory.ReadJsonAsync<PlaylistDto>(await user.Client.PutAsync(
            $"/api/playlists/{created.Id}", ApiTestFactory.Json(
                new UpdatePlaylistRequest(null, null, null, null, null, ["replaced"]))));
        Assert.Equal(["replaced"], updated.Tags);

        var detail = await ApiTestFactory.ReadJsonAsync<PlaylistDetailDto>(
            await user.Client.GetAsync($"/api/playlists/{created.Id}"));
        Assert.Equal(["replaced"], detail.Tags);
    }
}
