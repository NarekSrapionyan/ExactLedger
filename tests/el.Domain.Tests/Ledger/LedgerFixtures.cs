using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Domain.Tests.Ledger;

internal static class LedgerFixtures
{
    internal static readonly CurrencyCode Usd = CurrencyReferenceData.Usd;
    internal static readonly CurrencyCode Jpy = CurrencyReferenceData.Jpy;

    internal static readonly PartyId SellerId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    internal static readonly PartyId AffiliateId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static int _nextAccount;

    internal static AccountId NewAccountId() =>
        new(new Guid(Interlocked.Increment(ref _nextAccount), 0, 0, new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }));

    internal static Account Clearing(CurrencyCode? currency = null) =>
        new(NewAccountId(), AccountCode.Parse("CLEARING.BANK"), AccountType.Clearing, currency ?? Usd, null);

    internal static Account Revenue(CurrencyCode? currency = null) =>
        new(NewAccountId(), AccountCode.Parse("REVENUE.COMMISSION"), AccountType.Revenue, currency ?? Usd, null);

    internal static Account Payable(PartyId? party = null, CurrencyCode? currency = null) =>
        new(NewAccountId(), AccountCode.Parse("PAYABLE.SELLER"), AccountType.Payable, currency ?? Usd, party ?? SellerId);

    internal static Account Escrow(PartyId? party = null, CurrencyCode? currency = null) =>
        new(NewAccountId(), AccountCode.Parse("ESCROW.SELLER"), AccountType.Escrow, currency ?? Usd, party ?? SellerId);

    internal static Money Amount(long minorUnits, CurrencyCode? currency = null) =>
        Money.FromMinorUnits(minorUnits, currency ?? Usd);

    internal static JournalLineDraft Debit(Account account, long minorUnits, CurrencyCode? currency = null) =>
        new(account, EntrySide.Debit, Amount(minorUnits, currency ?? account.Currency));

    internal static JournalLineDraft Credit(Account account, long minorUnits, CurrencyCode? currency = null) =>
        new(account, EntrySide.Credit, Amount(minorUnits, currency ?? account.Currency));

    internal static JournalEntryId EntryId() => new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
}
