using FieldOps.Core.Jobs;
using FieldOps.Core.Routing;
using FieldOps.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Services;

public sealed record RouteStop(
    int VisitId,
    int JobId,
    string Reference,
    string Title,
    string Client,
    string Address,
    GeoPoint Location,
    bool IsDone,
    JobStatus Status,
    double LegKm);

public sealed record RouteDay(DateOnly Date, GeoPoint Depot, IReadOnlyList<RouteStop> Stops, RoutePlan? Plan);

/// <summary>Turns a technician's visits for one day into an ordered route that starts at the depot.</summary>
public sealed class RouteService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<RouteDay> PlanAsync(int technicianId, DateOnly date, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // "As booked" order: the order the office created the jobs in. The planner is measured against it.
        var stops = await db.Visits.AsNoTracking()
            .Where(v => v.Date == date)
            .Join(db.ActiveJobs.Where(j => j.AssigneeId == technicianId && j.Status != JobStatus.Cancelled),
                v => v.JobId, j => j.Id,
                (v, j) => new { Visit = v, Job = j })
            .OrderBy(x => x.Job.Id)
            .Select(x => new
            {
                VisitId = x.Visit.Id,
                x.Visit.IsDone,
                x.Job.Status,
                JobId = x.Job.Id,
                x.Job.Reference,
                x.Job.Title,
                x.Job.Client,
                x.Job.Address,
                x.Job.City,
                x.Job.Latitude,
                x.Job.Longitude,
            })
            .ToListAsync(ct);

        var depot = SeedData.Depot;
        if (stops.Count == 0) return new RouteDay(date, depot, [], null);

        var points = stops.Select(s => new GeoPoint(s.Latitude, s.Longitude)).ToList();
        var plan = RoutePlanner.Plan(depot, points);

        var ordered = new List<RouteStop>(stops.Count);
        var previous = depot;
        foreach (var index in plan.Order)
        {
            var s = stops[index];
            var here = points[index];
            ordered.Add(new RouteStop(s.VisitId, s.JobId, s.Reference, s.Title, s.Client, $"{s.Address}, {s.City}",
                here, s.IsDone, s.Status, Distance.Km(previous, here)));
            previous = here;
        }

        return new RouteDay(date, depot, ordered, plan);
    }

    /// <summary>Days from today onward on which the technician has visits, for quick navigation.</summary>
    public async Task<IReadOnlyList<DateOnly>> UpcomingDaysAsync(int technicianId, int take = 7, CancellationToken ct = default)
    {
        var today = Today;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Visits.AsNoTracking()
            .Where(v => v.Date >= today)
            .Join(db.ActiveJobs.Where(j => j.AssigneeId == technicianId && j.Status != JobStatus.Cancelled),
                v => v.JobId, j => j.Id, (v, j) => v.Date)
            .Distinct()
            .OrderBy(d => d)
            .Take(take)
            .ToListAsync(ct);
    }
}
