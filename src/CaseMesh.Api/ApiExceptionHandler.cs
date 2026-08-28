using CaseMesh.Persistence.Postgres;
using CaseMesh.Storage;
using Microsoft.AspNetCore.Diagnostics;

namespace CaseMesh.Api;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            PilotQuotaExceededException => (StatusCodes.Status429TooManyRequests, "Pilot limit reached", "The configured pilot resource limit was reached."),
            GeneratedArtifactExpiredException => (StatusCodes.Status410Gone, "Export expired", "The export retention window has expired."),
            GeneratedArtifactNotFoundException => (StatusCodes.Status404NotFound, "Resource not found", string.Empty),
            GeneratedArtifactIntegrityException => (StatusCodes.Status409Conflict, "Export integrity check failed", "The export could not pass integrity verification."),
            UnauthorizedAccessException => (StatusCodes.Status404NotFound, "Resource not found", string.Empty),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request", "The request was rejected."),
            InvalidDataException => (StatusCodes.Status400BadRequest, "Invalid request", "The request was rejected."),
            Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException =>
                (StatusCodes.Status400BadRequest, "Invalid request", "The request was rejected."),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request", "The request was rejected."),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Request conflict", "The request conflicts with the current state."),
            _ => (StatusCodes.Status500InternalServerError, "Request failed", "The request could not be completed.")
        };
        logger.LogWarning("API request failed with type {ExceptionType} and status {StatusCode}.",
            exception.GetType().Name, status);
        context.Response.StatusCode = status;
        context.Response.Headers["Cache-Control"] = "no-store, private";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";
        if (status == StatusCodes.Status404NotFound)
            return true;
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        };
        if (exception is PilotQuotaExceededException)
            PilotOperationsTelemetry.QuotaRejections.Add(1);
        if (exception is PilotQuotaExceededException quota) problem.Extensions["code"] = quota.Code;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem
        });
    }
}
