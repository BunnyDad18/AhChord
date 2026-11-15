using Hangfire.Dashboard;

namespace WebApi.Services;

/// <summary>
/// ST-14/ST-61: Hangfire dashboard authorization — **admin only**, checked against the
/// database (Authz.IsAdminAsync), not the frozen <c>is_admin</c> claim. The dashboard can
/// requeue/delete jobs and shows job parameters (user ids, draft ids) — every band member
/// with a valid token must not have that. The filter interface is synchronous; the check
/// is a single indexed row lookup, so the sync-over-async wait is bounded.
/// Denials get the standard JSON challenge (the app is JWT-only, no cookies).
/// </summary>
public sealed class JwtDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        if (http.User.Identity?.IsAuthenticated != true)
            return false;
        return Authz.IsAdminAsync(http).GetAwaiter().GetResult();
    }
}
