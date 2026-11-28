using System.Text.Json;
using el.Application.Abstractions;
using el.Application.Ledger;
using el.Domain.Ledger;
using Microsoft.AspNetCore.Diagnostics;
using el.Api.Contracts;
using el.Application.Clearing;
using el.Application.Knowledge;
using el.Application.Settlements;
using el.Domain.Settlements;
using el.Application.Reporting;

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
            ReportException report => (report.Status, report.Code),
            SettlementValidationException settlement => (400, settlement.Code),
            SettlementConflictException settlement => (409, settlement.Code),
            EscrowHoldException escrow => (409, escrow.Code),
            RefundException { Code: RefundException.AmountMismatch } refund => (400, refund.Code),
            RefundException refund => (409, refund.Code),
            ClearingException clearing => (clearing.Failure switch
            { ClearingFailure.Invalid => 400, ClearingFailure.NotFound => 404, _ => 409 }, clearing.Code),
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

        if (exception is ClearingException { Details: { Count: > 0 } details })
        {
            var problem = ApiErrorResponses.Create(context, status, code);
            problem.Extensions["details"] = details;
            await Results.Problem(problem).ExecuteAsync(context);

            return true;
        }

        await ApiErrorResponses.WriteAsync(context, status, code);
        return true;
    }
}
