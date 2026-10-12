using System.Globalization;
using System.Security.Claims;
using FieldOps.Web.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace FieldOps.Web.Services;

public static class Policies
{
    public const string Office = "office";
}

/// <summary>The signed-in user as the app sees them, read from the auth cookie's claims.</summary>
public sealed record UserContext(int Id, string Name, UserRole Role)
{
    public bool IsOffice => Role == UserRole.Office;

    public static IEnumerable<Claim> ClaimsFor(AppUser user, DateTimeOffset signedInUtc) =>
    [
        new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
        new(ClaimTypes.Name, user.Name),
        new(ClaimTypes.Role, user.Role.ToString()),
        new(SessionLimit.SignedInClaim, SessionLimit.Stamp(signedInUtc)),
    ];

    public static UserContext? From(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        if (!int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id)) return null;
        if (!Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var role)) return null;
        return new UserContext(id, principal.Identity.Name ?? string.Empty, role);
    }
}

/// <summary>
/// The absolute session cap. Sliding expiration re-issues an active cookie and resets its issue time, so the
/// original sign-in time travels in a claim of its own and is checked on every request.
/// </summary>
public static class SessionLimit
{
    public const string SignedInClaim = "fieldops.signed_in";

    public static readonly TimeSpan Absolute = TimeSpan.FromHours(12);

    public static string Stamp(DateTimeOffset signedInUtc) => signedInUtc.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>True once 12 hours have passed since sign-in. A missing or unreadable sign-in time counts as expired.</summary>
    public static bool HasExpired(string? signedInStamp, DateTimeOffset now) =>
        !DateTimeOffset.TryParseExact(signedInStamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var signedIn)
        || now - signedIn > Absolute;
}

/// <summary>
/// Applies the 12-hour cap to open tabs. The cookie check runs only on HTTP requests, and in-app navigation stays
/// on the Blazor circuit, so the circuit's user is checked again every minute. Once the cap has passed the user
/// becomes signed out, the router sends them to the sign-in page, and that request removes the cookie.
/// </summary>
public sealed class SessionLimitAuthenticationStateProvider(ILoggerFactory loggerFactory, TimeProvider clock)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        Task.FromResult(!SessionLimit.HasExpired(authenticationState.User.FindFirstValue(SessionLimit.SignedInClaim), clock.GetUtcNow()));
}

/// <summary>Resolves the signed-in user inside interactive components.</summary>
public sealed class CurrentUser(AuthenticationStateProvider authState)
{
    public async Task<UserContext> GetAsync()
    {
        var state = await authState.GetAuthenticationStateAsync();
        return UserContext.From(state.User) ?? throw new InvalidOperationException("No signed-in user.");
    }
}
