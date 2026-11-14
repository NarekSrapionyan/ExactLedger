namespace el.Domain.Ledger;

/// <summary>Purpose of a ledger account. Zero is deliberately not a valid value.</summary>
public enum AccountType
{
    /// <summary>System asset account for funds held at the (simulated) bank. Normal side: debit.</summary>
    Clearing = 1,

    /// <summary>Amount owed to one party, available for payout. Normal side: credit.</summary>
    Payable = 2,

    /// <summary>Amount owed to one party but held until escrow release. Normal side: credit.</summary>
    Escrow = 3,

    /// <summary>Marketplace income such as commission. Normal side: credit.</summary>
    Revenue = 4,
}
