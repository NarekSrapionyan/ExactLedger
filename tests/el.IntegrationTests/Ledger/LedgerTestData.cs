using el.Domain.Common;
using el.Domain.Ledger;
using Npgsql;

namespace el.IntegrationTests.Ledger;

internal sealed class LedgerTestData
{
    // Seeded accounts (database.md): CLEARING.BANK.<CUR>, REVENUE.COMMISSION.<CUR>, ...
    public const string ClearingUsd = "CLEARING.BANK.USD";
    public const string RevenueUsd = "REVENUE.COMMISSION.USD";
    public const string ClearingJpy = "CLEARING.BANK.JPY";
    public const string RevenueJpy = "REVENUE.COMMISSION.JPY";

    private readonly LedgerTestHost _host;

    public LedgerTestData(LedgerTestHost host) => _host = host;

    public Task<Guid> AccountGuidAsync(string code) =>
        _host.ScalarAsync<Guid>("SELECT id FROM ledger.accounts WHERE code = @code", new NpgsqlParameter("code", code));

    public async Task<AccountId> AccountIdAsync(string code) => new AccountId(await AccountGuidAsync(code));

    /// <summary>Closes the account in the database only, the way a concurrent closure would.</summary>
    public Task CloseAccountAsync(string code) =>
        _host.ExecuteAsync(
            "UPDATE ledger.accounts SET status = @closed WHERE code = @code",
            new NpgsqlParameter("closed", (int)AccountStatus.Closed),
            new NpgsqlParameter("code", code));

    /// <summary>A fresh domain Account (always Active) with the seeded account's ID. Clearing/revenue accounts only.</summary>
    public async Task<Account> LoadAccountAsync(string code)
    {
        var id = await AccountGuidAsync(code);
        var type = code.StartsWith("CLEARING", StringComparison.Ordinal) ? AccountType.Clearing : AccountType.Revenue;

        // ADAPT-1: the AccountCode factory (Parse is a guess, mirroring CurrencyCode.Parse).
        return new Account(new AccountId(id), AccountCode.Parse(code), type, CurrencyCode.Parse(code[^3..]), partyId: null);
    }

    /// <summary>A balanced two-line entry: Dr debit, Cr credit, same amount.</summary>
    public JournalEntry BuildEntry(string reference, Account debit, Account credit, long amountMinor)
    {
        var amount = Money.FromMinorUnits(amountMinor, debit.Currency);

        // ADAPT-2: the JournalLineDraft constructor and JournalReference factory (both guesses).
        return JournalEntry.Create(
            new JournalEntryId(Guid.NewGuid()),
            JournalReference.Parse(reference),
            "BE-008 test posting",
            [
                new JournalLineDraft(debit, EntrySide.Debit, amount),
                new JournalLineDraft(credit, EntrySide.Credit, amount),
            ]);
    }
}
