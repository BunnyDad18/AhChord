using WebApi.Endpoints;

namespace WebApi.Tests;

public class InfoEndpointsTests
{
    // --- changelog resolution (mirrors ChordProConversionJob.ResolvePromptFile) -------

    [Fact]
    public void ResolveChangelogFile_FindsRepoLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sfm-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "CHANGELOG.md");
        File.WriteAllText(file, "# Changelog\n");

        try
        {
            // Dev layout: the app runs from WebApi/bin/Debug/net10.0 and walks up to the repo root.
            var baseDir = Path.Combine(root, "WebApi", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(baseDir);
            Assert.Equal(file, InfoEndpoints.ResolveChangelogFile(baseDirectory: baseDir));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveChangelogFile_PrefersContainerLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sfm-app-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        // Container layout: the Dockerfile copies CHANGELOG.md next to WebApi.dll.
        var containerFile = Path.Combine(root, "changelog.md");
        File.WriteAllText(containerFile, "# Changelog (container)\n");

        try
        {
            Assert.Equal(containerFile, InfoEndpoints.ResolveChangelogFile(baseDirectory: root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveChangelogFile_Missing_ReturnsNull()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"sfm-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);

        try
        {
            Assert.Null(InfoEndpoints.ResolveChangelogFile(baseDirectory: baseDir));
        }
        finally
        {
            Directory.Delete(baseDir, true);
        }
    }

    // --- version normalization -------------------------------------------------

    [Theory]
    [InlineData("0.1.0+abc123", "0.1.0")] // SDK build metadata stripped
    [InlineData("0.1.0", "0.1.0")]        // already clean
    [InlineData(null, "0.0.0")]           // fallback when no version is present
    public static void NormalizeVersion_StripsBuildMetadata(string? input, string expected)
        => Assert.Equal(expected, InfoEndpoints.NormalizeVersion(input));

    [Fact]
    public void AppVersion_IsClean_NoBuildMetadata()
    {
        var v = InfoEndpoints.AppVersion();
        Assert.False(string.IsNullOrWhiteSpace(v));
        Assert.DoesNotContain("+", v);
    }
}
