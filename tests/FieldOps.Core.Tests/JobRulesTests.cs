using FieldOps.Core.Auditing;
using FieldOps.Core.Jobs;

namespace FieldOps.Core.Tests;

public class StatusWorkflowTests
{
    [Theory]
    [InlineData(JobStatus.Quote, JobStatus.Scheduled)]
    [InlineData(JobStatus.Scheduled, JobStatus.FieldWork)]
    [InlineData(JobStatus.FieldWork, JobStatus.Office)]
    [InlineData(JobStatus.Office, JobStatus.Delivered)]
    [InlineData(JobStatus.Delivered, JobStatus.Invoiced)]
    [InlineData(JobStatus.OnHold, JobStatus.FieldWork)]
    [InlineData(JobStatus.Cancelled, JobStatus.Quote)]
    public void Allows_the_normal_path(JobStatus from, JobStatus to) => Assert.True(StatusWorkflow.CanMove(from, to));

    [Theory]
    [InlineData(JobStatus.Quote, JobStatus.Invoiced)]
    [InlineData(JobStatus.Scheduled, JobStatus.Delivered)]
    [InlineData(JobStatus.Invoiced, JobStatus.Quote)]
    [InlineData(JobStatus.Cancelled, JobStatus.FieldWork)]
    public void Blocks_skipping_steps(JobStatus from, JobStatus to) => Assert.False(StatusWorkflow.CanMove(from, to));

    [Fact]
    public void Staying_on_the_same_status_is_always_allowed()
    {
        foreach (var status in Enum.GetValues<JobStatus>())
            Assert.True(StatusWorkflow.CanMove(status, status));
    }

    [Fact]
    public void Every_status_has_a_transition_list()
    {
        foreach (var status in Enum.GetValues<JobStatus>())
            Assert.NotNull(StatusWorkflow.NextSteps(status));
    }

    [Fact]
    public void On_hold_is_open_but_not_active()
    {
        Assert.True(StatusWorkflow.IsOpen(JobStatus.OnHold));
        Assert.False(StatusWorkflow.IsActive(JobStatus.OnHold));
        Assert.True(StatusWorkflow.IsActive(JobStatus.Scheduled));
        Assert.False(StatusWorkflow.IsActive(JobStatus.Invoiced));
    }

    [Fact]
    public void Only_invoiced_and_cancelled_are_closed()
    {
        var closed = Enum.GetValues<JobStatus>().Where(s => !StatusWorkflow.IsOpen(s));

        Assert.Equal(new[] { JobStatus.Invoiced, JobStatus.Cancelled }, closed);
    }

    [Theory]
    [InlineData(JobStatus.Delivered, JobStatus.Invoiced)]
    [InlineData(JobStatus.Scheduled, JobStatus.Cancelled)]
    [InlineData(JobStatus.Cancelled, JobStatus.Quote)]
    public void Billing_and_cancelling_are_office_only(JobStatus from, JobStatus to) =>
        Assert.True(StatusWorkflow.IsOfficeOnly(from, to));

    [Fact]
    public void Technicians_see_only_the_work_steps()
    {
        Assert.Equal(new[] { JobStatus.Office }, StatusWorkflow.NextSteps(JobStatus.Delivered, isOffice: false));
        Assert.Equal(new[] { JobStatus.Invoiced, JobStatus.Office }, StatusWorkflow.NextSteps(JobStatus.Delivered, isOffice: true));
        Assert.Empty(StatusWorkflow.NextSteps(JobStatus.Cancelled, isOffice: false));
        Assert.DoesNotContain(JobStatus.Cancelled, StatusWorkflow.NextSteps(JobStatus.Quote, isOffice: false));
    }
}

public class VisitScheduleTests
{
    private static readonly DateOnly Today = new(2025, 6, 10);

    [Fact]
    public void Next_visit_is_the_earliest_one_not_done()
    {
        var schedule = VisitSchedule.From(
        [
            new VisitInfo(Today.AddDays(5), false),
            new VisitInfo(Today.AddDays(2), false),
            new VisitInfo(Today.AddDays(-3), true),
        ]);

        Assert.Equal(Today.AddDays(2), schedule.NextVisit);
        Assert.Equal(Today.AddDays(-3), schedule.LastCompleted);
        Assert.Equal(3, schedule.Total);
        Assert.Equal(1, schedule.Done);
        Assert.False(schedule.IsOverdue(Today));
    }

    [Fact]
    public void A_past_visit_that_is_not_done_is_overdue()
    {
        var schedule = VisitSchedule.From([new VisitInfo(Today.AddDays(-1), false)]);

        Assert.True(schedule.IsOverdue(Today));
    }

    [Fact]
    public void A_visit_today_is_not_overdue()
    {
        var schedule = VisitSchedule.From([new VisitInfo(Today, false)]);

        Assert.False(schedule.IsOverdue(Today));
    }

    [Fact]
    public void No_visits_means_nothing_scheduled()
    {
        var schedule = VisitSchedule.From([]);

        Assert.Same(VisitSchedule.Empty, schedule);
        Assert.False(schedule.IsOverdue(Today));
    }
}

public class FieldDiffTests
{
    [Fact]
    public void Reports_only_the_fields_that_changed()
    {
        var before = new Dictionary<string, string?> { ["Title"] = "Roof inspection", ["City"] = "Fairview" };
        var after = new Dictionary<string, string?> { ["Title"] = "Roof inspection", ["City"] = "Lakeside" };

        var change = Assert.Single(FieldDiff.Compare(before, after));

        Assert.Equal(new FieldChange("City", "Fairview", "Lakeside"), change);
    }

    [Fact]
    public void Blank_and_missing_values_are_the_same()
    {
        var before = new Dictionary<string, string?> { ["Notes"] = null };
        var after = new Dictionary<string, string?> { ["Notes"] = "   " };

        Assert.Empty(FieldDiff.Compare(before, after));
    }

    [Fact]
    public void Surrounding_whitespace_is_ignored()
    {
        var before = new Dictionary<string, string?> { ["Client"] = "Harbor Point Realty" };
        var after = new Dictionary<string, string?> { ["Client"] = "  Harbor Point Realty " };

        Assert.Empty(FieldDiff.Compare(before, after));
    }

    [Fact]
    public void A_field_present_on_one_side_only_is_a_change()
    {
        var before = new Dictionary<string, string?> { ["Assignee"] = "Priya Shah" };
        var after = new Dictionary<string, string?>();

        var change = Assert.Single(FieldDiff.Compare(before, after));

        Assert.Equal(new FieldChange("Assignee", "Priya Shah", null), change);
    }
}
