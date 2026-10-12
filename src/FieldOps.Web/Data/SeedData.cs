using FieldOps.Core.Jobs;
using FieldOps.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Data;

/// <summary>
/// Creates the database on first run and fills it with believable sample data. Everything is generated
/// from a fixed seed and dated relative to the day the database is created, so every fresh clone gets the
/// same picture: about fourteen months of history, today's stops for each technician, and a few chats.
/// Delete fieldops.db to start over.
/// </summary>
public sealed class SeedData
{
    /// <summary>The office everyone drives out from - the route planner's start point.</summary>
    public static readonly GeoPoint Depot = new(40.0150, -83.0120);

    private static readonly (string Name, double Lat, double Lng)[] Cities =
    [
        ("Brookfield", 40.0780, -83.0950), ("Fairview", 39.9650, -83.1120), ("Lakeside", 40.1120, -82.9480),
        ("Millbrook", 39.9420, -82.9350), ("Westover", 40.0300, -83.1650), ("Riverton", 40.0880, -82.8920),
        ("Oakdale", 39.9880, -82.8600), ("Pinecrest", 40.1450, -83.0400),
    ];

    private static readonly string[] Streets =
    [
        "Oak St", "Maple Ave", "Cedar Ln", "Elm Dr", "Willow Way", "Birch Rd", "Hillcrest Blvd",
        "River Rd", "Park Pl", "Sunset Ave", "Meadow Ct", "Lake Shore Dr", "Ridge Rd", "Orchard St",
    ];

    private static readonly string[] Clients =
    [
        "Cedar Ridge Homes", "Harbor Point Realty", "Northgate Builders", "Blue Fern Properties",
        "Summit Line Utilities", "Oakhaven Owners Association", "Riverbend Construction", "Granite Peak Partners",
        "Willow Creek Dental", "Lakeside Self Storage", "Brightwater Schools", "Copperfield Farms",
        "Evergreen Clinic", "Ironwood Logistics", "Juniper Hotels", "Kestrel Developments",
        "Larkspur Apartments", "Meadowlark Church",
    ];

    private static readonly string[] Services =
    [
        "Boundary survey", "Site inspection", "Equipment install", "Annual maintenance", "Drainage assessment",
        "Roof inspection", "Utility locate", "Topographic survey", "Fence line check", "Elevation certificate",
        "Safety audit", "Meter replacement",
    ];

    private readonly AppDbContext _db;
    private readonly DateOnly _today;
    private readonly DateTime _nowUtc;
    private readonly Random _random = new(20251);
    private int _nextReference = 1001;

    private SeedData(AppDbContext db, DateOnly today, DateTime nowUtc)
    {
        _db = db;
        _today = today;
        _nowUtc = nowUtc;
    }

