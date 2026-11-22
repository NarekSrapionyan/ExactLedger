using System.Text.Json;
using el.Application.Abstractions;
using el.Application.Ledger;
using el.Domain.Ledger;
using Microsoft.AspNetCore.Diagnostics;
using el.Api.Contracts;
using el.Application.Knowledge;
using el.Application.Settlements;
using el.Domain.Settlements;

namespace el.Api.Errors;

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var (status, code) = exception switch
        {
            SettlementValidationException settlement => (400, settlement.Code),
            SettlementConflictException settlement => (409, settlement.Code),
            EscrowHoldException escrow => (409, escrow.Code),
            KnowledgeException knowledge => (knowledge.Failure switch
            { KnowledgeFailure.Invalid => 400, KnowledgeFailure.NotFound => 404, _ => 409 }, knowledge.Code),
            BadHttpRequestException { InnerException: MoneyJsonException money } => (400, money.Code),
            BadHttpRequestException badRequest => (badRequest.StatusCode, (string?)null),
            MoneyJsonException money => (400, money.Code),
            JsonException => (400, "INVALID_REQUEST"),
            JournalValidationException { Error: JournalError.Unbalanced } => (400, "LEDGER_UNBALANCED"),
            JournalValidationException => (400, "INVALID_JOURNAL"),
            LedgerLockTimeoutException => (503, "LEDGER_BUSY"),
            AccountNotPostableException => (409, "ACCOUNT_NOT_POSTABLE"),
            _ => (500, "INTERNAL_ERROR"),
        };

        if (exception is LedgerLockTimeoutException)
            context.Response.Headers.RetryAfter = "1";

        // Never log exception messages, request bodies, headers or tokens: they can
        // contain credentials. The correlation ID connects this record to the response.
        if (status >= 500)
        {
            logger.LogError("API request failed ({ErrorType}). CorrelationId: {CorrelationId}",
                exception.GetType().Name, context.TraceIdentifier);
        }

        await ApiErrorResponses.WriteAsync(context, status, code);
        return true;
    }
}
