namespace FieldOps.Core.Auditing;

/// <summary>One field that changed between two versions of a record.</summary>
public sealed record FieldChange(string Field, string? Before, string? After);

/// <summary>
/// Compares two snapshots of a record, field by field, for the activity log. Blank and
/// whitespace-only values count as empty, so re-saving an untouched form records nothing.
/// </summary>
public static class FieldDiff
{
    public static IReadOnlyList<FieldChange> Compare(
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var fields = before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal);
        var changes = new List<FieldChange>();

        foreach (var field in fields)
        {
            var oldValue = Normalize(before.GetValueOrDefault(field));
            var newValue = Normalize(after.GetValueOrDefault(field));
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                changes.Add(new FieldChange(field, oldValue, newValue));
        }

        return changes;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