    /// <summary>
    /// Creates the database if it does not exist and seeds it while it has no users. The seed is written in one
    /// transaction, so a first run stopped part-way leaves nothing behind and the next run simply seeds again.
    /// A database that already has data is left alone.
    /// </summary>
    public static async Task EnsureCreatedAsync(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        if (await db.Users.AnyAsync()) return;

        await using var transaction = await db.Database.BeginTransactionAsync();
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        new SeedData(db, today, clock.GetUtcNow().UtcDateTime).Populate();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private void Populate()
    {
        var office = new[]
        {
            new AppUser { Name = "Dana Whitfield", Title = "Operations lead", Role = UserRole.Office },
            new AppUser { Name = "Marcus Lee", Title = "Project coordinator", Role = UserRole.Office },
        };
        var field = new[]
        {
            new AppUser { Name = "Priya Shah", Title = "Field technician", Role = UserRole.Field },
            new AppUser { Name = "Tomas Rivera", Title = "Field technician", Role = UserRole.Field },
            new AppUser { Name = "Jordan Blake", Title = "Senior technician", Role = UserRole.Field },
        };
        _db.Users.AddRange(office);
        _db.Users.AddRange(field);
        _db.SaveChanges();

        var jobs = new List<Job>();

        // Fourteen months of intake with a gentle upward trend, so the dashboard forecast has something real to fit.
        for (int monthsAgo = 13; monthsAgo >= 0; monthsAgo--)
        {
            var monthStart = new DateOnly(_today.Year, _today.Month, 1).AddMonths(-monthsAgo);
            int daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
            int lastDay = monthsAgo == 0 ? _today.Day : daysInMonth;
            int count = Math.Max(3, (int)Math.Round(5 + 0.85 * (13 - monthsAgo) + _random.Next(-2, 3)));
            if (monthsAgo == 0) count = Math.Max(2, count * _today.Day / daysInMonth);

            for (int k = 0; k < count; k++)
            {
                var created = monthStart.AddDays(_random.Next(0, lastDay));
                jobs.Add(NewJob(created, field));
            }
        }

        // Today's and tomorrow's stops for every technician, so the route planner opens on a real day. Open work
        // each technician already has is booked first; only the shortfall becomes new jobs.
        foreach (var tech in field)
        {
            var workable = new Queue<Job>(jobs.Where(j => j.AssigneeId == tech.Id && j.Status is JobStatus.Scheduled or JobStatus.FieldWork));
            BookDay(tech, _today, 5 + _random.Next(0, 3), workable, jobs);
            BookDay(tech, _today.AddDays(1), 3 + _random.Next(0, 2), workable, jobs);

            // One missed visit each, so "overdue" has something real to show on the dashboard, list and board.
            jobs.Add(ScheduledJob(tech, _today.AddDays(-_random.Next(2, 6))));
        }

        foreach (var job in jobs)
        {
            job.Events.Add(new JobEvent
            {
                ActorId = office[_random.Next(office.Length)].Id,
                AtUtc = job.CreatedUtc,
                Summary = "Job created",
            });
            if (job.Status != JobStatus.Quote)
            {
                job.Events.Add(new JobEvent
                {
                    ActorId = job.AssigneeId ?? office[0].Id,
                    AtUtc = job.UpdatedUtc,
                    Summary = $"Status changed to {job.Status.Label()}",
                });
            }
        }

        _db.Jobs.AddRange(jobs);
        SeedChat(office, field, jobs);
    }

    private Job NewJob(DateOnly created, AppUser[] field)
    {
        int ageDays = _today.DayNumber - created.DayNumber;
        var status = PickStatus(ageDays);
        var tech = field[_random.Next(field.Length)];
        var job = CreateJob(created, status, status == JobStatus.Quote && _random.Next(3) == 0 ? null : tech);

        switch (status)
        {
            case JobStatus.Scheduled:
                // Mostly upcoming, a few already slipping past their date.
                AddVisit(job, _today.AddDays(_random.Next(5) == 0 ? -_random.Next(1, 6) : _random.Next(2, 14)), done: false);
                break;
            case JobStatus.FieldWork:
                AddVisit(job, NotAfterToday(created.AddDays(_random.Next(1, 5))), done: true);
                AddVisit(job, _today.AddDays(_random.Next(2, 10)), done: false);
                break;
            case JobStatus.Office or JobStatus.Delivered or JobStatus.Invoiced:
                var first = NotAfterToday(created.AddDays(_random.Next(2, 9)));
                AddVisit(job, first, done: true);
                if (_random.Next(3) == 0) AddVisit(job, NotAfterToday(first.AddDays(_random.Next(1, 4))), done: true);
                break;
            case JobStatus.OnHold:
                AddVisit(job, created.AddDays(_random.Next(3, 10)), done: false);
                break;
        }

        return job;
    }

    private void BookDay(AppUser tech, DateOnly day, int stops, Queue<Job> workable, List<Job> jobs)
    {
        for (int k = 0; k < stops; k++)
        {
            if (workable.TryDequeue(out var job)) AddVisit(job, day, done: false);
            else jobs.Add(ScheduledJob(tech, day));
        }
    }

    // Booked any time in the past year, so the extra jobs spread thinly across the intake chart instead of
    // piling into the last few weeks.
    private Job ScheduledJob(AppUser tech, DateOnly day)
    {
        var job = CreateJob(day.AddDays(-_random.Next(20, 330)), JobStatus.Scheduled, tech);
        AddVisit(job, day, done: false);
        return job;
    }

    private Job CreateJob(DateOnly created, JobStatus status, AppUser? assignee)
    {
        var city = Cities[_random.Next(Cities.Length)];
        var createdUtc = created.ToDateTime(new TimeOnly(8 + _random.Next(0, 9), _random.Next(0, 60)), DateTimeKind.Utc);
        var updatedUtc = createdUtc.AddDays(Math.Min(_random.Next(1, 20), Math.Max(0, (_nowUtc - createdUtc).TotalDays)));

        return new Job
        {
            Reference = $"J-{_nextReference++}",
            Title = Services[_random.Next(Services.Length)],
            Client = Clients[_random.Next(Clients.Length)],
            Address = $"{_random.Next(100, 9900)} {Streets[_random.Next(Streets.Length)]}",
            City = city.Name,
            Latitude = Math.Round(city.Lat + (_random.NextDouble() - 0.5) * 0.05, 5),
            Longitude = Math.Round(city.Lng + (_random.NextDouble() - 0.5) * 0.06, 5),
            Status = status,
            AssigneeId = assignee?.Id,
            CreatedUtc = createdUtc,
            UpdatedUtc = updatedUtc > _nowUtc ? _nowUtc : updatedUtc,
            Notes = _random.Next(4) == 0 ? "Gate code on file. Call the site contact 30 minutes before arrival." : null,
        };
    }

    private JobStatus PickStatus(int ageDays)
    {
        int roll = _random.Next(100);
        if (ageDays > 60)
            return roll switch { < 72 => JobStatus.Invoiced, < 82 => JobStatus.Delivered, < 90 => JobStatus.Cancelled, < 95 => JobStatus.OnHold, _ => JobStatus.Office };
        if (ageDays > 20)
            return roll switch { < 30 => JobStatus.Invoiced, < 55 => JobStatus.Delivered, < 80 => JobStatus.Office, < 92 => JobStatus.FieldWork, _ => JobStatus.OnHold };
        return roll switch { < 30 => JobStatus.Quote, < 65 => JobStatus.Scheduled, < 85 => JobStatus.FieldWork, _ => JobStatus.Office };
    }

    /// <summary>A completed visit can't be in the future, however recent the job is.</summary>
    private DateOnly NotAfterToday(DateOnly date) => date > _today ? _today : date;

    private void AddVisit(Job job, DateOnly date, bool done)
    {
        if (job.Visits.Any(v => v.Date == date)) return;
        job.Visits.Add(new Visit
        {
            Date = date,
            IsDone = done,
            Hours = done ? Math.Round((1.5 + _random.NextDouble() * 4.5) * 4) / 4 : null,
        });
    }

    private void SeedChat(AppUser[] office, AppUser[] field, List<Job> jobs)
    {
        var dana = office[0];
        var marcus = office[1];
        var priya = field[0];
        var jordan = field[2];
        string Ref(int i) => jobs[^(i + 1)].Reference;

        var minutes = 0;
        void Say(AppUser from, AppUser to, string body, bool read = true, bool done = false)
        {
            var sent = _nowUtc.AddHours(-6).AddMinutes(minutes += 7);
            _db.ChatMessages.Add(new ChatMessage
            {
                FromId = from.Id,
                ToId = to.Id,
                Body = body,
                SentUtc = sent,
                ReadUtc = read ? sent.AddMinutes(2) : null,
                DoneUtc = done ? sent.AddMinutes(30) : null,
                DoneById = done ? to.Id : null,
            });
        }

        Say(dana, priya, $"Morning! Can you take {Ref(0)} first today? The client asked for an early start.", done: true);
        Say(priya, dana, "Sure - heading there now. I'll update the visit when I'm done.");
        Say(marcus, jordan, $"The plans for {Ref(3)} were updated last night. Please check the new boundary before you start.");
        Say(jordan, marcus, "Got it. Is the site contact still the same person?");
        Say(marcus, jordan, "Yes, same contact. Gate code is in the job notes.", read: false);
        Say(priya, dana, $"Finished at {Ref(0)}. Logged 2.5 hours. Moving on to the next stop.", read: false);
        Say(jordan, dana, "Running about 20 minutes behind - traffic on River Rd.", read: false);
    }
}
