using System.Security.Claims;
using FieldOps.Core.Jobs;
using FieldOps.Web.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FieldOps.Web.Endpoints;

public sealed record StatusChange(JobStatus Status);

/// <summary>
/// A small REST API over the same <see cref="JobService"/> the UI uses, so both front doors share one set of
/// rules. It uses the sign-in cookie: sign in in the browser, then open /api/jobs in the same browser.
/// </summary>
public static class JobsApi
{
    public static IEndpointRouteBuilder MapJobsApi(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/jobs").RequireAuthorization();

        jobs.MapGet("/", async ([AsParameters] JobQuery query, JobService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SearchAsync(query, ct)));

        jobs.MapGet("/{id:int}", async Task<Results<Ok<JobDetail>, ProblemHttpResult>> (int id, JobService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } job ? TypedResults.Ok(job) : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound));

        jobs.MapPatch("/{id:int}/status", async (int id, StatusChange change, ClaimsPrincipal principal, JobService service, CancellationToken ct) =>
        {
            if (UserContext.From(principal) is not { } user) return Results.Unauthorized();
            var result = await service.MoveAsync(id, change.Status, user, ct);
            return ToHttp(result);
        });

        return app;
    }

    private static IResult ToHttp(ServiceResult result) => result.Kind switch
    {
        ResultKind.Ok => TypedResults.NoContent(),
        ResultKind.NotFound => TypedResults.Problem(result.Message, statusCode: StatusCodes.Status404NotFound),
        ResultKind.Forbidden => TypedResults.Problem(result.Message, statusCode: StatusCodes.Status403Forbidden),
        ResultKind.Conflict => TypedResults.Problem(result.Message, statusCode: StatusCodes.Status409Conflict),
        _ => TypedResults.Problem(result.Message, statusCode: StatusCodes.Status400BadRequest),
    };
}
