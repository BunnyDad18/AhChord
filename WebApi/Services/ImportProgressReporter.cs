using Microsoft.AspNetCore.SignalR;
using WebApi.Hubs;

namespace WebApi.Services;

/// <summary>
/// ST-33: pushes live chord-page→ChordPro conversion progress to the requester's open clients.
/// The job calls this at status transitions and for every streamed LLM chunk; the
/// implementation fans out over SignalR so any tab of the user sees it in real time.
/// </summary>
public interface IImportProgressReporter
{
    /// <summary>Status transition (running/succeeded/failed) with counters. <paramref name="attempt"/> rises on retry.</summary>
    Task ReportStatusAsync(Guid draftId, string? userId, string status, int chars, TimeSpan elapsed, int attempt);

    /// <summary>A chunk of streamed LLM output — the client appends it to its preview buffer.</summary>
    Task ReportChunkAsync(Guid draftId, string? userId, string text);
}

/// <summary>
/// SignalR implementation: per-user events on the shared session hub connection
/// (Clients.User — requires SubClaimUserIdProvider so "sub" resolves as the user id).
/// Sending to a user with no open connections is a no-op, never an error.
/// </summary>
public sealed class SignalRImportProgressReporter(IHubContext<SessionHub> hub) : IImportProgressReporter
{
    public Task ReportStatusAsync(Guid draftId, string? userId, string status, int chars, TimeSpan elapsed, int attempt)
        => SendToUser(userId, "conversionProgress", new
        {
            id = draftId,
            status,
            chars,
            elapsedSeconds = (int)elapsed.TotalSeconds,
            attempt
        });

    public Task ReportChunkAsync(Guid draftId, string? userId, string text)
        => SendToUser(userId, "conversionChunk", new { id = draftId, text });

    private Task SendToUser(string? userId, string method, object payload)
        => userId is null ? Task.CompletedTask : hub.Clients.User(userId).SendAsync(method, payload);
}
