using System.Net.Http.Headers;
using WebApi.Dtos;

namespace WebApi.Tests;

/// <summary>
/// ST-82: the /api/songs/{id}/source-files and /api/source-files endpoints (ST-46) —
/// multipart upload (pdf/png/jpg only — extension is the source of truth; 10 MB cap, 10
/// files per song), the metadata list (oldest first), raw-bytes download served inline
/// (nosniff, no Content-Disposition), and delete. 404 for missing songs/files, auth required.
/// Runs in the shared ST-82 collection store: usernames are unique per test.
/// </summary>
[Collection("ST-82 endpoint app")]
public class SongSourceFileTests
{
    private const int MaxSizeBytes = 10 * 1024 * 1024;
    private readonly ApiTestFactory _app;

    public SongSourceFileTests(ApiTestFactory app) => _app = app;

    private HttpClient Anonymous => _app.CreateClient();

    private static MultipartFormDataContent FilePart(string fileName, byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent();
        form.Add(part, "file", fileName);
        return form;
    }

    // ---------- POST /api/songs/{id}/source-files ----------

    [Fact]
    public async Task Upload_Pdf_201_MetadataMatches_ListShowsIt()
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync("Amazing Grace");
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // "%PDF"

        var response = await user.Client.PostAsync($"/api/songs/{song.Id}/source-files",
            FilePart("chart.pdf", bytes));

        Assert.Equal(201, (int)response.StatusCode);
        var dto = await ApiTestFactory.ReadJsonAsync<SongSourceFileDto>(response);
        Assert.Equal(song.Id, dto.SongId);
        Assert.Equal("chart.pdf", dto.FileName);
        Assert.Equal("application/pdf", dto.ContentType);
        Assert.Equal(bytes.Length, dto.SizeBytes);

        var list = await ApiTestFactory.ReadJsonAsync<List<SongSourceFileDto>>(
            await user.Client.GetAsync($"/api/songs/{song.Id}/source-files"));
        Assert.Equal(dto.Id, Assert.Single(list).Id);
    }

    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".PDF", "application/pdf")] // allow-list matching is case-insensitive
    public async Task Upload_AllowedExtensions_MapToCanonicalTypes(string ext, string contentType)
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync();

        var response = await user.Client.PostAsync($"/api/songs/{song.Id}/source-files",
            FilePart($"scan{ext}", [1, 2, 3]));

        Assert.Equal(201, (int)response.StatusCode);
        Assert.Equal(contentType, (await ApiTestFactory.ReadJsonAsync<SongSourceFileDto>(response)).ContentType);
    }

    [Theory]
    [InlineData("chart.cho")]   // ChordPro text is a song, never an attachment
    [InlineData("notes.txt")]
    [InlineData("chart.svg")]   // script-capable
    public async Task Upload_DisallowedExtension_400(string fileName)
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync();

        var response = await user.Client.PostAsync($"/api/songs/{song.Id}/source-files",
            FilePart(fileName, [1, 2, 3]));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("unsupported-type", code);
    }

    [Fact]
    public async Task Upload_MissingSong_404()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.PostAsync($"/api/songs/{Guid.NewGuid()}/source-files",
            FilePart("chart.pdf", [1, 2, 3]));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }

    [Fact]
    public async Task Upload_ExceedsTenFilesPerSong_400()
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync();
        await _app.SeedSourceFilesAsync(song.Id, 10);

        var response = await user.Client.PostAsync($"/api/songs/{song.Id}/source-files",
            FilePart("chart.pdf", [1, 2, 3]));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("too-many-files", code);
    }

    [Fact]
    public async Task Upload_OverTenMegabytes_400()
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync();

        var response = await user.Client.PostAsync($"/api/songs/{song.Id}/source-files",
            FilePart("huge.pdf", new byte[MaxSizeBytes + 1]));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(400, status);
        Assert.Equal("too-large", code);
    }

    [Fact]
    public async Task Upload_Anonymous_Unauthorized()
    {
        var song = await _app.SeedSongAsync();

        var response = await Anonymous.PostAsync($"/api/songs/{song.Id}/source-files",
            FilePart("chart.pdf", [1, 2, 3]));

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    // ---------- GET /api/songs/{id}/source-files ----------

    [Fact]
    public async Task List_MissingSong_404()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.GetAsync($"/api/songs/{Guid.NewGuid()}/source-files");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }

    [Fact]
    public async Task List_SongWithNoFiles_ReturnsEmptyList()
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync();

        var response = await user.Client.GetAsync($"/api/songs/{song.Id}/source-files");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Empty(await ApiTestFactory.ReadJsonAsync<List<SongSourceFileDto>>(response));
    }

    // ---------- GET /api/source-files/{id} ----------

    [Fact]
    public async Task Download_ReturnsExactBytes_InlineWithNosniff()
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync();
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };
        var upload = await user.Client.PostAsync($"/api/songs/{song.Id}/source-files",
            FilePart("chart.pdf", bytes));
        var uploaded = await ApiTestFactory.ReadJsonAsync<SongSourceFileDto>(upload);

        var response = await user.Client.GetAsync($"/api/source-files/{uploaded.Id}");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        // ST-65: the browser must not sniff past the allow-listed type; no
        // Content-Disposition → the PDF/image renders inline.
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task Download_MissingId_404()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.GetAsync($"/api/source-files/{Guid.NewGuid()}");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }

    [Fact]
    public async Task Download_Anonymous_Unauthorized()
    {
        var response = await Anonymous.GetAsync($"/api/source-files/{Guid.NewGuid()}");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(401, status);
        Assert.Equal("unauthenticated", code);
    }

    // ---------- DELETE /api/source-files/{id} ----------

    [Fact]
    public async Task Delete_204_DownloadGoneAfterwards()
    {
        var user = await _app.RegisterUserAsync();
        var song = await _app.SeedSongAsync();
        var upload = await user.Client.PostAsync($"/api/songs/{song.Id}/source-files",
            FilePart("chart.pdf", [1, 2, 3]));
        var uploaded = await ApiTestFactory.ReadJsonAsync<SongSourceFileDto>(upload);

        var delete = await user.Client.DeleteAsync($"/api/source-files/{uploaded.Id}");
        Assert.Equal(204, (int)delete.StatusCode);

        var download = await user.Client.GetAsync($"/api/source-files/{uploaded.Id}");
        Assert.Equal(404, (int)download.StatusCode);
    }

    [Fact]
    public async Task Delete_MissingId_404()
    {
        var user = await _app.RegisterUserAsync();

        var response = await user.Client.DeleteAsync($"/api/source-files/{Guid.NewGuid()}");

        var (status, code, _, _) = await ApiTestFactory.ReadErrorAsync(response);
        Assert.Equal(404, status);
        Assert.Equal("not-found", code);
    }
}
