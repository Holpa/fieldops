using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FieldOps.Web.Components;
using FieldOps.Web.Data;
using FieldOps.Web.Endpoints;
using FieldOps.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// A context factory rather than a scoped context: a Blazor Server circuit lives for minutes and can run
// several handlers at once, and a DbContext is neither long-lived nor thread-safe.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("FieldOps")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/signin";
        options.AccessDeniedPath = "/signin";
        // A __Host- cookie is only accepted when it is Secure, has Path=/ and has no Domain.
        options.Cookie.Name = "__Host-fieldops.auth";
        options.Cookie.Path = "/";
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            // API callers get a status code, not an HTML sign-in page.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                Security(context.HttpContext).ApiUnauthenticated(context.Request.Path, context.HttpContext.Connection.RemoteIpAddress);
            }
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        // Sliding renewal keeps an active session going, but never past 12 hours from sign-in.
        options.Events.OnValidatePrincipal = async context =>
        {
            var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            if (SessionLimit.HasExpired(context.Principal?.FindFirstValue(SessionLimit.SignedInClaim), now))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

// The antiforgery cookie gets the same __Host- treatment.
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-fieldops.antiforgery";
    options.Cookie.Path = "/";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.Office, policy => policy.RequireRole(nameof(UserRole.Office)));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, SessionLimitAuthenticationStateProvider>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // A request body that leaves out a value the API needs is a 400, not a silent default.
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});

// API errors are ProblemDetails bodies that carry the request's trace id.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c => c.ProblemDetails.Extensions["traceId"] = c.HttpContext.TraceIdentifier);

// Rate limits in fixed one-minute windows with no queue: sign-in posts per client address, API calls per
// signed-in user. A refusal is a logged 429 with Retry-After and a ProblemDetails body.
const int RateLimitWindowSeconds = 60;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AccountEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => PerWindow(10)));
    options.AddPolicy(JobsApi.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(UserOrClientKey(http), _ => PerWindow(60)));
    options.OnRejected = async (context, _) =>
    {
        var http = context.HttpContext;
        var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "none";
        Security(http).RateLimited(policy, policy == JobsApi.RateLimitPolicy ? UserOrClientKey(http) : ClientKey(http), http.Request.Path);
        http.Response.Headers.RetryAfter = RateLimitWindowSeconds.ToString(CultureInfo.InvariantCulture);
        await TypedResults.Problem("Too many requests. Wait a moment and try again.",
            statusCode: StatusCodes.Status429TooManyRequests, title: "Too Many Requests").ExecuteAsync(http);
    };
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChatNotifier>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<ChatSendLimiter>();
builder.Services.AddScoped<CircuitHandler, PresenceCircuitHandler>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<RouteService>();
builder.Services.AddScoped<ChatService>();

// No "Server: Kestrel" header on responses, and request bodies capped at 1 MB: the app takes no uploads, and
// its largest body is a small form or JSON patch. Blazor's SignalR messages have their own size limit.
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 1_048_576;
});

var app = builder.Build();

await SeedData.EnsureCreatedAsync(
    app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
    app.Services.GetRequiredService<TimeProvider>());

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

// Friendly "not found" pages are for people. API callers get the bare status code.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
        statusPages.Enabled = false;
    return next(context);
});

const string ContentSecurityPolicy =
    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
    "font-src 'self'; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

// Security headers on every response, set when the response starts so a re-executed error page keeps them.
app.Use((context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;

        // Signed-in pages and API data are not kept in the browser cache; static assets keep their own caching.
        if (context.User.Identity?.IsAuthenticated == true && !headers.ContainsKey("Cache-Control"))
        {
            headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            headers["Pragma"] = "no-cache";
        }
        return Task.CompletedTask;
    });
    return next(context);
});

// After the header pass, so the redirect carries the headers too; before authentication, so no cookie is
// read from a plain HTTP request.
app.UseHttpsRedirection();

app.UseAuthentication();
// After authentication, because the API limit is per signed-in user; before authorization, antiforgery and the
// endpoints, so a refused request never reaches the database.
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();

// Static files (CSS, fonts, the Blazor script) must load on the sign-in page too.
app.MapStaticAssets().AllowAnonymous();
app.MapAccountEndpoints();
app.MapJobsApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static FixedWindowRateLimiterOptions PerWindow(int permits) =>
    new() { PermitLimit = permits, Window = TimeSpan.FromSeconds(RateLimitWindowSeconds), QueueLimit = 0 };

// Rate-limit partitions: the client address, or the signed-in user with the address as the fallback.
static string ClientKey(HttpContext http) => "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

static string UserOrClientKey(HttpContext http) =>
    http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } id ? "user:" + id : ClientKey(http);

static ILogger Security(HttpContext http) => SecurityLog.For(http.RequestServices.GetRequiredService<ILoggerFactory>());
