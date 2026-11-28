using el.Application.Clearing;
using el.Application.Security;

namespace el.Api.Clearing;

public static class ClearingExportEndpoints
{
    public static IEndpointRouteBuilder MapClearingExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/clearing-batches").WithTags("Clearing");
        var permission = Permission.RunClearingAndExport.ToString();
        group.MapPost("/{id:guid}/export", ExportAsync).WithName("ExportClearingBatch").RequireAuthorization(permission)
            .Produces<ClearingBatchResponse>(201).Produces<ClearingBatchResponse>();
        group.MapGet("/{id:guid}/export", DownloadAsync).WithName("DownloadClearingExport").RequireAuthorization(permission)
            .Produces(200, contentType: "text/csv");
        group.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        return endpoints;
    }

    /// <summary>Persist the deterministic payout CSV once (201). A repeat returns the stored export (200). Nothing is posted.</summary>
    public static async Task<IResult> ExportAsync(Guid id, ClearingExportService service, CancellationToken ct)
    {
        var result = await service.ExportAsync(id, ct);
        var response = ClearingEndpoints.ToResponse(result.Batch);

        return result.Created ? Results.Created($"/v1/clearing-batches/{id}/export", response) : Results.Ok(response);
    }

    /// <summary>Download the stored payout CSV; the bytes and SHA-256 never change.</summary>
    public static async Task<IResult> DownloadAsync(Guid id, HttpContext context, ClearingExportService service, CancellationToken ct)
    {
        var export = await service.DownloadAsync(id, ct);
        context.Response.Headers["X-Content-SHA256"] = export.Metadata.Sha256;

        return Results.File(export.Content, "text/csv; charset=utf-8", $"payouts-{id:N}.csv");
    }
}
