using System.Globalization;
using el.Api.Errors;
using el.Api.Ledger;
using el.Application.Clearing;
using el.Application.Security;
using el.Domain.Common;

namespace el.Api.Clearing;

/// <summary>Prepare the daily batch of one UTC business date and currency.</summary>
public sealed record PrepareBatchRequest(DateOnly BusinessDate, string? Currency);

/// <summary>One frozen payout instruction; its amount is reserved until a simulated confirmation consumes it.</summary>
public sealed record PayoutInstructionResponse(Guid Id, string Reference, Guid PartyId, Money Amount);

public sealed record ExportMetadataResponse(string Sha256, int RecordCount, AmountResponse Total, DateTimeOffset GeneratedAtUtc);

public sealed record ConfirmationMetadataResponse(string StatementSha256, int InstructionCount, AmountResponse Total, DateTimeOffset ConfirmedAtUtc);

/// <summary>A clearing batch. State is Prepared, Exported or Confirmed.</summary>
public sealed record ClearingBatchResponse(Guid Id, string BusinessDate, string Currency, string State, DateTimeOffset CutAtUtc,
    long CutSequence, int EntryCount, AmountResponse Total, IReadOnlyList<PayoutInstructionResponse> Instructions,
    ExportMetadataResponse? Export, ConfirmationMetadataResponse? Confirmation);

public static class ClearingEndpoints
{
    public static IEndpointRouteBuilder MapClearingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/clearing-batches").WithTags("Clearing");
        var permission = Permission.RunClearingAndExport.ToString();
        group.MapPost("/", PrepareAsync).WithName("PrepareClearingBatch").RequireAuthorization(permission)
            .Produces<ClearingBatchResponse>(201).Produces<ClearingBatchResponse>().Produces(204);
        group.MapGet("/", ListAsync).WithName("ListClearingBatches").RequireAuthorization(permission)
            .Produces<IReadOnlyList<ClearingBatchResponse>>();
        group.MapGet("/{id:guid}", ReadAsync).WithName("ReadClearingBatch").RequireAuthorization(permission)
            .Produces<ClearingBatchResponse>();
        group.ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
        return endpoints;
    }

    /// <summary>Run the daily cut. A retry for the same date and currency returns the stored batch (200); nothing to package returns 204.</summary>
    public static async Task<IResult> PrepareAsync(PrepareBatchRequest request, ClearingService service, CancellationToken ct)
    {
        var result = await service.PrepareAsync(request.BusinessDate, request.Currency, ct);

        return result.Outcome switch
        {
            ClearingOutcome.Prepared => Results.Created("/v1/clearing-batches/" + result.Batch!.Id, ToResponse(result.Batch)),
            ClearingOutcome.Existing => Results.Ok(ToResponse(result.Batch!)),
            _ => Results.NoContent(),
        };
    }

    /// <summary>List batches, newest business date first (up to 100 per page).</summary>
    public static async Task<IResult> ListAsync(int? offset, int? limit, HttpContext context, ClearingService service, CancellationToken ct)
    {
        var start = offset ?? 0;
        var count = limit ?? 50;

        if (start < 0 || count is < 1 or > 100)
        {
            return ApiErrorResponses.Validation(context, new Dictionary<string, string[]>
            { ["pagination"] = ["Offset must be non-negative; limit must be 1 to 100."] });
        }

        return Results.Ok((await service.ListAsync(start, count, ct)).Select(ToResponse).ToArray());
    }

    /// <summary>Read one batch with its frozen instructions.</summary>
    public static async Task<IResult> ReadAsync(Guid id, ClearingService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.GetAsync(id, ct)));

    internal static ClearingBatchResponse ToResponse(ClearingBatch batch)
    {
        var currency = CurrencyCode.Parse(batch.Currency);

        return new ClearingBatchResponse(batch.Id, batch.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            batch.Currency, batch.State.ToString(), batch.CutAtUtc, batch.CutSequence, batch.EntryCount,
            LedgerEndpoints.Format(batch.TotalMinorUnits, currency),
            batch.Instructions.Select(item => new PayoutInstructionResponse(item.Id, item.Reference, item.PartyId,
                Money.FromMinorUnits(item.AmountMinor, currency))).ToArray(),
            batch.Export is null ? null : new ExportMetadataResponse(batch.Export.Sha256, batch.Export.RecordCount,
                LedgerEndpoints.Format(batch.Export.TotalMinorUnits, currency), batch.Export.GeneratedAtUtc),
            batch.Confirmation is null ? null : new ConfirmationMetadataResponse(batch.Confirmation.StatementSha256,
                batch.Confirmation.InstructionCount, LedgerEndpoints.Format(batch.Confirmation.TotalMinorUnits, currency),
                batch.Confirmation.ConfirmedAtUtc));
    }
}
