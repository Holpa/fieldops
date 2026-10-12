namespace FieldOps.Core.Jobs;

/// <summary>Where a job is in its life, from first quote to final invoice.</summary>
public enum JobStatus
{
    Quote,
    Scheduled,
    FieldWork,
    Office,
    Delivered,
    Invoiced,
    OnHold,
    Cancelled
}

public static class JobStatusExtensions
{
    /// <summary>The human-readable name shown in the UI.</summary>
    public static string Label(this JobStatus status) => status switch
    {
        JobStatus.FieldWork => "Field work",
        JobStatus.OnHold => "On hold",
        _ => status.ToString()
    };

    /// <summary>The CSS modifier used for badges and board accents, e.g. "field-work".</summary>
    public static string CssKey(this JobStatus status) => status switch
    {
        JobStatus.FieldWork => "field-work",
        JobStatus.OnHold => "on-hold",
        _ => status.ToString().ToLowerInvariant()
    };
}
