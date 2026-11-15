using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.RateLimiting;
using Hangfire;
using Microsoft.AspNetCore.HttpOverrides;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using WebApi.Data;
using WebApi.Endpoints;
using WebApi.Identity;
using WebApi.Services;

var builder = WebApplication.CreateBuilder(args);

// ST-63 (finding 4): global request-body ceiling. The source-file upload raises its own
// [RequestSizeLimit] (11 MB); everything else has no business sending multi-MB bodies, and
// an uncapped POST lets a client make the server spool arbitrary bytes.
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 16 * 1024 * 1024);

// JWT: resolve the signing key once. Fail fast outside Development if it is missing or too short.
var jwtSecret = builder.Configuration["JWT_SECRET"];
if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < 32)
{
    if (builder.Environment.IsDevelopment())
    {
        jwtSecret = "dev-only-insecure-jwt-secret-do-not-use-in-production-0123456789";
        Console.WriteLine("Warning: JWT_SECRET is unset; using an insecure Development-only signing key. Set JWT_SECRET for anything real.");
    }
    else
    {
        throw new InvalidOperationException(
            "JWT_SECRET must be set to a string of at least 32 characters. Generate one with: openssl rand -hex 32");
    }
}
// ST-35 A2: 30-day tokens by default (43200 min) so users aren't re-logging in every
// visit; the token is persisted client-side and can always be revoked by signing out.
var jwtExpiryMinutes = builder.Configuration.GetValue("JWT_EXPIRY_MINUTES", 43200);

// Add services to the container.
builder.Services.AddOpenApi(options =>
{
    // Advertise the bearer scheme so Scalar's "Authorize" button can attach a token.
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Components ??= new OpenApiComponents();
        var components = document.Components;
        components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        var bearer = new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "JWT from POST /api/auth/login (or /api/auth/register)."
        };
        components.SecuritySchemes["bearerAuth"] = bearer;
        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(new OpenApiSecurityRequirement
        {
            { new OpenApiSecuritySchemeReference("bearerAuth", document, ""), new List<string>() }
        });
        return Task.CompletedTask;
    });
});
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton(new JwtTokenService(jwtSecret, jwtExpiryMinutes));

// MusicBrainz (ST-12): release-search proxy with a client-side rate limit (1 request / 1.5s).
// Base URL, timeout and User-Agent are configurable; defaults satisfy MusicBrainz's API policy.
builder.Services.AddHttpClient("MusicBrainz", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration.GetValue<string>("MusicBrainz:BaseUrl") ?? "https://musicbrainz.org/ws/2/");
    client.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("MusicBrainz:TimeoutSeconds", 10));
    var userAgent = builder.Configuration.GetValue<string>("MusicBrainz:UserAgent")
        ?? "AhChord/1.0 (self-hosted chord app)";
    client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
});
// Singleton wrapper: the rate limiter's queue must be shared across all requests.
// (AddHttpClient<T> would hand out a fresh — and unthrottled — client per resolution.)
builder.Services.AddSingleton<MusicBrainzClient>(sp =>
    new MusicBrainzClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("MusicBrainz")));

// Songsterr (ST-44): song-search proxy with a light client-side throttle (1 request / 0.5s).
builder.Services.AddHttpClient("Songsterr", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration.GetValue<string>("Songsterr:BaseUrl") ?? "https://www.songsterr.com/");
    client.Timeout = TimeSpan.FromSeconds(
        builder.Configuration.GetValue("Songsterr:TimeoutSeconds", 10));
});
// Singleton wrapper: the throttle queue must be shared across all requests.
builder.Services.AddSingleton<SongsterrClient>(sp =>
    new SongsterrClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("Songsterr")));

