using System.Reflection;

namespace WebApi.Endpoints;

/// <summary>
/// ST-31: app info — version + changelog, shown in Settings → About.
///
///   GET /api/info  → { service, version, changelog }
///
/// Anonymous (like /health): the version and changelog are non-sensitive, and the endpoint is
/// only surfaced on the authenticated Settings page anyway. The changelog is the raw CHANGELOG.md
/// text; the frontend renders it as Markdown.
/// </summary>
public static class InfoEndpoints
{
    public static RouteGroupBuilder MapInfoEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/info", () =>
            Results.Ok(new
            {
                service = "ahchord-webapi",
                version = AppVersion(),
                changelog = ReadChangelog()
            })).AllowAnonymous();

        return api;
    }

    /// <summary>
    /// The user-facing version string, shared by /api/info and /api/health so both agree. Prefers the
    /// informational version (the release version set at build time — ST-127, /p:ReleaseVersion)
    /// over the 4-part assembly version ("0.5.0.0").
    /// </summary>
    internal static string AppVersion()
    {
        var asm = typeof(InfoEndpoints).Assembly;
        return NormalizeVersion(
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString());
    }

    /// <summary>Strips the SDK's "+&lt;gitsha&gt;" build metadata so users see e.g. "0.1.28", not a hash.</summary>
    internal static string NormalizeVersion(string? raw) => raw is null ? "0.0.0" : raw.Split('+', 2)[0];

    /// <summary>The raw CHANGELOG.md text, or an empty string if the file cannot be found.</summary>
    private static string ReadChangelog()
    {
        var path = ResolveChangelogFile();
        return path is null ? "" : File.ReadAllText(path);
    }

    /// <summary>
    /// Locate CHANGELOG.md. Candidates, in order: {baseDir}/changelog.md (container layout — the
    /// Dockerfile copies it next to WebApi.dll), then a walk up from the base directory looking for
    /// the repo layout {dir}/CHANGELOG.md (dev). Mirrors ChordProConversionJob.ResolvePromptFile.
    /// </summary>
    public static string? ResolveChangelogFile(string? baseDirectory = null)
    {
        var baseDir = baseDirectory ?? AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "changelog.md"),
            Path.Combine(baseDir, "CHANGELOG.md")
        };
        for (var dir = Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar));
             dir is not null && dir != Path.GetPathRoot(dir);
             dir = Path.GetDirectoryName(dir))
        {
            candidates.Add(Path.Combine(dir, "CHANGELOG.md"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
