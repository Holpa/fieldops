using System.Security.Claims;
using FieldOps.Web.Data;
using FieldOps.Web.Services;

namespace FieldOps.Web.Tests;

public sealed class SessionLimitTests
{
    private static readonly DateTimeOffset SignedIn = new(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_session_just_under_twelve_hours_old_is_allowed() =>
        Assert.False(SessionLimit.HasExpired(SessionLimit.Stamp(SignedIn), SignedIn.AddHours(11).AddMinutes(59)));

    [Fact]
    public void A_session_just_over_twelve_hours_old_has_expired() =>
        Assert.True(SessionLimit.HasExpired(SessionLimit.Stamp(SignedIn), SignedIn.AddHours(12).AddMinutes(1)));

    [Fact]
    public void A_session_without_a_sign_in_time_has_expired() =>
        Assert.True(SessionLimit.HasExpired(null, SignedIn));

    [Fact]
    public void The_sign_in_claims_carry_the_time_the_checks_read()
    {
        // The cookie check and the open-tab check both read this claim from the signed-in principal.
        var user = new AppUser { Id = 3, Name = "Kai Moreno", Title = "Field technician", Role = UserRole.Field };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(UserContext.ClaimsFor(user, SignedIn), "test"));
        var stamp = principal.FindFirstValue(SessionLimit.SignedInClaim);

        Assert.False(SessionLimit.HasExpired(stamp, SignedIn.AddHours(11).AddMinutes(59)));
        Assert.True(SessionLimit.HasExpired(stamp, SignedIn.AddHours(12).AddMinutes(1)));
    }
}
