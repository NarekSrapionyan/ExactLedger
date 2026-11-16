using System.Globalization;
using System.Numerics;
using el.Api.Authentication;
using el.Api.Errors;
using el.Application.Audit;
using el.Application.Ledger;
using el.Application.Security;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Api.Ledger;

/// <summary>An exact currency amount, including totals beyond Int64.</summary>
/// <param name="Amount">Decimal string with the currency's exact fractional digits.</param>
/// <param name="Currency">USD, JPY or KWD.</param>
public sealed record AmountResponse(string Amount, string Currency);
/// <summary>Account totals; net debit is debits minus credits.</summary>
public sealed record AccountBalanceResponse(Guid AccountId, AmountResponse DebitTotal, AmountResponse CreditTotal, AmountResponse NetDebit);
/// <summary>Exact debit and credit totals for one currency.</summary>
public sealed record TrialBalanceResponse(string Currency, AmountResponse DebitTotal, AmountResponse CreditTotal, bool IsBalanced);

public static class LedgerEndpoints
{
    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/ledger/accounts/{accountId}/balance", AccountBalanceAsync)
            .RequireAuthorization(Permission.ReadBalancesAndPayouts.ToString())
            .Produces<AccountBalanceResponse>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithTags("Ledger");
        endpoints.MapGet("/v1/ledger/trial-balance", TrialBalanceAsync)
            .RequireAuthorization(Permission.ReadLedger.ToString())
            .Produces<IReadOnlyList<TrialBalanceResponse>>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithTags("Ledger");
        endpoints.MapGet("/v1/audit-events", ReadAuditAsync)
            .RequireAuthorization(Permission.ReadAudit.ToString())
            .Produces<IReadOnlyList<AuditEvent>>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithTags("Audit");
        return endpoints;
    }

    /// <summary>Read an account balance (Finance/Auditor or the owning Payee).</summary>
    public static async Task<IResult> AccountBalanceAsync(Guid accountId, HttpContext context,
        ILedgerBalanceQueries queries, CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty) return Results.Problem(ApiErrorResponses.Create(context, 404));
        var balance = await queries.GetAccountBalanceAsync(new AccountId(accountId), cancellationToken);
        if (balance is null || !ResourceAccess.Party(RequestIdentity.Get(context), balance.PartyId?.Value))
            return Results.Problem(ApiErrorResponses.Create(context, 404));
        return Results.Ok(new AccountBalanceResponse(accountId, Format(balance.DebitTotal, balance.Currency),
            Format(balance.CreditTotal, balance.Currency), Format(balance.NetDebit, balance.Currency)));
    }

    /// <summary>Read all-time trial balances by currency (Finance/Auditor).</summary>
    public static async Task<IResult> TrialBalanceAsync(string? currency, HttpContext context,
        ILedgerBalanceQueries queries, CancellationToken cancellationToken)
    {
        var code = CurrencyReferenceData.Catalog.Definitions.Select(definition => definition.Code)
            .FirstOrDefault(code => string.Equals(code.Value, currency, StringComparison.Ordinal));
        if (currency is not null && code is null)
            return ApiErrorResponses.Validation(context, new Dictionary<string, string[]> { ["currency"] = ["Use USD, JPY or KWD."] });
        var balances = await queries.GetTrialBalanceAsync(code, cancellationToken);
        return Results.Ok(balances.Select(balance => new TrialBalanceResponse(balance.Currency.Value,
            Format(balance.DebitTotal, balance.Currency), Format(balance.CreditTotal, balance.Currency), balance.IsBalanced)));
    }

    /// <summary>Read audit events (Finance/Admin/Auditor; up to 100 per page).</summary>
    public static async Task<IResult> ReadAuditAsync(int? offset, int? limit, HttpContext context,
        IAuditQueries queries, CancellationToken cancellationToken)
    {
        var start = offset ?? 0;
        var count = limit ?? 50;
        if (start < 0 || count is < 1 or > 100)
            return ApiErrorResponses.Validation(context, new Dictionary<string, string[]> { ["pagination"] = ["Offset must be non-negative; limit must be 1 to 100."] });
        return Results.Ok(await queries.ReadAsync(start, count, cancellationToken));
    }

    // Aggregate totals can exceed Money's per-value limit. Format the exact integer
    // directly, retaining the same decimal-string/currency contract as individual money.
    public static AmountResponse Format(BigInteger minorUnits, CurrencyCode currency)
    {
        var exponent = CurrencyReferenceData.Catalog.Resolve(currency, CurrencyReferenceData.InitialVersion).Exponent;
        var absolute = BigInteger.Abs(minorUnits);
        var whole = BigInteger.DivRem(absolute, BigInteger.Pow(10, exponent), out var fraction);
        var text = whole.ToString(CultureInfo.InvariantCulture);
        if (exponent > 0) text += "." + fraction.ToString(CultureInfo.InvariantCulture).PadLeft(exponent, '0');
        return new AmountResponse((minorUnits.Sign < 0 ? "-" : string.Empty) + text, currency.Value);
    }
}
