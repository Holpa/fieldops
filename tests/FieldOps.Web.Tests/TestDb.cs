using FieldOps.Core.Jobs;
using FieldOps.Web.Data;
using FieldOps.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Web.Tests;

/// <summary>
/// A context factory over one in-memory SQLite database. The connection stays open until the factory is
/// disposed, because an in-memory database disappears when its last connection closes.
/// </summary>
internal sealed class TestDbFactory : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public TestDbFactory()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public AppDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// A clock stopped at one moment, in UTC unless a zone is given, so "today" never depends on when or where the
/// tests run.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? zone = null) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;

    public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;
}

/// <summary>Minimal sample rows for a test. Every name is invented.</summary>
internal static class Seed
{
    public static UserContext User(TestDbFactory factory, string name, UserRole role)
    {
        using var db = factory.CreateDbContext();
        var user = new AppUser { Name = name, Title = role == UserRole.Office ? "Office" : "Field technician", Role = role };
        db.Users.Add(user);
        db.SaveChanges();
        return new UserContext(user.Id, user.Name, user.Role);
    }

    public static int Job(TestDbFactory factory, string reference, JobStatus status, int? assigneeId, DateTime nowUtc)
    {
        using var db = factory.CreateDbContext();
        var job = new Job
        {
            Reference = reference,
            Title = "Fix boiler",
            Client = "Harbor Bakery",
            Address = "12 Quay Street",
            City = "Fairview",
            Status = status,
            AssigneeId = assigneeId,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
        };
        db.Jobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }
}