// AddIdentityCore (NOT Microsoft.AspNetCore.Identity's AddIdentity, which force-registers
// cookie authentication). We authenticate with JWT bearer only.
builder.Services
    .AddIdentityCore<ApplicationUser>(options =>
    {
        // Locked decision: password >= 8 chars, no other complexity rules.
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        // Self-hosted MVP: no account lockout — login checks call CheckPasswordAsync, which never locks.
        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero // an expired token is expired, no grace period
        };
        options.Events = new JwtBearerEvents
        {
            // Public-demo gate 4: a stateless 30-day JWT has no revocation at all — changing a
            // password, rotating the stamp or (for the admin) removing an account leaves the
            // issued token valid for weeks. The token now carries the Identity security stamp
            // ("sstamp"), and the row is read per request: a stamp mismatch — or no stamp claim
            // at all (a token issued before this control existed) — is a 401.
            //
            // OFF by default (JWT_STAMP_CHECK=true opts in), because every other gate's default
            // is today's LAN behaviour and this one changes it visibly: turning it on ends every
            // token issued before it, i.e. every device signs in again once, and a password
            // change signs out all devices. Set it true on an internet-facing demo (SECURITY.md).
            // The lookup is one indexed read on the path Authz.IsAdminAsync already walks.
            OnTokenValidated = async context =>
            {
                var services = context.HttpContext.RequestServices;
                if (services.GetService<IDeploymentPolicy>() is not { JwtStampCheck: true })
                    return;

                var sub = Authz.Subject(context.Principal);
                if (string.IsNullOrEmpty(sub))
                {
                    context.Fail("Token has no subject.");
                    return;
                }

                var stamp = context.Principal?.FindFirst("sstamp")?.Value;
                string? current;
                try
                {
                    current = await services.GetRequiredService<ApplicationDbContext>()
                        .Users.Where(u => u.Id == sub)
                        .Select(u => u.SecurityStamp)
                        .FirstOrDefaultAsync(context.HttpContext.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The stamp check runs inside the authentication handler, so an exception
                    // here escapes it: an unhandled one is a 500 with an EMPTY body (the API's
                    // JSON-shaping middleware is registered later), on every token-carrying
                    // request — including the anonymous-by-design surfaces, because the SPA
                    // attaches the token everywhere. A database that is down is a 401, logged
                    // once, and /api/health (no token) keeps reporting the real 503.
                    services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Auth.StampCheck")
                        .LogWarning(ex, "Security-stamp lookup failed for account {Sub}.", sub);
                    context.Fail("Could not validate the token.");
                    return;
                }

                if (current is null)
                {
                    context.Fail("Account no longer exists.");
                    return;
                }
                if (!string.Equals(current, stamp, StringComparison.Ordinal))
                {
                    context.Fail("Token was issued before the credentials changed.");
                }
            },
            // ST-25: browser WebSockets cannot set an Authorization header, so the
            // SignalR JS client sends the JWT as ?access_token= (per the SignalR
            // handshake spec). ST-61: accept it ONLY on hub paths — anywhere else the
            // 30-day token leaks into access logs, browser history and Referer headers.
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token)
                    && Authz.AllowsQueryStringToken(context.Request.Path.Value)
                    && context.HttpContext.Request.Query.TryGetValue("access_token", out var queryToken))
                {
                    context.Token = queryToken.ToString();
                }
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                return context.Response.WriteAsJsonAsync(new { error = new { code = "unauthenticated", message = "Authentication required." } });
            },
            OnForbidden = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";
                return context.Response.WriteAsJsonAsync(new { error = new { code = "forbidden", message = "Access denied." } });
            }
        };
    });
builder.Services.AddAuthorization();

// Public-demo gate 2: the review found no rate limiting anywhere — a login endpoint that
// answers deterministic 409s/401s and never throttles is an enumeration and password-stuffing
// target, and every conversion queues work on one GPU. Fixed windows, partitioned per USER
// when a token is present and per CLIENT ADDRESS for the anonymous auth endpoints (login and
// register carry no token, which is exactly the brute-force surface). Budgets are generous on
// purpose: a band never trips them, a script does. Tunable per deployment (RATE_LIMIT_*).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (ctx, ct) =>
    {
        var retry = 30;
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var meta) && meta is TimeSpan span)
            retry = Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));
        ctx.HttpContext.Response.Headers.RetryAfter = retry.ToString();
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new { error = new { code = "rate-limited", message = "Too many requests from here — wait a moment and try again." } },
            ct);
    };

    // The built-in fixed-window policy, keyed per request: a signed-in user id, else the client
    // address. The budget comes from the IDeploymentPolicy singleton through the request's
    // services — reading builder.Configuration here would miss a config source added at host
    // build (the same reason the LLM policy is resolved lazily).
    options.AddPolicy("auth", (HttpContext http) =>
    {
        var budget = http.RequestServices.GetRequiredService<IDeploymentPolicy>();
        return RateLimitPartition.GetFixedWindowLimiter(RateLimitKey.Partition(http),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = budget.AuthPermitLimit,
                Window = TimeSpan.FromSeconds(budget.AuthWindowSeconds),
                QueueLimit = 0
            });
    });

    options.AddPolicy("convert", (HttpContext http) =>
    {
        var budget = http.RequestServices.GetRequiredService<IDeploymentPolicy>();
        return RateLimitPartition.GetFixedWindowLimiter(RateLimitKey.Partition(http),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = budget.ImportPermitLimit,
                Window = TimeSpan.FromSeconds(budget.ImportWindowSeconds),
                QueueLimit = 0
            });
    });
});

