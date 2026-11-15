using System.Security.Claims;
using WebApi.Services;

namespace WebApi.Endpoints;

/// <summary>
/// ST-35 H2: in-app bug reports. POST /api/bug-reports appends a timestamped entry to
/// /data/bug_reports.md on the persistent volume — readable from the host, so it can be
/// fed straight back ("review the in-app bug list"). Each entry: "## [timestamp] user" + text.
/// ST-37 D1: GET /api/bug-reports lists entries (newest first) with a resolved status;
/// POST /api/bug-reports/{id}/resolve toggles it — persisted as an HTML comment marker in the file.
/// ST-39 D2: three statuses (new / resolved / waiting-for-a-question) + the app version a bug
/// was resolved in. Markers: "<!-- status: resolved in 0.3.0 -->", "<!-- status: waiting -->";
/// the legacy "<!-- resolved -->" marker still parses as resolved (no version). "new" = no marker.
/// ST-40 D1/D2: a short title per report ("<!-- title: ... -->") — set while processing, shown
/// collapsed in the UI; POST /api/bug-reports/{id} updates status and/or title in one call.
/// </summary>
public static class BugReportEndpoints
{
    private const string DataDir = "/data";
    private const string FilePath = "/data/bug_reports.md";

    /// <summary>ST-37 D1 legacy marker — still recognized as resolved (no version).</summary>
    private const string LegacyResolvedMarker = "<!-- resolved -->";

    /// <summary>ST-63 (finding 5): the message is the file's growth vector — bounded.</summary>
    internal const int MaxMessageLength = 20_000;

    /// <summary>Serializes appends — concurrent requests must not interleave file writes.</summary>
    private static readonly object FileLock = new();

    public record BugReportRequest(string? Message);
    public record BugReportStatusRequest(string Status, string? ResolvedIn);

    /// <summary>ST-40: update any combination of status / resolved-in / title on one report.</summary>
    public record BugReportUpdateRequest(string? Status, string? ResolvedIn, string? Title);

    private static bool IsValidStatus(string s) =>
        s is "new" or "resolved" or "waiting";

