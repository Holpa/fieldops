using System.Globalization;
using System.Security.Claims;
using FieldOps.Web.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace FieldOps.Web.Services;

public static class Policies
{
    public const string Office = "office";
}

/// <summary>The signed-in user as the app sees them, read from the auth cookie's claims.</summary>
public sealed record UserContext(int Id, string Name, UserRole Role)
{
    public bool IsOffice => Role == UserRole.Office;

    public static IEnumerable<Claim> ClaimsFor(AppUser user) =>
    [
        new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
        new(ClaimTypes.Name, user.Name),
        new(ClaimTypes.Role, user.Role.ToString()),
    ];

    public static UserContext? From(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        if (!int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id)) return null;
        if (!Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var role)) return null;
        return new UserContext(id, principal.Identity.Name ?? string.Empty, role);
    }
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
