using System.Numerics;
using el.Domain.Ledger;

namespace el.Application.Ledger;

public sealed record AccountRegisterBalance(Guid Id, string Code, AccountType Type, string Currency,
    BigInteger OpeningNetDebit, BigInteger DebitTotal, BigInteger CreditTotal)
{
    public BigInteger ClosingNetDebit => OpeningNetDebit + DebitTotal - CreditTotal;
}

public interface IAccountRegisterQueries
{
    Task<IReadOnlyList<AccountRegisterBalance>> ReadAsync(string? currency, DateOnly? from, DateOnly? to,
        int offset, int limit, CancellationToken ct);
}
