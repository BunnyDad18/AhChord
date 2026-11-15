using WebApi.Endpoints;

namespace WebApi.Tests;

/// <summary>
/// MT-01 (in-app updates): the APK shell compares its own versionCode against the server's
/// latest build. Version numbers come from a sidecar "<apk>.version.json" published next to
/// the apk by Mobile/publish-apk.sh; when it's absent the versionName falls back to the file
/// name and the shell sees no update.
/// </summary>
public class AppDownloadEndpointsTests
{
    private static string TempApk(string name, string? sidecarJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ahchord-apk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var apk = Path.Combine(dir, name);
        File.WriteAllText(apk, "not-a-real-apk");
        if (sidecarJson is not null)
            File.WriteAllText(apk + ".version.json", sidecarJson);
        return apk;
    }

    [Fact]
    public void Sidecar_ProvidesCodeAndName()
    {
        var apk = TempApk("ahchord-0.3.53.apk", """{"versionCode":3,"versionName":"0.3.53"}""");

        var (code, name) = AppDownloadEndpoints.ReadSidecar(apk);

        Assert.Equal(3, code);
        Assert.Equal("0.3.53", name);
    }

    [Fact]
    public void Sidecar_Absent_NullNull()
    {
        var apk = TempApk("ahchord-0.3.53.apk", sidecarJson: null);

        var (code, name) = AppDownloadEndpoints.ReadSidecar(apk);

        Assert.Null(code);
        Assert.Null(name);
    }

    [Fact]
    public void Sidecar_BrokenJson_NullNull_NotThrowing()
    {
        var apk = TempApk("ahchord-0.3.53.apk", "{ this is not json");

        var (code, name) = AppDownloadEndpoints.ReadSidecar(apk);

        Assert.Null(code);
        Assert.Null(name);
    }

    [Fact]
    public void Sidecar_StringCode_Ignored()
    {
        // A non-numeric versionCode must not count as an update signal (the shell reads an int).
        var apk = TempApk("ahchord-0.3.53.apk", """{"versionCode":"3","versionName":"0.3.53"}""");

        var (code, name) = AppDownloadEndpoints.ReadSidecar(apk);

        Assert.Null(code);
        Assert.Equal("0.3.53", name);
    }

    [Theory]
    [InlineData("ahchord-0.3.53.apk", "0.3.53")]
    [InlineData("ahchord-android.apk", null)]          // legacy spike build: no version in the name
    [InlineData("ahchord-v2.apk", null)]               // a bare digit is not a version
    [InlineData("ahchord-1.2.apk", "1.2")]
    public void VersionNameFromFileName(string file, string? expected)
        => Assert.Equal(expected, AppDownloadEndpoints.VersionNameFromFileName(file));
}
