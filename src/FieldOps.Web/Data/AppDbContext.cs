using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FieldOps.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    /// <summary>Jobs that have not been deleted. Deletes are soft, so history stays intact.</summary>
    public IQueryable<Job> ActiveJobs => Jobs.Where(j => !j.IsDeleted);

    /// <summary>
    /// Every DateTime in the model is UTC. SQLite keeps no time zone, so values read back are marked as UTC
    /// again: the API then writes them with a trailing "Z" and nothing has to guess.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configuration) =>
        configuration.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AppUser>(user =>
        {
            user.Property(u => u.Name).HasMaxLength(80);
            user.Property(u => u.Title).HasMaxLength(80);
            user.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
            user.Ignore(u => u.Initials);
        });

        model.Entity<Job>(job =>
        {
            job.HasIndex(j => j.Reference).IsUnique();
            job.HasIndex(j => j.Status);
            job.Property(j => j.Reference).HasMaxLength(20);
            job.Property(j => j.Title).HasMaxLength(120);
            job.Property(j => j.Client).HasMaxLength(120);
            job.Property(j => j.Address).HasMaxLength(160);
            job.Property(j => j.City).HasMaxLength(80);
            job.Property(j => j.Notes).HasMaxLength(2000);
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            job.Property(j => j.Version).IsConcurrencyToken();
            job.HasOne(j => j.Assignee).WithMany().HasForeignKey(j => j.AssigneeId).OnDelete(DeleteBehavior.SetNull);
            job.HasMany(j => j.Visits).WithOne().HasForeignKey(v => v.JobId).OnDelete(DeleteBehavior.Cascade);
            job.HasMany(j => j.Events).WithOne().HasForeignKey(e => e.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        // One visit per job per day: a second visit on the same day is the same visit.
        model.Entity<Visit>().HasIndex(v => new { v.JobId, v.Date }).IsUnique();

        model.Entity<JobEvent>(evt =>
        {
            evt.HasIndex(e => e.JobId);
            evt.Property(e => e.Summary).HasMaxLength(200);
            evt.HasOne(e => e.Actor).WithMany().HasForeignKey(e => e.ActorId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ChatMessage>(message =>
        {
            message.Property(m => m.Body).HasMaxLength(2000);
            message.HasIndex(m => new { m.ToId, m.ReadUtc });
            message.HasIndex(m => new { m.FromId, m.ToId, m.SentUtc });
        });
    }
}