// ST-25: SignalR real-time sessions (playlist-scoped, in-memory state).
builder.Services.AddSignalR();

// ST-14: in-app chord-page→ChordPro conversion. OpenAI-compatible LLM client (config-driven;
// LlmClient.IsConfigured=false → endpoints return a clean 503) + Hangfire job queue with
// Postgres storage (jobs survive restarts; the dashboard is auth-protected below).
// The LLM SSRF policy (LLM_ALLOW_PRIVATE_NETWORK, default true — the product's LLM is normally
// a local model). Resolved lazily so a booted test host can flip it through app.Configuration.
builder.Services.AddSingleton<ILlmEndpointPolicy>(sp => new LlmEndpointPolicy(
    sp.GetRequiredService<IConfiguration>().GetValue("LLM_ALLOW_PRIVATE_NETWORK", true)));

// The public-demo gates (registration, shared-write admin gate, token revocation, rate-limit
// budgets) are one config read at startup — see Services/DeploymentPolicy.cs.
builder.Services.AddSingleton<IDeploymentPolicy, DeploymentPolicy>();

builder.Services.AddHttpClient("Llm", client =>
{
    // 300s default: a 27B thinking model on a shared GPU can be slow when the box is busy.
    client.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("LLM_TIMEOUT_SECONDS", 300));
})
// SSRF: a member-supplied endpoint is fetched with redirects OFF (a public endpoint that
// bounces into the LAN is refused) and, when the policy is closed, the socket layer validates
// the RESOLVED address before connecting (see LlmFetchHandler).
.ConfigurePrimaryHttpMessageHandler(sp => LlmFetchHandler(sp.GetRequiredService<ILlmEndpointPolicy>()));
// ST-30: settings are resolved per call (user row → global row → .env) so the Settings page
// can change them without a restart; the auth header is set per request, not on this client.
// ST-65: the fallback model for rows without one comes from LLM_MODEL config (was hardcoded).
builder.Services.AddSingleton<ILlmClient>(sp =>
    new LlmClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("Llm"),
        defaultModel: builder.Configuration["LLM_MODEL"],
        endpoints: sp.GetRequiredService<ILlmEndpointPolicy>()));

// ST-65: the Settings "Test" probe used a `new HttpClient` per click (socket churn).
builder.Services.AddHttpClient("llm-test", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(sp => LlmFetchHandler(sp.GetRequiredService<ILlmEndpointPolicy>()));
builder.Services.AddScoped<ILlmSettingsProvider, LlmSettingsProvider>();
// ST-33: live conversion progress — per-user SignalR events (Clients.User needs the "sub"
// claim as user id) pushed by the job while the LLM streams.
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, WebApi.Hubs.SubClaimUserIdProvider>();
builder.Services.AddSingleton<IImportProgressReporter, SignalRImportProgressReporter>();
// ST-86: live session state shared by the session hub and GET /api/sessions (Start/Join discovery).
builder.Services.AddSingleton<WebApi.Hubs.SessionRegistry>();
builder.Services.AddTransient<ChordProConversionJob>();
builder.Services.AddScoped<MetadataReviewJob>(); // ST-58: explicit (Hangfire resolved it implicitly via GetServiceOrCreateInstance)
builder.Services.AddHangfire(cfg => cfg
    .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(
        builder.Configuration.GetConnectionString("DefaultConnection")!)));
// ST-58: two server pools — a single-worker "llm" queue serializes the LLM-bound jobs
// (chord-page conversions, metadata scans — routed there by [Queue("llm")] on the job methods)
// against the one GPU, while the default pool keeps its normal worker count for everything
// else. This replaces the static semaphores that used to park a default worker per queued LLM job.
// ST-74: Hangfire 1.8 offers no job-token cancellation on shutdown (ShutdownTimeout=15s just
// force-stops the server, leaving jobs "Processing" for the watchdog to re-queue 30 min later —
// the scan-restart loop). The LLM jobs watch the host lifetime themselves (MetadataReviewJob):
// on SIGTERM they mark the progress row interrupted and return, so nothing is re-queued.
builder.Services.AddHangfireServer(o =>
{
    o.Queues = new[] { "llm" };
    o.WorkerCount = 1;
});
builder.Services.AddHangfireServer(o =>
{
    o.Queues = new[] { "default" };
});

