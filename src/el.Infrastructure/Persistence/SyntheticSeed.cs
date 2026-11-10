using System.Security.Cryptography;
using System.Text;
using el.Domain.Common;
using el.Domain.Ledger;

namespace el.Infrastructure.Persistence;

/// <summary>
/// Synthetic seed data for the educational MVP. No real people, businesses or bank details:
/// every party name starts with "Synthetic". IDs are derived from fixed names, so they are the
/// same on every machine and in every migration.
/// </summary>
/// <remarks>
/// Account families per seeded currency:
/// bank clearing (Clearing), commission (Revenue), tax and processor (Payable of the synthetic
/// tax authority and processor), and payable plus escrow for each synthetic seller and affiliate.
/// </remarks>
public static class SyntheticSeed
{
    public static readonly Party SellerOne = SeedParty("SELLER-1", "Synthetic Seller One");
    public static readonly Party SellerTwo = SeedParty("SELLER-2", "Synthetic Seller Two");
    public static readonly Party Affiliate = SeedParty("AFFILIATE-1", "Synthetic Affiliate");
    public static readonly Party Processor = SeedParty("PROCESSOR", "Synthetic Card Processor");
    public static readonly Party TaxAuthority = SeedParty("TAX", "Synthetic Tax Authority");

    public static IReadOnlyList<Party> Parties { get; } = [SellerOne, SellerTwo, Affiliate, Processor, TaxAuthority];

    internal static IReadOnlyList<KnownCurrency> Currencies { get; } = CurrencyReferenceData.Catalog.Definitions
        .Select(definition => definition.Code)
        .Distinct()
        .Select(code => new KnownCurrency(code))
        .ToArray();

    public static IReadOnlyList<Account> Accounts { get; } = CurrencyReferenceData.Catalog.Definitions
        .Select(definition => definition.Code)
        .Distinct()
        .SelectMany(AccountsFor)
        .ToArray();

    private static IEnumerable<Account> AccountsFor(CurrencyCode currency)
    {
        yield return SeedAccount("CLEARING.BANK", AccountType.Clearing, currency, null);
        yield return SeedAccount("REVENUE.COMMISSION", AccountType.Revenue, currency, null);
        yield return SeedAccount("PAYABLE.TAX", AccountType.Payable, currency, TaxAuthority);
        yield return SeedAccount("PAYABLE.PROCESSOR", AccountType.Payable, currency, Processor);

        foreach (var (key, party) in new[] { ("SELLER-1", SellerOne), ("SELLER-2", SellerTwo), ("AFFILIATE-1", Affiliate) })
        {
            yield return SeedAccount("PAYABLE." + key, AccountType.Payable, currency, party);
            yield return SeedAccount("ESCROW." + key, AccountType.Escrow, currency, party);
        }
    }

    private static Party SeedParty(string key, string name) =>
        new(new PartyId(DeterministicGuid("party:" + key)), name);

    private static Account SeedAccount(string family, AccountType type, CurrencyCode currency, Party? party)
    {
        var code = AccountCode.Parse(family + "." + currency.Value);

        return new Account(new AccountId(DeterministicGuid("account:" + code.Value)), code, type, currency, party?.Id);
    }

    private static Guid DeterministicGuid(string name) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("exactledger-seed:" + name)).AsSpan(0, 16));
}
