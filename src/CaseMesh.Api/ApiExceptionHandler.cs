using Microsoft.AspNetCore.Diagnostics;

namespace CaseMesh.Api;

public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            UnauthorizedAccessException => (StatusCodes.Status404NotFound, "Resource not found"),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Request conflict"),
            _ => (StatusCodes.Status500InternalServerError, "Request failed")
        };
        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = status, Title = title,
                Detail = status >= 500 ? "The request could not be completed." : exception.Message
            }
        });
    }
}
