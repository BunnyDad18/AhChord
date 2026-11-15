namespace WebApi.Endpoints;

/// <summary>
/// MT-02: Android APK download — the in-app "download the app" link (Settings) and the LAN
/// install path for phones. Serves the most recently published .apk from /data/apk on the
/// persistent volume, so a new build is installable over the network with zero extra steps:
/// copy the apk into the volume, done.
///
///   GET /api/app/download  → application/vnd.android.package-archive (404 if none published)
///   GET /api/app/version   → { versionCode, versionName, sizeBytes, publishedAt } (in-app update check)
///
/// Anonymous (like /info and /health): a phone browser opens this URL directly with no session,
/// and an APK for a self-hosted instance is not sensitive — the app itself requires login.
///
/// MT-01 (in-app updates): publishing an update is copying the new apk into /data/apk (the
/// download endpoint always serves the newest) — the installed shell polls /app/version
/// (native check in MainActivity) and offers the user an install when versionCode advances.
/// The version numbers come from a sidecar "<apk>.version.json" written by Mobile/publish-apk.sh;
/// without one, versionName falls back to the apk file name and versionCode is null (no update).
/// </summary>
public static class AppDownloadEndpoints
{
    private const string ApkDir = "/data/apk";

    public static RouteGroupBuilder MapAppDownloadEndpoints(this RouteGroupBuilder api)
    {
        // MapMethods (not MapGet) so HEAD works too — otherwise it falls through to the SPA fallback.
        // The Settings UI probes with HEAD to show build date/size and hide itself when 404.
        api.MapMethods("/app/download", new[] { HttpMethods.Get, HttpMethods.Head }, () =>
        {
            var apk = FindLatestApk();
            return apk is null
                ? Results.Json(new { error = new { code = "no-app-build" } }, statusCode: StatusCodes.Status404NotFound)
                : Results.File(apk, "application/vnd.android.package-archive", Path.GetFileName(apk));
        }).AllowAnonymous();

        // MT-01 (in-app updates): the APK shell checks this on startup. A sidecar file
        // "<apk>.version.json" ({"versionCode":2,"versionName":"0.3.52"}) written next to the
        // apk by Mobile/publish-apk.sh carries the real version; without it the shell sees
        // versionCode null (no update) and the UI falls back to the file name for a label.
        api.MapGet("/app/version", () =>
        {
            var apk = FindLatestApk();
            if (apk is null)
                return Results.Json(new { error = new { code = "no-app-build" } }, statusCode: StatusCodes.Status404NotFound);

            var fi = new FileInfo(apk);
            var (code, name) = ReadSidecar(apk);
            return Results.Json(new
            {
                versionCode = code,
                versionName = name ?? VersionNameFromFileName(apk),
                sizeBytes = fi.Length,
                publishedAt = fi.LastWriteTimeUtc
            });
        }).AllowAnonymous();

        return api;
    }

    /// <summary>Read the sidecar next to <paramref name="apkPath"/>; (null, null) when absent/bad.</summary>
    internal static (int? Code, string? Name) ReadSidecar(string apkPath)
    {
        try
        {
            var sidecar = apkPath + ".version.json";
            if (!File.Exists(sidecar))
                return (null, null);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sidecar));
            var root = doc.RootElement;
            // Kind-check before TryGetInt32 — it throws on a string token, and the catch
            // would drop the whole sidecar (a string code should just be ignored).
            int? code = root.TryGetProperty("versionCode", out var c)
                && c.ValueKind == System.Text.Json.JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : null;
            string? name = root.TryGetProperty("versionName", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
                ? n.GetString() : null;
            return (code, name);
        }
        catch
        {
            return (null, null); // a broken sidecar must never break the download endpoint
        }
    }

    /// <summary>"ahchord-0.3.52.apk" → "0.3.52"; null when the name carries no version.</summary>
    internal static string? VersionNameFromFileName(string apkPath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            Path.GetFileName(apkPath), @"(\d+\.\d+(?:\.\d+)?)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The most recently modified .apk in /data/apk, or null if the dir is missing/empty.</summary>
    private static string? FindLatestApk()
    {
        if (!Directory.Exists(ApkDir))
            return null;
        return Directory.EnumerateFiles(ApkDir, "*.apk")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