// MT-02: the standalone Android shell (Capacitor APK) is served from the WebView's own
// origin (http://localhost) and calls the user-entered server address cross-origin —
// without CORS headers the WebView rejects every response, including the first-run
// /api/health probe. The web build stays same-origin and is unaffected. Any origin is
// safe here: the API authenticates with a bearer token in the Authorization header
// (no cookie credentials), so a foreign page learns nothing it can use without the JWT.
// ST-87: origins are ECHOED with Access-Control-Allow-Credentials — the SignalR JS
// client fetches /negotiate with credentials:include, and browsers reject a
// credentialed response carrying the ACAO wildcard ("Failed to negotiate with the
// server" in the APK; same-origin web + Node clients never see it). Bearer-token
// auth means echoing origins adds no cookie exposure.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// Apply migrations + seed on startup (MVP convenience, gated by Database:AutoMigrate).
// Fail-safe: if the DB is unreachable the app still boots; /api/health reports the error.
if (app.Configuration.GetValue("Database:AutoMigrate", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        db.Database.Migrate();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Public-demo gate 1: seed an admin from config (SEED_ADMIN_USERNAME / SEED_ADMIN_PASSWORD
        // / SEED_ADMIN_EMAIL) BEFORE DbSeeder.Seed, whose SeedFirstUserIsAdmin promotes the OLDEST
        // user whenever no admin exists. Order is the whole point: seeded first, the configured
        // admin wins and the promotion is a no-op; seeded after, an existing non-admin is already
        // admin by then and the operator's SEED_ADMIN_USERNAME is silently ignored. With
        // REGISTRATION_OPEN=false and an empty database this seed is the only way in.
        var seedAdminUser = app.Configuration["SEED_ADMIN_USERNAME"];
        var seedAdminPassword = app.Configuration["SEED_ADMIN_PASSWORD"];
        if (!string.IsNullOrWhiteSpace(seedAdminUser) && !string.IsNullOrWhiteSpace(seedAdminPassword))
        {
            // Startup is synchronous; blocking on the one-shot seed call is safe here.
            var note = DbSeeder.SeedAdminAsync(
                    userManager, seedAdminUser, app.Configuration["SEED_ADMIN_EMAIL"], seedAdminPassword)
                .GetAwaiter().GetResult();
            if (note is not null)
                app.Logger.LogInformation("{Note}", note);
            else
                app.Logger.LogInformation(
                    "SEED_ADMIN_USERNAME=\"{User}\": not applied — this database already has an admin.",
                    seedAdminUser);
        }

        DbSeeder.Seed(db);
        // ST-106: one-shot tag backfill (artist strings → Artist tags, genre strings →
        // title-cased Genre tags, collections → setlist tags, Key re-derived from
        // chords). AppMeta-marked and idempotent — a no-op on every boot after the first.
        TagBackfill.Run(db, app.Logger);
        // ST-68: a scan row still "running" after a restart is stale — the job is not
        // resuming, so the strip must not show a frozen progress forever.
        var staleScan = db.MetadataScanProgresses.FirstOrDefault(m => m.Key == "library");
        if (staleScan is { Status: "running" })
        {
            staleScan.Status = "interrupted";
            staleScan.FinishedAt = DateTime.UtcNow;
            db.SaveChanges();
        }
        // ST-35 A3: optional fixed test account for unattended agent testing — only created when
        // TEST_USER_PASSWORD is configured; deployments that leave it unset get no extra user.
        var testUserPassword = app.Configuration["TEST_USER_PASSWORD"];
        if (!string.IsNullOrWhiteSpace(testUserPassword))
            DbSeeder.SeedTestUserAsync(userManager, "agent", "agent@ahchord.local", testUserPassword)
                .GetAwaiter().GetResult();

        // The locked-door warning, and it is only true when it is true: registration closed AND
        // no admin on this database. (A populated demo database with an admin restarts daily;
        // warning every time trains everyone to ignore the log.) Note that this block, and with
        // it the admin seed, runs only when Database:AutoMigrate is on — the documented
        // deployment default; an operator who turns migrations off seeds by hand (DEPLOYMENT.md).
        if (app.Configuration.GetValue("REGISTRATION_OPEN", true) == false
            && !userManager.Users.Any(u => u.IsAdmin))
        {
            app.Logger.LogWarning(
                "REGISTRATION_OPEN=false and no admin account exists: nobody can sign in. Set " +
                "SEED_ADMIN_USERNAME/SEED_ADMIN_PASSWORD (the startup seed) or open registration.");
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database migration/seed failed; continuing startup.");
    }
}

// ST-58: drafts left at "running" by a previous process (redeploy mid-conversion) are
// undeletable zombies — reconcile them to failed("interrupted") before any Hangfire server
// starts, so nothing live can be mid-conversion here.
try
{
    using var scope = app.Services.CreateScope();
    var reconciled = await ImportDraftReconciler.ReconcileStuckDraftsAsync(
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    if (reconciled > 0)
        app.Logger.LogInformation("ST-58: reconciled {Count} interrupted import draft(s) to failed.", reconciled);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "ST-58: import draft reconciliation skipped (database unavailable?).");
}

// MT-02: CORS runs first — cross-origin preflight (OPTIONS) requests from the shell
// carry no credentials and must be answered before auth challenges them (405 on
// OPTIONS blocked every Authorization-header call in the APK).
app.UseCors();

// Public-demo gate 2 (half of it): an internet-facing deployment runs behind a reverse proxy
// (Traefik / Caddy / nginx), where every request's RemoteIpAddress is the PROXY — the auth
// limiter would then share one bucket across the whole site and the logs would name the proxy.
// X-Forwarded-For is honoured only for proxies named in TRUSTED_PROXY_IPS: an unauthenticated
// forwarded header is a spoofable client address, so nothing is trusted by default.
var trustedProxies = (app.Configuration["TRUSTED_PROXY_IPS"] ?? string.Empty)
    .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (trustedProxies.Length > 0)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    };
    // Exact addresses and CIDR blocks (a docker-network subnet is the normal value — Traefik's
    // address moves on redeploy). .NET takes the RIGHT-MOST forwarded entry as the client
    // address, so one appending proxy (Traefik) yields the real client and the header is not
    // per-request forgeable. Two chained proxies (Cloudflare → Traefik) need the OUTER proxy's
    // ranges listed too, and the address then names the inner proxy — see SECURITY.md gate 2.
    TrustedProxies.Apply(forwarded, trustedProxies,
        entry => app.Logger.LogWarning("TRUSTED_PROXY_IPS: \"{Entry}\" is not an IP address or CIDR block — ignored.", entry));
    app.UseForwardedHeaders(forwarded);
}

