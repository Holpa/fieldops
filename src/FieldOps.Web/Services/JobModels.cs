using System.ComponentModel.DataAnnotations;
using FieldOps.Core.Auditing;
using FieldOps.Core.Jobs;

namespace FieldOps.Web.Services;

/// <summary>
/// Search, filter, sort and paging options for the jobs list and the API. Every field round-trips through the URL.
/// The defaults are constructor defaults so the API's query-string binding treats every value as optional.
/// </summary>
public sealed record JobQuery(
    string? Q = null,
    JobStatus? Status = null,
    int? Assignee = null,
    bool Open = false,
    bool Overdue = false,
    string? Sort = null,
    bool Desc = false,
    int Page = 1,
    int PageSize = 15);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed record JobRow(
    int Id,
    string Reference,
    string Title,
    string Client,
    string City,
    JobStatus Status,
    string? AssigneeName,
    DateOnly? NextVisit,
    bool IsOverdue,
    DateTime UpdatedUtc);

public sealed record VisitRow(int Id, DateOnly Date, bool IsDone, double? Hours);

public sealed record EventRow(DateTime AtUtc, string ActorName, string Summary, IReadOnlyList<FieldChange> Changes);

public sealed record JobDetail(
    int Id,
    string Reference,
    JobInput Input,
    int Version,
    string? AssigneeName,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<VisitRow> Visits,
    VisitSchedule Schedule,
    IReadOnlyList<EventRow> Events);

public sealed record BoardCard(
    int Id,
    string Reference,
    string Title,
    string Client,
    JobStatus Status,
    int? AssigneeId,
    string? AssigneeInitials,
    DateOnly? NextVisit,
    bool IsOverdue,
    DateTime UpdatedUtc);

public sealed record UserOption(int Id, string Name);

/// <summary>The editable fields of a job, with the validation rules the form and the service both enforce.</summary>
public sealed class JobInput
{
    [Required, StringLength(120)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(120)]
    public string Client { get; set; } = string.Empty;

    [Required, StringLength(160)]
    public string Address { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string City { get; set; } = string.Empty;

    [Range(-90, 90)]
    public double Latitude { get; set; }

    [Range(-180, 180)]
    public double Longitude { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Quote;

    public int? AssigneeId { get; set; }

    [StringLength(2000)]
    public string? Notes { get; set; }

    public JobInput Clone() => (JobInput)MemberwiseClone();
}
