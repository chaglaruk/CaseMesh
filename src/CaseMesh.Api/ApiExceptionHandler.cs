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
        if (status == StatusCodes.Status404NotFound)
            return true;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = status, Title = title, Detail = detail
            }
        });
    }
}
