# Ledger domain entities

Task: BE-006 — Party, Account & Journal Domain Entities (FR-LED-01; 5.1)

## Identity

`Party`, `Account` and `JournalEntry` in `el.Domain/Ledger` are entities. They derive from `Entity<TId>` in `el.Domain/Common` and are equal when they have the same type and ID, whatever their other state. IDs are typed wrappers around caller-supplied, non-empty GUIDs (`PartyId`, `AccountId`, `JournalEntryId`), so an account ID can never be passed where a party ID is expected. Value objects such as `Money`, `ScaledPercentage`, `CurrencyCode` and `AccountCode` remain records that are equal by value.

## Enums

Every enum starts at 1, so an uninitialized value (0) is never a valid state.

- `EntrySide`: `Debit`, `Credit`.
- `PartyStatus`: `Active`, `Suspended`, `Closed`. Active ⇄ Suspended; either can be closed; Closed is final.
- `AccountStatus`: `Active`, `Closed`. Closed is final.
- `AccountType` and its normal side:

| Type | Party | Normal side | Purpose |
|---|---|---|---|
| `Clearing` | none | Debit | Funds at the (simulated) bank |
| `Payable` | required | Credit | Owed to a party, available for payout |
| `Escrow` | required | Credit | Owed to a party, held until release |
| `Revenue` | none | Credit | Marketplace income such as commission |

## Accounts

An account's ID, code, type, currency and party never change. Each party therefore has separate payable and escrow accounts per currency. The rule "one account per party, type and currency" is a uniqueness constraint that persistence enforces (BE-007). Balances are derived from journal lines (BE-008) and are not stored on the account.

## Journal entries

`JournalEntry` can only be created through two factories:

- `Create` for explicitly submitted lines: zero and negative amounts are rejected.
- `FromGeneratedComponents` for calculated components such as the settlement waterfall: zero-valued components are omitted first; negative components are still rejected.

Both enforce, in this order, and report a `JournalError` with the 1-based line number where it applies:

1. At least two lines (`TooFewLines`).
2. Every amount positive (`NonPositiveAmount`).
3. Every amount in the currency of the first line (`MixedCurrencies`) and of its account (`AccountCurrencyMismatch`).
4. Every account active (`InactiveAccount`).
5. Total debits exactly equal to total credits (`Unbalanced`), summed as `BigInteger`.

A validated entry and its lines are immutable. Lines keep the submitted order and reference accounts by `AccountId`.

Each entry has a `JournalReference` naming the business event it records (for example `SETTLEMENT:T-1001:CAPTURE`), and a reversal names the entry it reverses (`ReversalOf`); an entry cannot reverse itself. Uniqueness of references and one reversal per entry are enforced by persistence (BE-007.3).

## Boundaries

Persistence, database constraints and seeding are BE-007. Atomic posting, the monetary lock, posting time and balances are BE-008. Checks that need other aggregates, such as whether an account's party is active, belong to the posting use cases.
