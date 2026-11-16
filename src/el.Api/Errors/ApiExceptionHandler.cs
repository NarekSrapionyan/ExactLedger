using System.Text.Json;
using el.Application.Abstractions;
using el.Application.Ledger;
using el.Domain.Ledger;
using Microsoft.AspNetCore.Diagnostics;
using el.Api.Contracts;

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
            BadHttpRequestException { InnerException: MoneyJsonException money } => (400, money.Code),
            BadHttpRequestException badRequest => (badRequest.StatusCode, (string?)null),
            MoneyJsonException money => (400, money.Code),
            JsonException => (400, "invalid_request"),
            JournalValidationException { Error: JournalError.Unbalanced } => (400, "LEDGER_UNBALANCED"),
            JournalValidationException => (400, "invalid_journal"),
            LedgerLockTimeoutException => (409, "ledger_busy"),
            AccountNotPostableException => (409, "account_not_postable"),
            _ => (500, "internal_error"),
        };

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
