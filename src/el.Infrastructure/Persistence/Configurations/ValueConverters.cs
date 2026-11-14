using el.Domain.Common;
using el.Domain.Ledger;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace el.Infrastructure.Persistence.Configurations;

/// <summary>Converters between domain value types and their column values.</summary>
internal static class ValueConverters
{
    internal static readonly ValueConverter<CurrencyCode, string> CurrencyCodeToText =
        new(code => code.Value, value => CurrencyCode.Parse(value));

    internal static readonly ValueConverter<AccountCode, string> AccountCodeToText =
        new(code => code.Value, value => AccountCode.Parse(value));

    internal static readonly ValueConverter<PartyId, Guid> PartyIdToGuid =
        new(id => id.Value, value => new PartyId(value));

    internal static readonly ValueConverter<AccountId, Guid> AccountIdToGuid =
        new(id => id.Value, value => new AccountId(value));

    internal static readonly ValueConverter<JournalEntryId, Guid> JournalEntryIdToGuid =
        new(id => id.Value, value => new JournalEntryId(value));

    internal static readonly ValueConverter<JournalReference, string> JournalReferenceToText =
        new(reference => reference.Value, value => JournalReference.Parse(value));
}
