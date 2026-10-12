using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using FieldOps.Core.Auditing;
using FieldOps.Core.Jobs;
using FieldOps.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Services;

/// <summary>
/// Reads and changes jobs. Every change checks permission, validates against the status workflow, and
/// writes its activity-log entry in the same SaveChanges as the change itself, so the log can never miss
/// an edit. Edits use optimistic concurrency: a save made against an out-of-date copy is rejected rather
/// than silently overwriting someone else's work.
/// </summary>
public sealed class JobService(IDbContextFactory<AppDbContext> dbFactory, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const int SqliteUniqueConstraint = 2067; // SQLITE_CONSTRAINT_UNIQUE

    private static readonly Expression<Func<Job, int>> StatusOrder = j =>
        j.Status == JobStatus.Quote ? 0 :
        j.Status == JobStatus.Scheduled ? 1 :
        j.Status == JobStatus.FieldWork ? 2 :
        j.Status == JobStatus.Office ? 3 :
        j.Status == JobStatus.Delivered ? 4 :
        j.Status == JobStatus.Invoiced ? 5 :
        j.Status == JobStatus.OnHold ? 6 : 7;

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------- queries

    public async Task<PagedResult<JobRow>> SearchAsync(JobQuery query, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = Today;
        var jobs = Filter(db.ActiveJobs.AsNoTracking(), query, today);

        int total = await jobs.CountAsync(ct);
        int pageSize = Math.Clamp(query.PageSize, 5, 100);
        int lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        int page = Math.Clamp(query.Page, 1, lastPage);

        var rows = await Sort(jobs, query.Sort, query.Desc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(j => new
            {
                j.Id,
                j.Reference,
                j.Title,
                j.Client,
                j.City,
                j.Status,
                AssigneeName = j.Assignee != null ? j.Assignee.Name : null,
                NextVisit = j.Visits.Where(v => !v.IsDone).OrderBy(v => v.Date).Select(v => (DateOnly?)v.Date).FirstOrDefault(),
                j.UpdatedUtc,
            })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new JobRow(r.Id, r.Reference, r.Title, r.Client, r.City, r.Status, r.AssigneeName,
                r.NextVisit, StatusWorkflow.IsActive(r.Status) && r.NextVisit < today, r.UpdatedUtc))
            .ToList();

        return new PagedResult<JobRow>(items, total, page, pageSize);
    }

    public async Task<JobDetail?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.ActiveJobs.AsNoTracking()
            .Include(j => j.Assignee)
            .Include(j => j.Visits)
            .FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return null;

        var events = await db.JobEvents.AsNoTracking()
            .Where(e => e.JobId == id)
            .OrderByDescending(e => e.AtUtc).ThenByDescending(e => e.Id)
            .Select(e => new { e.AtUtc, ActorName = e.Actor!.Name, e.Summary, e.ChangesJson })
            .ToListAsync(ct);

        return new JobDetail(
            job.Id,
            job.Reference,
            ToInput(job),
            job.Version,
            job.Assignee?.Name,
            job.CreatedUtc,
            job.UpdatedUtc,
            job.Visits.OrderBy(v => v.Date).Select(v => new VisitRow(v.Id, v.Date, v.IsDone, v.Hours)).ToList(),
            VisitSchedule.From(job.Visits.Select(v => new VisitInfo(v.Date, v.IsDone))),
            events.Select(e => new EventRow(e.AtUtc, e.ActorName, e.Summary, ReadChanges(e.ChangesJson))).ToList());
    }

    public async Task<IReadOnlyList<BoardCard>> BoardAsync(CancellationToken ct = default)
    {
        var today = Today;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ActiveJobs.AsNoTracking()
            .Where(j => j.Status != JobStatus.Cancelled)
            .Select(j => new
            {
                j.Id,
                j.Reference,
                j.Title,
                j.Client,
                j.Status,
                j.AssigneeId,
                AssigneeName = j.Assignee != null ? j.Assignee.Name : null,
                NextVisit = j.Visits.Where(v => !v.IsDone).OrderBy(v => v.Date).Select(v => (DateOnly?)v.Date).FirstOrDefault(),
                j.UpdatedUtc,
            })
            .ToListAsync(ct);

        return rows
            .OrderByDescending(r => r.UpdatedUtc)
            .Select(r => new BoardCard(r.Id, r.Reference, r.Title, r.Client, r.Status, r.AssigneeId,
                r.AssigneeName is null ? null : AppUser.InitialsOf(r.AssigneeName), r.NextVisit,
                StatusWorkflow.IsActive(r.Status) && r.NextVisit < today, r.UpdatedUtc))
            .ToList();
    }

    public async Task<IReadOnlyList<UserOption>> FieldTechniciansAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Field)
            .OrderBy(u => u.Name)
            .Select(u => new UserOption(u.Id, u.Name))
            .ToListAsync(ct);
    }

    // ---------------------------------------------------------------- job commands

    public async Task<ServiceResult<int>> CreateAsync(JobInput input, UserContext user, CancellationToken ct = default)
    {
        if (!user.IsOffice) return ServiceResult.Forbidden("Only office staff can create jobs.");
        if (Validate(input) is { } problem) return ServiceResult.Invalid(problem);
        if (input.Status is not (JobStatus.Quote or JobStatus.Scheduled))
            return ServiceResult.Invalid("A new job starts as a quote or as scheduled.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await IsValidAssigneeAsync(db, input.AssigneeId, ct))
            return ServiceResult.Invalid("Jobs can only be assigned to a field technician.");

        var now = UtcNow;
        var job = new Job
        {
            Reference = await NextReferenceAsync(db, ct),
            Title = input.Title,
            Client = input.Client,
            Address = input.Address,
            City = input.City,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        Apply(job, input);
        job.Events.Add(new JobEvent { ActorId = user.Id, AtUtc = now, Summary = "Job created" });

        db.Jobs.Add(job);
        var saved = await SaveAsync(db, ct);
        return saved.IsOk ? ServiceResult<int>.Ok(job.Id) : saved;
    }

    public async Task<ServiceResult> UpdateAsync(int id, JobInput input, int expectedVersion, UserContext user, CancellationToken ct = default)
    {
        if (!user.IsOffice) return ServiceResult.Forbidden("Only office staff can edit job details.");
        if (Validate(input) is { } problem) return ServiceResult.Invalid(problem);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.ActiveJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return ServiceResult.NotFound();
        if (job.Version != expectedVersion) return ServiceResult.Conflict();
        if (!StatusWorkflow.CanMove(job.Status, input.Status))
            return ServiceResult.Invalid($"A job can't move from {job.Status.Label()} to {input.Status.Label()}.");
        if (!await IsValidAssigneeAsync(db, input.AssigneeId, ct))
            return ServiceResult.Invalid("Jobs can only be assigned to a field technician.");

        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var before = Snapshot(job, names);
        Apply(job, input);
        var changes = FieldDiff.Compare(before, Snapshot(job, names));
        if (changes.Count == 0) return ServiceResult.Ok();

        job.Version++;
        Log(job, user, Describe(changes), changes);
        return await SaveAsync(db, ct);
    }

    public async Task<ServiceResult> MoveAsync(int id, JobStatus to, UserContext user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.ActiveJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return ServiceResult.NotFound();
        if (!CanWorkOn(job, user)) return ServiceResult.Forbidden("Only the office or the assigned technician can move this job.");
        if (job.Status == to) return ServiceResult.Ok();
        if (!StatusWorkflow.CanMove(job.Status, to))
            return ServiceResult.Invalid($"{job.Reference} can't move from {job.Status.Label()} to {to.Label()}.");
        if (!user.IsOffice && StatusWorkflow.IsOfficeOnly(job.Status, to))
            return ServiceResult.Forbidden("Only office staff can invoice, cancel or reopen a job.");

        var change = new FieldChange("Status", job.Status.Label(), to.Label());
        job.Status = to;
        job.Version++;
        Log(job, user, $"Status: {change.Before} → {change.After}", [change]);
        return await SaveAsync(db, ct);
    }

    public async Task<ServiceResult> DeleteAsync(int id, UserContext user, CancellationToken ct = default)
    {
        if (!user.IsOffice) return ServiceResult.Forbidden("Only office staff can delete jobs.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.ActiveJobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return ServiceResult.NotFound();

        job.IsDeleted = true;
        job.Version++;
        Log(job, user, "Job deleted");
        return await SaveAsync(db, ct);
    }

    // ---------------------------------------------------------------- visit commands
    // Visits are their own rows, so changing one does not bump the job's version: a technician logging
    // hours never invalidates an office edit that is open on the same job.

    public async Task<ServiceResult> AddVisitAsync(int jobId, DateOnly date, UserContext user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var job = await db.ActiveJobs.Include(j => j.Visits).FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return ServiceResult.NotFound();
        if (!CanChangeVisits(job.Status, job.AssigneeId, user)) return ServiceResult.Forbidden();
        if (job.Visits.Any(v => v.Date == date))
            return ServiceResult.Invalid($"This job already has a visit on {date.ToString("MMM d", CultureInfo.CurrentCulture)}.");

        job.Visits.Add(new Visit { Date = date });
        Log(job, user, $"Visit added for {date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)}");
        return await SaveAsync(db, ct);
    }

    public async Task<ServiceResult> SetVisitDoneAsync(int visitId, bool done, UserContext user, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (visit, job) = await LoadVisitAsync(db, visitId, ct);
        if (visit is null || job is null) return ServiceResult.NotFound();
        if (!CanChangeVisits(job.Status, job.AssigneeId, user)) return ServiceResult.Forbidden();
        if (visit.IsDone == done) return ServiceResult.Ok();
        if (done && visit.Date > Today) return ServiceResult.Invalid("A visit can't be marked done before its date.");

        visit.IsDone = done;
        Log(job, user, $"Visit on {visit.Date.ToString("MMM d", CultureInfo.CurrentCulture)} {(done ? "completed" : "reopened")}");
        return await SaveAsync(db, ct);
    }

    public async Task<ServiceResult> SetVisitHoursAsync(int visitId, double? hours, UserContext user, CancellationToken ct = default)
    {
        if (hours is < 0 or > 24) return ServiceResult.Invalid("Hours must be between 0 and 24.");
        var rounded = hours is { } h ? Math.Round(h * 4, MidpointRounding.AwayFromZero) / 4 : (double?)null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (visit, job) = await LoadVisitAsync(db, visitId, ct);
        if (visit is null || job is null) return ServiceResult.NotFound();
        if (!CanChangeVisits(job.Status, job.AssigneeId, user)) return ServiceResult.Forbidden();
        if (visit.Hours == rounded) return ServiceResult.Ok();

        var change = new FieldChange("Hours", FormatHours(visit.Hours), FormatHours(rounded));
        visit.Hours = rounded;
        Log(job, user, $"Hours on {visit.Date.ToString("MMM d", CultureInfo.CurrentCulture)} updated", [change]);
        return await SaveAsync(db, ct);
    }

    public async Task<ServiceResult> RemoveVisitAsync(int visitId, UserContext user, CancellationToken ct = default)
    {
        if (!user.IsOffice) return ServiceResult.Forbidden("Only office staff can remove visits.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (visit, job) = await LoadVisitAsync(db, visitId, ct);
        if (visit is null || job is null) return ServiceResult.NotFound();

        db.Visits.Remove(visit);
        Log(job, user, $"Visit on {visit.Date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)} removed");
        return await SaveAsync(db, ct);
    }

    // ---------------------------------------------------------------- helpers

    private static IQueryable<Job> Filter(IQueryable<Job> jobs, JobQuery query, DateOnly today)
    {
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            // LIKE is case-insensitive in SQLite; user input is escaped so % and _ are matched literally.
            var pattern = "%" + EscapeLike(query.Q.Trim()) + "%";
            jobs = jobs.Where(j =>
                EF.Functions.Like(j.Reference, pattern, @"\") ||
                EF.Functions.Like(j.Title, pattern, @"\") ||
                EF.Functions.Like(j.Client, pattern, @"\") ||
                EF.Functions.Like(j.Address, pattern, @"\") ||
                EF.Functions.Like(j.City, pattern, @"\"));
        }

        if (query.Status is { } status) jobs = jobs.Where(j => j.Status == status);
        if (query.Assignee is { } assignee) jobs = jobs.Where(j => j.AssigneeId == assignee);
        if (query.Open) jobs = jobs.Where(j => j.Status != JobStatus.Invoiced && j.Status != JobStatus.Cancelled);
        if (query.Overdue)
        {
            // Overdue means active work with a past visit still not done; on-hold jobs are paused, not late.
            jobs = jobs.Where(j => j.Status != JobStatus.Invoiced && j.Status != JobStatus.Cancelled && j.Status != JobStatus.OnHold
                && j.Visits.Any(v => !v.IsDone && v.Date < today));
        }
        return jobs;
    }

    private static IQueryable<Job> Sort(IQueryable<Job> jobs, string? sort, bool desc)
    {
        IOrderedQueryable<Job> ordered = sort switch
        {
            "ref" => desc ? jobs.OrderByDescending(j => j.Reference) : jobs.OrderBy(j => j.Reference),
            "title" => desc ? jobs.OrderByDescending(j => j.Title) : jobs.OrderBy(j => j.Title),
            "client" => desc ? jobs.OrderByDescending(j => j.Client) : jobs.OrderBy(j => j.Client),
            "city" => desc ? jobs.OrderByDescending(j => j.City) : jobs.OrderBy(j => j.City),
            "status" => desc ? jobs.OrderByDescending(StatusOrder) : jobs.OrderBy(StatusOrder),
            "next" => (desc
                    ? jobs.OrderBy(j => j.Visits.Any(v => !v.IsDone) ? 0 : 1).ThenByDescending(j => j.Visits.Where(v => !v.IsDone).Min(v => (DateOnly?)v.Date))
                    : jobs.OrderBy(j => j.Visits.Any(v => !v.IsDone) ? 0 : 1).ThenBy(j => j.Visits.Where(v => !v.IsDone).Min(v => (DateOnly?)v.Date))),
            "updated" => desc ? jobs.OrderByDescending(j => j.UpdatedUtc) : jobs.OrderBy(j => j.UpdatedUtc),
            _ => jobs.OrderByDescending(j => j.UpdatedUtc),
        };

        // A unique tie-breaker keeps paging stable when many rows share the sort value.
        return ordered.ThenBy(j => j.Id);
    }

    private static string EscapeLike(string text) => text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static bool CanWorkOn(Job job, UserContext user) => user.IsOffice || job.AssigneeId == user.Id;

    /// <summary>Invoiced and cancelled jobs are closed records: only the office changes their visits and hours.</summary>
    public static bool CanChangeVisits(JobStatus status, int? assigneeId, UserContext user) =>
        user.IsOffice || (assigneeId == user.Id && StatusWorkflow.IsOpen(status));

    private static async Task<bool> IsValidAssigneeAsync(AppDbContext db, int? assigneeId, CancellationToken ct) =>
        assigneeId is not { } id || await db.Users.AnyAsync(u => u.Id == id && u.Role == UserRole.Field, ct);

    /// <summary>Re-runs the form's validation rules on the server - the browser is never trusted.</summary>
    private static string? Validate(JobInput input)
    {
        var results = new List<ValidationResult>();
        return Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true)
            ? null
            : results[0].ErrorMessage;
    }

    private static async Task<string> NextReferenceAsync(AppDbContext db, CancellationToken ct)
    {
        // Deleted jobs are included on purpose: a reference is never handed out twice.
        var last = await db.Jobs.OrderByDescending(j => j.Id).Select(j => j.Reference).FirstOrDefaultAsync(ct);
        int number = last is not null && int.TryParse(last.AsSpan(2), CultureInfo.InvariantCulture, out var n) ? n : 1000;
        return $"J-{number + 1}";
    }

    private static async Task<(Visit? Visit, Job? Job)> LoadVisitAsync(AppDbContext db, int visitId, CancellationToken ct)
    {
        var visit = await db.Visits.FirstOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit is null) return (null, null);
        var job = await db.ActiveJobs.FirstOrDefaultAsync(j => j.Id == visit.JobId, ct);
        return (visit, job);
    }

    private void Log(Job job, UserContext user, string summary, IReadOnlyList<FieldChange>? changes = null)
    {
        job.UpdatedUtc = UtcNow;
        job.Events.Add(new JobEvent
        {
            ActorId = user.Id,
            AtUtc = job.UpdatedUtc,
            Summary = summary,
            ChangesJson = changes is { Count: > 0 } ? JsonSerializer.Serialize(changes, Json) : null,
        });
    }

    private static async Task<ServiceResult> SaveAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return ServiceResult.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ServiceResult.Conflict();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteUniqueConstraint })
        {
            // A unique index caught a clash with a save made at the same moment (a job reference, or a second
            // visit on the same day). Nothing was written; trying again reads the latest state.
            return ServiceResult.Conflict("Someone else saved a clashing change at the same moment. Please try again.");
        }
    }

    private static void Apply(Job job, JobInput input)
    {
        job.Title = input.Title.Trim();
        job.Client = input.Client.Trim();
        job.Address = input.Address.Trim();
        job.City = input.City.Trim();
        job.Latitude = Math.Round(input.Latitude, 5);
        job.Longitude = Math.Round(input.Longitude, 5);
        job.Status = input.Status;
        job.AssigneeId = input.AssigneeId;
        job.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
    }

    private static JobInput ToInput(Job job) => new()
    {
        Title = job.Title,
        Client = job.Client,
        Address = job.Address,
        City = job.City,
        Latitude = job.Latitude,
        Longitude = job.Longitude,
        Status = job.Status,
        AssigneeId = job.AssigneeId,
        Notes = job.Notes,
    };

    private static Dictionary<string, string?> Snapshot(Job job, IReadOnlyDictionary<int, string> names) => new()
    {
        ["Title"] = job.Title,
        ["Client"] = job.Client,
        ["Address"] = job.Address,
        ["City"] = job.City,
        ["Location"] = string.Create(CultureInfo.InvariantCulture, $"{job.Latitude:F5}, {job.Longitude:F5}"),
        ["Status"] = job.Status.Label(),
        ["Assignee"] = job.AssigneeId is { } id && names.TryGetValue(id, out var name) ? name : null,
        ["Notes"] = job.Notes,
    };

    private static string Describe(IReadOnlyList<FieldChange> changes) => changes switch
    {
        [{ Field: "Status" } only] => $"Status: {only.Before} → {only.After}",
        [var only] => $"Changed {only.Field.ToLowerInvariant()}",
        _ => $"Changed {changes.Count} fields",
    };

    private static IReadOnlyList<FieldChange> ReadChanges(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<FieldChange>>(json, Json) ?? [];

    private static string? FormatHours(double? hours) => hours?.ToString("0.##", CultureInfo.InvariantCulture);

}
