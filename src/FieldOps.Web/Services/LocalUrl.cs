using System.Diagnostics.CodeAnalysis;

namespace FieldOps.Web.Services;

/// <summary>
/// Return addresses (after sign-in, "Back to jobs") must stay on this site. Same rules as ASP.NET Core's
/// IsLocalUrl: one leading slash, and no control characters, because browsers drop tabs and newlines
/// and would read "/\t/example.com" as "//example.com".
/// </summary>
public static class LocalUrl
{
    public static bool IsLocal([NotNullWhen(true)] string? url) =>
        url is { Length: > 0 }
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl);
}