    public static RouteGroupBuilder MapBugReportEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/bug-reports", async (BugReportRequest request, HttpContext http) =>
        {
            var message = request.Message?.Trim();
            if (string.IsNullOrWhiteSpace(message))
                return Results.Json(
                    new { error = new { code = "validation-failed", message = "Message is required." } },
                    statusCode: StatusCodes.Status400BadRequest);

            // ST-63 (finding 5): bounded payload — the message is the file's growth vector.
            if (message.Length > MaxMessageLength)
                return Results.Json(
                    new { error = new { code = "validation-failed", message = $"Message is limited to {MaxMessageLength / 1000}k characters." } },
                    statusCode: StatusCodes.Status400BadRequest);

            // The JWT carries display name + email claims — use whichever identifies the reporter.
            var user = http.User.FindFirstValue("name") ?? http.User.FindFirstValue("email") ?? "unknown";

            await Task.Run(() =>
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(DataDir);
                    // ST-63 (finding 2): the entry id is an app-written marker, and the message is
                    // neutralized so report text can never forge an entry boundary ("## ...") or a
                    // status marker ("<!-- ... -->") — phantom entries used to shift every later
                    // positional id, so "resolve #7" edited the wrong report.
                    var entry = $"\n## [{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC] {user}\n\n<!-- id:{Guid.NewGuid():N} -->\n\n{SanitizeMessage(message)}\n";
                    if (!System.IO.File.Exists(FilePath))
                        System.IO.File.WriteAllText(FilePath, "# Bug reports" + entry);
                    else
                        System.IO.File.AppendAllText(FilePath, entry);
                }
            });

            return Results.Json(new { ok = true }, statusCode: StatusCodes.Status201Created);
        });

        // ST-37 D1 / ST-39 D2: list submitted bugs (newest first) with status + resolved-in version.
        api.MapGet("/bug-reports", () => Task.Run<IResult>(() =>
        {
            lock (FileLock)
            {
                if (!System.IO.File.Exists(FilePath))
                    return Results.Json(Array.Empty<object>());

                var entries = ParseEntries(System.IO.File.ReadAllText(FilePath));
                // Newest first for triage.
                entries.Reverse();
                return Results.Json(entries.Select(e => new
                {
                    id = e.id,
                    timestamp = e.timestamp,
                    user = e.user,
                    text = e.text,
                    status = e.status,
                    resolvedIn = e.resolvedIn,
                    title = e.title
                }));
            }
        }));

        // ST-39 D2: set an entry's status (persisted as a marker line in the file).
        api.MapPost("/bug-reports/{id}/status", async (string id, BugReportStatusRequest request, HttpContext http, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: triage belongs to the admin on an internet-facing instance —
            // any member closing reports silently retires the queue. Submitting a report
            // (POST /api/bug-reports) stays open: members reporting bugs IS the feature.
            if (await Authz.DenySharedDestroyAsync(http, policy, "triage bug reports") is { } denied)
                return denied;

            if (!IsValidStatus(request.Status))
                return Results.Json(
                    new { error = new { code = "validation-failed", message = "Status must be new, resolved or waiting." } },
                    statusCode: StatusCodes.Status400BadRequest);

            var resolvedIn = request.Status == "resolved" ? request.ResolvedIn?.Trim() : null;
            if (string.IsNullOrWhiteSpace(resolvedIn)) resolvedIn = null;

            var found = await Task.Run(() =>
            {
                lock (FileLock)
                {
                    if (!System.IO.File.Exists(FilePath)) return false;
                    var content = System.IO.File.ReadAllText(FilePath);
                    // ST-63: ids come from GET (stable guid ids, positional ids for old entries) —
                    // resolve to the entry position; unknown id → 404.
                    var index = ResolveEntryIndex(id, content);
                    if (index == 0) return false;
                    var (f, updated) = SetStatus(content, index, request.Status, resolvedIn);
                    if (f && updated is not null) System.IO.File.WriteAllText(FilePath, updated);
                    return f;
                }
            });

            if (!found)
                return Results.Json(
                    new { error = new { code = "not-found", message = "No bug report with that id." } },
                    statusCode: StatusCodes.Status404NotFound);

            // A no-op update (same status/version) is still a 200 — the entry exists.
            return Results.Json(new { ok = true, id, status = request.Status, resolvedIn });
        });

        // ST-40 D1/D2: update a report's status and/or title (persisted as marker lines).
        api.MapPost("/bug-reports/{id}", async (string id, BugReportUpdateRequest request, HttpContext http, IDeploymentPolicy policy) =>
        {
            // Public-demo gate 3: same triage gate as the status route.
            if (await Authz.DenySharedDestroyAsync(http, policy, "triage bug reports") is { } denied)
                return denied;

            if (!string.IsNullOrWhiteSpace(request.Status) && !IsValidStatus(request.Status!))
                return Results.Json(
                    new { error = new { code = "validation-failed", message = "Status must be new, resolved or waiting." } },
                    statusCode: StatusCodes.Status400BadRequest);

            var status = request.Status; // null = leave as-is
            var resolvedIn = status == "resolved" ? request.ResolvedIn?.Trim() : null;
            if (string.IsNullOrWhiteSpace(resolvedIn)) resolvedIn = null;
            // Title: null = leave as-is, "" = clear it.
            var title = request.Title?.Trim();

            var found = await Task.Run(() =>
            {
                lock (FileLock)
                {
                    if (!System.IO.File.Exists(FilePath)) return false;
                    var content = System.IO.File.ReadAllText(FilePath);
                    // ST-63: resolve guid/positional ids to the entry position (see status route).
                    var index = ResolveEntryIndex(id, content);
                    if (index == 0) return false;
                    var (f, updated) = SetFields(content, index, status, resolvedIn, title);
                    if (f && updated is not null) System.IO.File.WriteAllText(FilePath, updated);
                    return f;
                }
            });

            if (!found)
                return Results.Json(
                    new { error = new { code = "not-found", message = "No bug report with that id." } },
                    statusCode: StatusCodes.Status404NotFound);

            // A no-op update is still a 200 — the entry exists.
            return Results.Json(new { ok = true, id, status, resolvedIn, title });
        });

        return api;
    }

    internal record BugEntry(string id, string timestamp, string user, string text, string status, string? resolvedIn, string? title);

    /// <summary>True when the line is a marker (status — legacy or current — or title).</summary>
    internal static bool IsMarkerLine(string line)
    {
        var t = line.Trim();
        return t == LegacyResolvedMarker ||
               (t.StartsWith("<!-- status:", StringComparison.Ordinal) && t.EndsWith("-->", StringComparison.Ordinal)) ||
               (t.StartsWith("<!-- title:", StringComparison.Ordinal) && t.EndsWith("-->", StringComparison.Ordinal)) ||
               // ST-63: stable entry id, written by the app on append.
               (t.StartsWith("<!-- id:", StringComparison.Ordinal) && t.EndsWith("-->", StringComparison.Ordinal));
    }

    /// <summary>ST-63: extract the app-written stable id from a "&lt;!-- id:... --&gt;" marker.</summary>
    internal static string? ParseIdMarker(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("<!-- id:", StringComparison.Ordinal) || !t.EndsWith("-->", StringComparison.Ordinal))
            return null;
        var id = t["<!-- id:".Length..^"-->".Length].Trim();
        return id.Length > 0 ? id : null;
    }

    /// <summary>
    /// ST-63: resolve an id the client sent (a stable guid id from GET, or the legacy positional
    /// "bug-N") to the 1-based entry index SetStatus/SetFields expect; 0 when no entry matches.
    /// Stable ids make "resolve #7" address the report it names even after older entries are
    /// removed — the sanitizer already prevents forged entries from shifting the positions.
    /// </summary>
    internal static int ResolveEntryIndex(string id, string content)
    {
        var entries = ParseEntries(content);
        var i = entries.FindIndex(e => e.id == id);
        if (i >= 0)
            return i + 1;
        if (id.StartsWith("bug-", StringComparison.Ordinal)
            && int.TryParse(id["bug-".Length..], out int n) && n >= 1 && n <= entries.Count)
            return n;
        return 0;
    }

    /// <summary>
    /// ST-63 (finding 2): neutralize the two constructs the parser reads structurally —
    /// a line-leading "## " (entry boundary) and "&lt;!--" (marker open). Report text must
    /// round-trip as literal text; embedded markers/boundaries used to corrupt the store
    /// (phantom entries shifted every positional id; an embedded status marker self-resolved).
    /// </summary>
    internal static string SanitizeMessage(string message)
    {
        var lines = message.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("##", StringComparison.Ordinal))
                lines[i] = " " + lines[i]; // " ##" is not an entry boundary; displays nearly identical
        return string.Join('\n', lines).Replace("<!--", "< !--");
    }

    /// <summary>Extract the title from a "&lt;!-- title: ... --&gt;" marker; null when it is not one.</summary>
    internal static string? ParseTitleMarker(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("<!-- title:", StringComparison.Ordinal) || !t.EndsWith("-->", StringComparison.Ordinal))
            return null;
        var value = t["<!-- title:".Length..^3].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Extract (status, resolvedIn) from a marker line; ("", null) when it is not one.</summary>
    internal static (string Status, string? ResolvedIn) ParseMarker(string line)
    {
        var t = line.Trim();
        if (t == LegacyResolvedMarker) return ("resolved", null);
        if (!t.StartsWith("<!-- status:", StringComparison.Ordinal) || !t.EndsWith("-->", StringComparison.Ordinal))
            return ("", null);

        var inner = t["<!-- status:".Length..^3].Trim();
        // "resolved in 0.3.0" → (resolved, 0.3.0); "waiting" / "new" → as-is.
        var inIdx = inner.IndexOf(" in ", StringComparison.Ordinal);
        return inIdx > 0 ? (inner[..inIdx], inner[(inIdx + 4)..].Trim()) : (inner, null);
    }

    /// <summary>Parse "## [timestamp] user" blocks into entries (1-based ids — stable: entries are only appended).</summary>
    internal static List<BugEntry> ParseEntries(string content)
    {
        var entries = new List<BugEntry>();
        var lines = content.Split('\n');

        var starts = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("## ", StringComparison.Ordinal)) starts.Add(i);

        for (var n = 0; n < starts.Count; n++)
        {
            var start = starts[n];
            var end = n + 1 < starts.Count ? starts[n + 1] : lines.Length;

            // Heading: "[2026-09-30 17:48 UTC] James Taylor"
            var heading = lines[start]["## ".Length..].Trim();
            string timestamp = "", user;
            if (heading.StartsWith('['))
            {
                var close = heading.IndexOf(']');
                if (close > 0)
                {
                    timestamp = heading[1..close].Trim();
                    user = heading[(close + 1)..].Trim();
                }
                else user = heading;
            }
            else user = heading;

            var bodyLines = lines[(start + 1)..end];
            string status = "new";
            string? resolvedIn = null;
            string? title = null;
            string? stableId = null;
            foreach (var l in bodyLines)
            {
                if (!IsMarkerLine(l)) continue;
                var idm = ParseIdMarker(l);
                if (idm is not null) { stableId ??= idm; continue; } // ST-63: app-written stable id
                var t = ParseTitleMarker(l);
                if (t is not null) { title ??= t; continue; } // first title wins
                (status, resolvedIn) = ParseMarker(l);
                break; // first status marker wins
            }
            var text = string.Join('\n', bodyLines.Where(l => !IsMarkerLine(l))).Trim();

            // ST-63: stable ids for app-written entries; entries predating ST-63 keep their
            // positional id (the sanitizer prevents new phantom entries, so it stays stable).
            entries.Add(new BugEntry(stableId ?? $"bug-{n + 1}", timestamp, user, text, status, resolvedIn, title));
        }
        return entries;
    }

    /// <summary>Set entry #index (1-based) to the given status.</summary>
    private static (bool Found, string? Updated) SetStatus(string content, int index, string status, string? resolvedIn)
        => SetFields(content, index, status, resolvedIn, title: null);

    /// <summary>
    /// Update entry #index (1-based): any of status / resolved-in / title; a null argument leaves
    /// that field as-is. Markers are rewritten in place (title first, then status).
    /// Returns (found, updated) — updated is null when nothing changed.
    /// </summary>
    internal static (bool Found, string? Updated) SetFields(string content, int index, string? status, string? resolvedIn, string? title)
    {
        var lines = content.Split('\n');

        var starts = new List<int>();
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("## ", StringComparison.Ordinal)) starts.Add(i);

        if (index < 1 || index > starts.Count) return (false, null);

        var start = starts[index - 1];
        var end = index < starts.Count ? starts[index] : lines.Length;

        var block = lines[start..end].ToList();

        // Current marker state, so fields we don't touch are preserved.
        string curStatus = "new";
        string? curResolvedIn = null;
        string? curTitle = null;
        string? curId = null;
        foreach (var l in block)
        {
            if (!IsMarkerLine(l)) continue;
            var idm = ParseIdMarker(l);
            if (idm is not null) { curId ??= idm; continue; } // ST-63: stable id survives rewrites
            var t = ParseTitleMarker(l);
            if (t is not null) { curTitle ??= t; continue; }
            (curStatus, curResolvedIn) = ParseMarker(l);
            break; // first status marker wins
        }

        var effStatus = status ?? curStatus;
        var effResolvedIn = status != null ? resolvedIn : curResolvedIn;
        if (effStatus != "resolved") effResolvedIn = null;
        var effTitle = title is null ? curTitle : SanitizeMarkerText(title);

        // Drop any existing markers (legacy + current), then re-add the effective ones —
        // including the stable id, which IsMarkerLine now recognizes.
        block.RemoveAll(IsMarkerLine);
        var markers = new List<string>();
        if (curId is not null) markers.Add($"<!-- id:{curId} -->");
        if (!string.IsNullOrWhiteSpace(effTitle)) markers.Add($"<!-- title: {effTitle} -->");
        if (effStatus != "new")
            markers.Add(effStatus == "resolved" && effResolvedIn is not null
                ? $"<!-- status: resolved in {effResolvedIn} -->"
                : $"<!-- status: {effStatus} -->");

        if (markers.Count > 0)
        {
            // Insert after the last non-blank line of the body.
            var insertAt = block.Count - 1;
            while (insertAt > 0 && string.IsNullOrWhiteSpace(block[insertAt])) insertAt--;
            for (var i = markers.Count - 1; i >= 0; i--) block.Insert(insertAt + 1, markers[i]);
        }

        var result = new List<string>(lines.Length);
        result.AddRange(lines.Take(start));
        result.AddRange(block);
        result.AddRange(lines.Skip(end));
        var updated = string.Join('\n', result);
        return (true, updated == content ? null : updated);
    }

    /// <summary>A marker lives on one line — flatten newlines and strip the sequence that would close it early.</summary>
    internal static string SanitizeMarkerText(string s) =>
        s.Replace('\r', ' ').Replace('\n', ' ').Replace("-->", "").Trim();
}
