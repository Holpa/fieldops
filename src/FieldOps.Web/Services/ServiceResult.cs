namespace FieldOps.Web.Services;

public enum ResultKind
{
    Ok,
    NotFound,
    Forbidden,
    Invalid,
    Conflict
}

/// <summary>
/// The outcome of a command. Services report expected failures (not found, not allowed, invalid,
/// edited by someone else) as values; the UI shows the message and the API maps the kind to a status code.
/// </summary>
public readonly record struct ServiceResult(ResultKind Kind, string? Message = null)
{
    public bool IsOk => Kind == ResultKind.Ok;

    public static ServiceResult Ok() => new(ResultKind.Ok);
    public static ServiceResult NotFound(string message = "That record no longer exists.") => new(ResultKind.NotFound, message);
    public static ServiceResult Forbidden(string message = "You don't have permission to do that.") => new(ResultKind.Forbidden, message);
    public static ServiceResult Invalid(string message) => new(ResultKind.Invalid, message);
    public static ServiceResult Conflict(string message = "Someone else changed this job while you were editing it.") => new(ResultKind.Conflict, message);
}

public readonly record struct ServiceResult<T>(ResultKind Kind, T? Value = default, string? Message = null)
{
    public bool IsOk => Kind == ResultKind.Ok;

    public static ServiceResult<T> Ok(T value) => new(ResultKind.Ok, value);

    public static implicit operator ServiceResult<T>(ServiceResult failure) => new(failure.Kind, default, failure.Message);
}
