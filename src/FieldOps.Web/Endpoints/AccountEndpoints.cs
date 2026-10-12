using System.Security.Claims;
using FieldOps.Web.Data;
using FieldOps.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Endpoints;

/// <summary>
/// Sign-in for the demo: pick one of the sample users and get a cookie for them. There are no passwords
/// anywhere in the app. Both endpoints bind form fields, so ASP.NET Core validates the antiforgery token
/// that the forms post with.
/// </summary>
public static class AccountEndpoints
{
    /// <summary>Rate-limit policy for sign-in posts: 10 a minute per client address (set up in Program.cs).</summary>
    public const string RateLimitPolicy = "signin";

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/signin", async (
                [FromForm] int userId,
                [FromForm] string? returnUrl,
                HttpContext http,
                IDbContextFactory<AppDbContext> dbFactory,
                TimeProvider clock,
                ILoggerFactory loggerFactory) =>
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (user is null) return Results.Redirect("/signin");

                var identity = new ClaimsIdentity(UserContext.ClaimsFor(user, clock.GetUtcNow()), CookieAuthenticationDefaults.AuthenticationScheme);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                SecurityLog.For(loggerFactory).SignedIn(user.Id, user.Role, http.Connection.RemoteIpAddress);
                return Results.LocalRedirect(LocalUrl.IsLocal(returnUrl) ? returnUrl : "/");
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);

        app.MapPost("/account/signout", async ([FromForm] string? returnUrl, HttpContext http, ILoggerFactory loggerFactory) =>
        {
            var userId = UserContext.From(http.User)?.Id;
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            SecurityLog.For(loggerFactory).SignedOut(userId);
            return Results.Redirect("/signin");
        });

        return app;
    }
}
