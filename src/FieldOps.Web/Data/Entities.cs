using FieldOps.Core.Jobs;

namespace FieldOps.Web.Data;

public enum UserRole
{
    Office,
    Field
}

public class AppUser
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Title { get; set; }
    public UserRole Role { get; set; }

    public string Initials => InitialsOf(Name);

    /// <summary>"Dana Whitfield" becomes "DW": the text shown in avatar circles.</summary>
    public static string InitialsOf(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? "?"
            : string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => part[0])).ToUpperInvariant();
}

public class Job
{
    public int Id { get; set; }
    public required string Reference { get; set; }
    public required string Title { get; set; }
    public required string Client { get; set; }
    public required string Address { get; set; }
    public required string City { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public JobStatus Status { get; set; }
    public int? AssigneeId { get; set; }
    public AppUser? Assignee { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Optimistic-concurrency token: bumped by every edit, status move and delete, and checked on every update.
    /// Visit changes leave it alone, so a technician logging hours never invalidates an open office edit.
    /// </summary>
    public int Version { get; set; }

    public List<Visit> Visits { get; set; } = [];
    public List<JobEvent> Events { get; set; } = [];
}

public class Visit
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public DateOnly Date { get; set; }
    public bool IsDone { get; set; }
    public double? Hours { get; set; }
}

/// <summary>One entry in a job's activity log.</summary>
public class JobEvent
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public DateTime AtUtc { get; set; }
    public int ActorId { get; set; }
    public AppUser? Actor { get; set; }
    public required string Summary { get; set; }

    /// <summary>Field-level changes as JSON, when the event changed fields.</summary>
    public string? ChangesJson { get; set; }
}

public class ChatMessage
{
    public int Id { get; set; }
    public int FromId { get; set; }
    public int ToId { get; set; }
    public required string Body { get; set; }
    public DateTime SentUtc { get; set; }
    public DateTime? ReadUtc { get; set; }

    /// <summary>Set when either participant marks the message as handled.</summary>
    public DateTime? DoneUtc { get; set; }
    public int? DoneById { get; set; }
}
