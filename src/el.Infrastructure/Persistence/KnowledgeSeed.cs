using System.Security.Cryptography;
using System.Text;
using el.Domain.Common;
using el.Domain.Knowledge;

namespace el.Infrastructure.Persistence;

public static class KnowledgeSeed
{
    private static readonly DateTimeOffset From = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static IReadOnlyList<Region> Regions { get; } = [new("DEMO-A", "Synthetic Region A"), new("DEMO-B", "Synthetic Region B"), new("DEMO-NONE", "Synthetic no-deduction region")];
    public static IReadOnlyList<DeductionType> Types { get; } = [new("DEMO-LEVY", "Synthetic levy", "Educational gross-based deduction, not a statement of law.")];
    public static IReadOnlyList<DeductionRule> Rules { get; } = new[] { ("DEMO-A", 10_000_000L), ("DEMO-B", 5_000_000L) }
        .Select(item => new DeductionRule(Id(item.Item1 + ":1"), Id(item.Item1), 1, item.Item1, "DEMO-LEVY", "Gross", item.Item2, 1,
            Account("PAYABLE.TAX.USD"), "USD", RoundingMode.HalfEven, From, To, "Synthetic teaching assumption; no real tax law is represented.", true, "synthetic-seed", From)).ToArray();
    public static IReadOnlyList<RuleFixture> RuleFixtures { get; } = Rules.Select(rule =>
        new RuleFixture(Id(rule.RegionCode + ":fixture"), rule.Id, "Synthetic USD 100.00 gross example.", 10000, rule.RegionCode == "DEMO-A" ? 1000 : 500)).ToArray();
    public static IReadOnlyList<CorporateFeeProfile> Profiles { get; } =
    [
        new(Id("standard:1"), Id("standard"), 1, "demo-client-1", "DEMO-STANDARD", "USD", 10_000_000, 2_900_000, 30, 50, 500,
            RoundingMode.HalfEven, 10_000_000, 7, Account("REVENUE.COMMISSION.USD"), Account("PAYABLE.PROCESSOR.USD"), From, To,
            "Synthetic seller-borne processor fee; 10% escrow held for 7 days.", true, "synthetic-seed", From),
        new(Id("simple:1"), Id("simple"), 1, "demo-client-1", "DEMO-SIMPLE", "USD", 0, 1_000_000, 0, null, null,
            RoundingMode.HalfAwayFromZero, 0, 0, Account("REVENUE.COMMISSION.USD"), Account("PAYABLE.PROCESSOR.USD"), From, To,
            "Synthetic seller-borne processor fee; no escrow.", true, "synthetic-seed", From),
    ];
    public static IReadOnlyList<ProfileFixture> ProfileFixtures { get; } =
    [
        new(Id("standard:fixture"), Profiles[0].Id, "Synthetic gross 100.00; beneficiary share 80.00.", 10000, 8000, 1000, 320, 800, 7200),
        new(Id("simple:fixture"), Profiles[1].Id, "Synthetic gross 100.00; beneficiary share 80.00.", 10000, 8000, 0, 100, 0, 8000),
    ];
    private static Guid Account(string code) => SyntheticSeed.Accounts.Single(account => account.Code.Value == code).Id.Value;
    private static Guid Id(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes("exactledger-knowledge:" + value)).AsSpan(0, 16));
}
