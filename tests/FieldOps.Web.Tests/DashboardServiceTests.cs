using FieldOps.Core.Jobs;
using FieldOps.Web.Services;

namespace FieldOps.Web.Tests;

public sealed class DashboardServiceTests : IDisposable
{
    private readonly TestDbFactory _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Intake_counts_a_job_in_the_local_month_it_was_created()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");
        var now = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
        // 02:00 UTC on March 1 is still February 28 at five hours behind UTC. This is the only job.
        Seed.Job(_db, "J-1001", JobStatus.Quote, null, new DateTime(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc));

        var data = await new DashboardService(_db, new FixedTimeProvider(now, zone)).LoadAsync();

        Assert.Equal(1, data.Intake[^2].Count);
        Assert.Equal(0, data.Intake[^1].Count);
        Assert.True(data.Intake[^1].IsCurrent);
    }
}
