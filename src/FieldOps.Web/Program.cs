using System.Text.Json.Serialization;
using FieldOps.Web.Components;
using FieldOps.Web.Data;
using FieldOps.Web.Endpoints;
using FieldOps.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics;
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
        options.Cookie.Name = "fieldops.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            // API callers get a status code, not an HTML sign-in page.
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.Office, policy => policy.RequireRole(nameof(UserRole.Office)));
builder.Services.AddCascadingAuthenticationState();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // A request body that leaves out a value the API needs is a 400, not a silent default.
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});

// API errors are ProblemDetails bodies that carry the request's trace id.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c => c.ProblemDetails.Extensions["traceId"] = c.HttpContext.TraceIdentifier);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChatNotifier>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddScoped<CircuitHandler, PresenceCircuitHandler>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<RouteService>();
builder.Services.AddScoped<ChatService>();

// No "Server: Kestrel" header on responses.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

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
    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
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

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Static files (CSS, fonts, the Blazor script) must load on the sign-in page too.
app.MapStaticAssets().AllowAnonymous();
app.MapAccountEndpoints();
app.MapJobsApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