app.UseAuthentication();
app.UseAuthorization();

// Public-demo gate 2: after auth, so a partitioned policy can key on the user id and the
// anonymous endpoints key on the (possibly forwarded) client address.
app.UseRateLimiter();

// ST-63 (finding 6): the two exception families that reach the default handler today get the
// app's JSON error shape instead of an empty 500: DbUpdateException (a lost unique-constraint
// race on a path without its own catch) maps to 409; IOException (the file-backed bug-report
// and wishlist stores) to a 500 with a stable code. Stack traces stay server-side.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (DbUpdateException ex) when (!ctx.Response.HasStarted)
    {
        ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Exceptions").LogWarning(ex, "DbUpdateException on {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(
            new { error = new { code = "conflict", message = "That record already exists (concurrent change)." } });
    }
    catch (IOException ex) when (!ctx.Response.HasStarted)
    {
        ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Exceptions").LogError(ex, "IOException on {Path}", ctx.Request.Path);
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(
            new { error = new { code = "io-error", message = "Server storage is unavailable. Try again." } });
    }
});

// ST-33: SPA HTML must always be revalidated. Hashed assets are immutable and cache
// freely, but a browser-cached index.html (served for "/" and every extensionless SPA
// route via the fallback) pins the previous deploy's bundles until a hard refresh.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    // MT-04: the service worker + manifest must always be revalidated, same reasoning as
    // index.html — a cached sw.js pins the whole precached bundle until a hard refresh.
    var isShellControl = path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase);
    if (isShellControl
        || (!Path.HasExtension(path)
        && !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWith("/hangfire", StringComparison.OrdinalIgnoreCase)))
    {
        ctx.Response.Headers.CacheControl = "no-cache";
    }
    await next();
});

