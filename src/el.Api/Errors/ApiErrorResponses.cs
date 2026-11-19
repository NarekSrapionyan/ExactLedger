using Microsoft.AspNetCore.Mvc;

namespace el.Api.Errors;

public static class ApiErrorResponses
{
    public static ProblemDetails Create(HttpContext context, int status, string? code = null)
    {
        var (defaultCode, message) = status switch
        {
            400 => ("INVALID_REQUEST", "The request is invalid."),
            401 => ("UNAUTHENTICATED", "Authentication is required or the bearer token is invalid."),
            403 => ("FORBIDDEN", "You do not have permission to perform this operation."),
            404 => ("NOT_FOUND", "The requested resource was not found."),
            409 => ("CONFLICT", "The operation conflicts with the current resource state."),
            429 => ("RATE_LIMITED", "Too many requests. Try again later."),
            503 => ("SERVICE_UNAVAILABLE", "The service is temporarily unavailable."),
            _ when status is >= 400 and < 500 => ("REQUEST_REJECTED", "The request could not be processed."),
            _ => ("INTERNAL_ERROR", "An unexpected error occurred."),
        };

        return new ProblemDetails
        {
            Status = status,
            Title = message,
            Extensions =
            {
                ["code"] = code ?? defaultCode,
                ["message"] = message,
                ["correlationId"] = context.TraceIdentifier,
            },
        };
    }

    public static Task WriteAsync(HttpContext context, int status, string? code = null) =>
        Results.Problem(Create(context, status, code)).ExecuteAsync(context);

    public static IResult Validation(HttpContext context, IDictionary<string, string[]> fields) =>
        Results.ValidationProblem(fields, title: "The request is invalid.", extensions: Create(context, 400).Extensions);
}
