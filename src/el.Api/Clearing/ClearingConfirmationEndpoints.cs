using el.Application.Clearing;
using el.Application.Security;
using el.Domain.Clearing;
using Microsoft.Net.Http.Headers;

namespace el.Api.Clearing;

public static class ClearingConfirmationEndpoints
{
    public static IEndpointRouteBuilder MapClearingConfirmationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/clearing-batches").WithTags("Clearing");
        var permission = Permission.ConfirmSimulatedBank.ToString();
        group.MapPost("/{id:guid}/simulate-statement", SimulateAsync).WithName("SimulateClearingStatement").RequireAuthorization(permission)
            .Produces(200, contentType: "text/csv");
        group.MapPost("/{id:guid}/confirm-simulated", ConfirmAsync).WithName("ConfirmClearingBatch").RequireAuthorization(permission)
            .Produces<ClearingBatchResponse>();
        group.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        return endpoints;
    }

    /// <summary>Generate the explicitly synthetic bank statement for an exported batch (text/csv).</summary>
    public static async Task<IResult> SimulateAsync(Guid id, HttpContext context, ClearingConfirmationService service, CancellationToken ct)
    {
        var statement = await service.SimulateAsync(id, ct);
        context.Response.Headers["X-Content-SHA256"] = statement.Sha256;

        return Results.File(statement.Content, "text/csv; charset=utf-8", $"statement-{id:N}.csv");
    }

    /// <summary>
    /// Confirm with a text/csv statement. Only a complete exact match posts Dr Payable / Cr Simulated Bank Clearing.
    /// Repeating the same statement returns the original result; a different statement for a confirmed batch is 409.
    /// </summary>
    public static async Task<IResult> ConfirmAsync(Guid id, HttpRequest request, ClearingConfirmationService service, CancellationToken ct)
    {
        var statement = await ReadStatementAsync(request, ct);
        var result = await service.ConfirmAsync(id, statement, ct);

        return Results.Ok(ClearingEndpoints.ToResponse(result.Batch));
    }

    private static async Task<byte[]> ReadStatementAsync(HttpRequest request, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type)
            || !type.MediaType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Send the statement as text/csv.");
        }

        if (request.ContentLength > StatementCsv.MaxBytes)
        {
            throw Invalid("The statement is too large.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > StatementCsv.MaxBytes)
            {
                throw Invalid("The statement is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static ClearingException Invalid(string message) => new(ClearingFailure.Invalid, "STATEMENT_INVALID", message);
}
