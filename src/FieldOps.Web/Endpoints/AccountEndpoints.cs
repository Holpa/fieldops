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
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/signin", async (
                [FromForm] int userId,
                [FromForm] string? returnUrl,
                HttpContext http,
                IDbContextFactory<AppDbContext> dbFactory) =>
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (user is null) return Results.Redirect("/signin");

                var identity = new ClaimsIdentity(UserContext.ClaimsFor(user), CookieAuthenticationDefaults.AuthenticationScheme);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.LocalRedirect(LocalUrl.IsLocal(returnUrl) ? returnUrl : "/");
            })
            .AllowAnonymous();

        app.MapPost("/account/signout", async ([FromForm] string? returnUrl, HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/signin");
        });

        return app;
    }
}
