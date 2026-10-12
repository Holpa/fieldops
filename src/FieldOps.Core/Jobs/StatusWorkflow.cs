namespace FieldOps.Core.Jobs;

/// <summary>
/// The allowed moves between job statuses. The editor, the board and the API all go through
/// <see cref="CanMove"/>, so the rules live in exactly one place and the UI only ever offers
/// moves the server will accept.
/// </summary>
public static class StatusWorkflow
{
    private static readonly IReadOnlyDictionary<JobStatus, JobStatus[]> Transitions =
        new Dictionary<JobStatus, JobStatus[]>
        {
            [JobStatus.Quote] = [JobStatus.Scheduled, JobStatus.OnHold, JobStatus.Cancelled],
            [JobStatus.Scheduled] = [JobStatus.FieldWork, JobStatus.Quote, JobStatus.OnHold, JobStatus.Cancelled],
            [JobStatus.FieldWork] = [JobStatus.Office, JobStatus.Scheduled, JobStatus.OnHold],
            [JobStatus.Office] = [JobStatus.Delivered, JobStatus.FieldWork, JobStatus.OnHold],
            [JobStatus.Delivered] = [JobStatus.Invoiced, JobStatus.Office],
            [JobStatus.Invoiced] = [],
            [JobStatus.OnHold] = [JobStatus.Quote, JobStatus.Scheduled, JobStatus.FieldWork, JobStatus.Office, JobStatus.Cancelled],
            [JobStatus.Cancelled] = [JobStatus.Quote],
        };

    /// <summary>The statuses a job can move to from <paramref name="from"/>, in display order.</summary>
    public static IReadOnlyList<JobStatus> NextSteps(JobStatus from) => Transitions[from];

    /// <summary>The moves this person may make: field technicians don't get the office-only ones.</summary>
    public static IReadOnlyList<JobStatus> NextSteps(JobStatus from, bool isOffice) =>
        isOffice ? Transitions[from] : Transitions[from].Where(to => !IsOfficeOnly(from, to)).ToArray();

    /// <summary>True when the move is allowed. Staying on the same status is always allowed.</summary>
    public static bool CanMove(JobStatus from, JobStatus to) => from == to || Transitions[from].Contains(to);

    /// <summary>
    /// Billing and cancelling are office decisions. Invoiced is final, so a technician must not be able to
    /// close their own job; cancelling a job, or bringing a cancelled one back, is the office's call too.
    /// </summary>
    public static bool IsOfficeOnly(JobStatus from, JobStatus to) =>
        from != to && (to is JobStatus.Invoiced or JobStatus.Cancelled || from == JobStatus.Cancelled);

    /// <summary>Open jobs still need work. Invoiced and cancelled jobs are closed.</summary>
    public static bool IsOpen(JobStatus status) => status is not (JobStatus.Invoiced or JobStatus.Cancelled);

    /// <summary>
    /// Open and not paused. Only active work can be overdue: a job on hold with a visit date in the past is
    /// waiting on purpose, not late.
    /// </summary>
    public static bool IsActive(JobStatus status) => IsOpen(status) && status != JobStatus.OnHold;
}
