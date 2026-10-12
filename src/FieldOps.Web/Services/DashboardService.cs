using System.Globalization;
using FieldOps.Core.Analytics;
using FieldOps.Core.Jobs;
using FieldOps.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Services;

public sealed record MonthBar(string Label, int Count, bool IsCurrent);
public sealed record StatusCount(JobStatus Status, int Count);
public sealed record UpcomingVisit(int JobId, string Reference, string Title, DateOnly Date, string? Technician);
public sealed record Workload(int UserId, string Name, int OpenJobs, int VisitsThisWeek, int Overdue);

public sealed record DashboardData(
    int OpenJobs,
    int VisitsThisWeek,
    int OverdueJobs,
    double HoursThisMonth,
    IReadOnlyList<MonthBar> Intake,
    ForecastResult Forecast,
    string ForecastMonth,
    IReadOnlyList<StatusCount> OpenByStatus,
    IReadOnlyList<UpcomingVisit> Upcoming,
    IReadOnlyList<Workload> Workloads);

/// <summary>
/// Builds the dashboard in one pass over a slim projection of the jobs. The intake trend is fitted to the
/// last twelve complete months and projected for the current month, so a half-finished month never drags
/// the line down.
/// </summary>
public sealed class DashboardService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider clock)
{
    private const int HistoryMonths = 12;

    public async Task<DashboardData> LoadAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var weekEnd = today.AddDays(6);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var jobs = await db.ActiveJobs.AsNoTracking()
            .Select(j => new
            {
                j.Id,
                j.Reference,
                j.Title,
                j.Status,
                j.CreatedUtc,
                j.AssigneeId,
                Visits = j.Visits.Select(v => new { v.Date, v.IsDone, v.Hours }).ToList(),
            })
            .ToListAsync(ct);
        var technicians = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Field)
            .OrderBy(u => u.Name)
            .ToListAsync(ct);
        var names = technicians.ToDictionary(u => u.Id, u => u.Name);

        var open = jobs.Where(j => StatusWorkflow.IsOpen(j.Status)).ToList();
        bool IsOverdue(JobStatus status, IEnumerable<DateOnly> pending) => StatusWorkflow.IsActive(status) && pending.Any(d => d < today);

        // Intake: twelve complete months plus the month in progress.
        var months = Enumerable.Range(0, HistoryMonths + 1)
            .Select(i => monthStart.AddMonths(i - HistoryMonths))
            .ToList();
        // Jobs count in the local month they were created in, the same calendar as monthStart.
        var createdByMonth = jobs
            .Select(j => TimeZoneInfo.ConvertTimeFromUtc(j.CreatedUtc, clock.LocalTimeZone))
            .GroupBy(created => new DateOnly(created.Year, created.Month, 1))
            .ToDictionary(g => g.Key, g => g.Count());
        var intake = months
            .Select(m => new MonthBar(
                m.ToString("MMM", CultureInfo.CurrentCulture),
                createdByMonth.GetValueOrDefault(m),
                m == monthStart))
            .ToList();
        var forecast = TrendForecast.Evaluate(intake.Where(b => !b.IsCurrent).Select(b => b.Count).ToList());

        var upcoming = open
            .SelectMany(j => j.Visits.Where(v => !v.IsDone && v.Date >= today && v.Date <= weekEnd)
                .Select(v => new UpcomingVisit(j.Id, j.Reference, j.Title, v.Date,
                    j.AssigneeId is { } a ? names.GetValueOrDefault(a) : null)))
            .OrderBy(v => v.Date).ThenBy(v => v.Reference)
            .ToList();

        var workloads = technicians
            .Select(t =>
            {
                var mine = open.Where(j => j.AssigneeId == t.Id).ToList();
                return new Workload(
                    t.Id,
                    t.Name,
                    mine.Count,
                    mine.Sum(j => j.Visits.Count(v => !v.IsDone && v.Date >= today && v.Date <= weekEnd)),
                    mine.Count(j => IsOverdue(j.Status, j.Visits.Where(v => !v.IsDone).Select(v => v.Date))));
            })
            .ToList();

        return new DashboardData(
            OpenJobs: open.Count,
            VisitsThisWeek: upcoming.Count,
            OverdueJobs: open.Count(j => IsOverdue(j.Status, j.Visits.Where(v => !v.IsDone).Select(v => v.Date))),
            HoursThisMonth: jobs.SelectMany(j => j.Visits).Where(v => v.IsDone && v.Date >= monthStart).Sum(v => v.Hours ?? 0),
            Intake: intake,
            Forecast: forecast,
            ForecastMonth: monthStart.ToString("MMMM", CultureInfo.CurrentCulture),
            OpenByStatus: Enum.GetValues<JobStatus>()
                .Where(StatusWorkflow.IsOpen)
                .Select(s => new StatusCount(s, open.Count(j => j.Status == s)))
                .ToList(),
            Upcoming: upcoming.Take(8).ToList(),
            Workloads: workloads);
    }
}
