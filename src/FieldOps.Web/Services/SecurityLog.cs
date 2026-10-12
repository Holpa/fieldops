using System.Net;
using FieldOps.Web.Data;

namespace FieldOps.Web.Services;

/// <summary>
/// Security events, written through ILogger under one category so they can be filtered and kept together.
/// They carry user ids, roles, paths and client addresses only: never cookies, tokens or message text.
/// </summary>
public static partial class SecurityLog
{
    public const string Category = "FieldOps.Security";

    public static ILogger For(ILoggerFactory loggerFactory) => loggerFactory.CreateLogger(Category);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "User {UserId} ({Role}) signed in from {ClientIp}")]
    public static partial void SignedIn(this ILogger logger, int userId, UserRole role, IPAddress? clientIp);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "User {UserId} signed out")]
    public static partial void SignedOut(this ILogger logger, int? userId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Permission denied to user {UserId} ({Role}) in {Operation}: {Reason}")]
    public static partial void PermissionDenied(this ILogger logger, int userId, UserRole role, string operation, string? reason);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Signed-out API request to {Path} from {ClientIp} answered 401")]
    public static partial void ApiUnauthenticated(this ILogger logger, PathString path, IPAddress? clientIp);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Rate limit {Policy} refused {PartitionKey} on {Path}")]
    public static partial void RateLimited(this ILogger logger, string policy, string partitionKey, PathString path);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Chat send limit refused user {UserId}")]
    public static partial void ChatRateLimited(this ILogger logger, int userId);
}
