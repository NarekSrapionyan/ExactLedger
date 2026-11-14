using el.Domain.Common;

namespace el.Infrastructure.Persistence;

/// <summary>
/// Persistence-only row for a supported currency code. Currency definitions and accounts reference
/// it, so the database rejects any currency that is not in the reference data.
/// </summary>
internal sealed class KnownCurrency
{
    public KnownCurrency(CurrencyCode code) => Code = code;

    public CurrencyCode Code { get; }
}
