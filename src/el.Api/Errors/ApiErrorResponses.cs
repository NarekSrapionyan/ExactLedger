using Microsoft.AspNetCore.Mvc;

namespace el.Api.Errors;

public static class ApiErrorResponses
{
    public static ProblemDetails Create(HttpContext context, int status, string? code = null)
    {
        var (defaultCode, message) = status switch
        {
            400 => ("invalid_request", "The request is invalid."),
            401 => ("unauthenticated", "Authentication is required or the bearer token is invalid."),
            403 => ("forbidden", "You do not have permission to perform this operation."),
            404 => ("not_found", "The requested resource was not found."),
            409 => ("conflict", "The operation conflicts with the current resource state."),
            429 => ("rate_limited", "Too many requests. Try again later."),
            503 => ("service_unavailable", "The service is temporarily unavailable."),
            _ when status is >= 400 and < 500 => ("request_rejected", "The request could not be processed."),
            _ => ("internal_error", "An unexpected error occurred."),
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