// Serve the React build (wwwroot) as static files
app.UseStaticFiles();

// OpenAPI document + Scalar API reference, available in all environments (anonymous)
app.MapOpenApi();
app.MapScalarApiReference();

// ST-14: Hangfire dashboard, JWT-protected (unauthenticated requests get the standard
// 401 JSON challenge). Must be mapped before the SPA fallback so /hangfire is not swallowed.
app.MapHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new IDashboardAuthorizationFilter[] { new JwtDashboardAuthFilter() }
});

// /api/* requires a valid JWT by default; anonymous endpoints opt out explicitly.
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/health", async (ApplicationDbContext db) =>
{
    var databaseOk = true;
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
    }
    catch
    {
        databaseOk = false;
    }

    var payload = new
    {
        status = databaseOk ? "ok" : "error",
        database = databaseOk ? "ok" : "error",
        service = "ahchord-webapi",
        version = InfoEndpoints.AppVersion(), // ST-31: same clean version as /api/info
        timestamp = DateTime.UtcNow
    };
    // 503 still carries the payload so the body shows database: "error" (ST-04 acceptance).
    return databaseOk
        ? Results.Ok(payload)
        : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

api.MapAuthEndpoints();
api.MapSongEndpoints();
api.MapSongSourceFileEndpoints(); // ST-46: source files (PDF/PNG/JPG) per song
api.MapImportEndpoints();
api.MapImportConvertEndpoints();
api.MapLlmSettingsEndpoints(); // ST-30: LLM settings (global default + per-user override)
api.MapPlaylistEndpoints();
api.MapCollectionEndpoints();
api.MapTagEndpoints(); // ST-106: autocomplete + the tag browser
api.MapMusicBrainzEndpoints();
api.MapSongsterrEndpoints(); // ST-44: song search (chord-source lookup)
api.MapWishlistEndpoints(); // ST-44: per-user wishlist
api.MapInfoEndpoints(); // ST-31: version + changelog (Settings → About)
api.MapAppDownloadEndpoints(); // MT-02: Android APK download (Settings link, LAN install)
api.MapSyncEndpoints(); // MT-04: offline cache payload (full + updatedSince delta)
api.MapBugReportEndpoints(); // ST-35 H2: in-app bug reports → /data/bug_reports.md
api.MapMetadataReviewEndpoints(); // ST-35 F3: metadata auto-review (proposals + accept/reject)
api.MapSessionEndpoints(); // ST-86: session discovery for the Start/Join UI (GET /api/sessions)

// ST-25: real-time session hub (JWT-authenticated, not under the /api group).
app.MapHub<WebApi.Hubs.SessionHub>("/hubs/session");

// SPA fallback: serve the React app for any non-API, non-asset route.
// Must come AFTER all API routes so /api/* and /scalar are never swallowed.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>
/// SSRF hardening for the LLM surface (the only member-supplied URL the server fetches —
/// conversion input is pasted text, never a fetched page). Both LLM
/// clients fetch a member-supplied base URL, so: redirects are never followed, and when
/// LLM_ALLOW_PRIVATE_NETWORK=false the connect callback re-validates the RESOLVED address —
/// a public hostname that answers with a LAN/loopback/metadata address (DNS rebinding) dies
/// at the socket, not in the response. With the policy open (the normal self-hosted local
/// model) the handler only adds AllowAutoRedirect=false, so the default connect pipeline
/// (Happy Eyeballs, proxies) stays in charge.
/// </summary>
static HttpMessageHandler LlmFetchHandler(ILlmEndpointPolicy policy)
{
    var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
    if (policy.AllowPrivateNetwork)
        return handler;

    handler.ConnectCallback = async (SocketsHttpConnectionContext context, CancellationToken ct) =>
    {
        // .NET 10's context carries the target as a DnsEndPoint (host + port, not a resolved
        // address), so validate every address the name resolves to and dial the one we checked.
        var target = context.DnsEndPoint;
        var resolved = await Dns.GetHostAddressesAsync(target.Host, ct);
        if (resolved.Length == 0 || Array.Exists(resolved, LlmUrlGuard.IsPrivateAddress))
            throw new HttpRequestException("The LLM endpoint resolved to a disallowed address.");

        var socket = new Socket(resolved[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(new IPEndPoint(resolved[0], target.Port), ct);
        return new NetworkStream(socket, ownsSocket: true);
    };
    return handler;
}
