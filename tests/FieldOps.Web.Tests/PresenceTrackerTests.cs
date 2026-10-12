using FieldOps.Web.Services;

namespace FieldOps.Web.Tests;

public sealed class PresenceTrackerTests
{
    [Fact]
    public void A_failing_subscriber_does_not_stop_the_others()
    {
        var presence = new PresenceTracker();
        int calls = 0;
        presence.Changed += () => throw new InvalidOperationException("This page has already closed.");
        presence.Changed += () => calls++;

        presence.Connected(1);

        Assert.Equal(1, calls);
        Assert.True(presence.IsOnline(1));
    }
}
