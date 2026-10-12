using FieldOps.Core.Jobs;
using FieldOps.Web.Data;
using FieldOps.Web.Services;

namespace FieldOps.Web.Tests;

public sealed class JobServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly VisitDay = new(2026, 3, 12);

    private readonly TestDbFactory _db = new();
    private readonly JobService _jobs;
    private readonly UserContext _office;
    private readonly UserContext _tech;
    private readonly int _jobId;

    public JobServiceTests()
    {
        _jobs = new JobService(_db, new FixedTimeProvider(Now));
        _office = Seed.User(_db, "Avery Stone", UserRole.Office);
        _tech = Seed.User(_db, "Kai Moreno", UserRole.Field);
        _jobId = Seed.Job(_db, "J-1001", JobStatus.FieldWork, _tech.Id, Now.UtcDateTime);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_save_against_an_old_version_is_a_conflict()
    {
        var first = await _jobs.UpdateAsync(_jobId, Input(title: "Replace the boiler valve"), expectedVersion: 0, _office);
        var stale = await _jobs.UpdateAsync(_jobId, Input(title: "Service the boiler"), expectedVersion: 0, _office);

        Assert.True(first.IsOk);
        Assert.Equal(ResultKind.Conflict, stale.Kind);
    }

    [Fact]
    public async Task A_technician_cannot_invoice_their_own_job()
    {
        var jobId = Seed.Job(_db, "J-1002", JobStatus.Delivered, _tech.Id, Now.UtcDateTime);

        var result = await _jobs.MoveAsync(jobId, JobStatus.Invoiced, _tech);

        Assert.Equal(ResultKind.Forbidden, result.Kind);
        Assert.Equal(JobStatus.Delivered, (await _jobs.GetAsync(jobId))!.Input.Status);
    }

    [Fact]
    public async Task A_two_field_edit_writes_one_activity_entry_with_both_changes()
    {
        var result = await _jobs.UpdateAsync(_jobId, Input(title: "Replace the boiler valve", notes: "Bring a ladder"), expectedVersion: 0, _office);

        Assert.True(result.IsOk);
        var entry = Assert.Single((await _jobs.GetAsync(_jobId))!.Events);
        Assert.Equal("Changed 2 fields", entry.Summary);
        Assert.Equal(new[] { "Notes", "Title" }, entry.Changes.Select(c => c.Field).Order());
    }

    [Fact]
    public async Task A_second_visit_on_the_same_day_is_invalid()
    {
        var first = await _jobs.AddVisitAsync(_jobId, VisitDay, _tech);
        var second = await _jobs.AddVisitAsync(_jobId, VisitDay, _tech);

        Assert.True(first.IsOk);
        Assert.Equal(ResultKind.Invalid, second.Kind);
    }

    [Fact]
    public async Task A_move_the_workflow_does_not_allow_is_invalid()
    {
        var result = await _jobs.MoveAsync(_jobId, JobStatus.Invoiced, _office);

        Assert.Equal(ResultKind.Invalid, result.Kind);
    }

    [Fact]
    public async Task A_technician_cannot_change_visits_on_an_invoiced_job()
    {
        var jobId = Seed.Job(_db, "J-1002", JobStatus.Invoiced, _tech.Id, Now.UtcDateTime);

        var result = await _jobs.AddVisitAsync(jobId, VisitDay, _tech);

        Assert.Equal(ResultKind.Forbidden, result.Kind);
    }

    [Theory]
    [InlineData(1000, 1, 100, 1)]
    [InlineData(1, 1, 5, 1)]
    [InlineData(5, 0, 5, 1)]
    [InlineData(5, 99, 5, 3)]
    public async Task Search_clamps_the_page_size_and_page(int pageSize, int page, int expectedPageSize, int expectedPage)
    {
        // Twelve jobs in all, so a page size of 5 gives three pages.
        for (int i = 2; i <= 12; i++)
            Seed.Job(_db, $"J-{1000 + i}", JobStatus.Scheduled, null, Now.UtcDateTime);

        var result = await _jobs.SearchAsync(new JobQuery(Page: page, PageSize: pageSize));

        Assert.Equal(expectedPageSize, result.PageSize);
        Assert.Equal(expectedPage, result.Page);
    }

    // The seeded job's own values, with the given title and notes.
    private JobInput Input(string title, string? notes = null) => new()
    {
        Title = title,
        Client = "Harbor Bakery",
        Address = "12 Quay Street",
        City = "Fairview",
        Status = JobStatus.FieldWork,
        AssigneeId = _tech.Id,
        Notes = notes,
    };
}
