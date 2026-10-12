namespace FieldOps.Core.Jobs;

/// <summary>One site visit: the day it happens and whether it has been completed.</summary>
public readonly record struct VisitInfo(DateOnly Date, bool IsDone);

/// <summary>
/// A job's schedule, derived from its visits every time it is needed. Nothing stores a separate
/// "scheduled date" that could drift away from the visits themselves.
/// </summary>
public sealed record VisitSchedule(DateOnly? NextVisit, DateOnly? LastCompleted, int Total, int Done)
{
    public static readonly VisitSchedule Empty = new(null, null, 0, 0);

    /// <summary>The next visit is in the past and still not done.</summary>
    public bool IsOverdue(DateOnly today) => NextVisit is { } next && next < today;

    public static VisitSchedule From(IEnumerable<VisitInfo> visits)
    {
        DateOnly? next = null;
        DateOnly? last = null;
        int total = 0, done = 0;

        foreach (var visit in visits)
        {
            total++;
            if (visit.IsDone)
            {
                done++;
                if (last is null || visit.Date > last) last = visit.Date;
            }
            else if (next is null || visit.Date < next)
            {
                next = visit.Date;
            }
        }

        return total == 0 ? Empty : new VisitSchedule(next, last, total, done);
    }
}
