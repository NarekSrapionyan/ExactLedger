# Reference delivery measurements

Requirement: NFR-PRF-01 and BE-014. Measurements use real HTTP through the
containerized Release API and PostgreSQL, not in-memory stores or bulk SQL seeding.

## Environment and method

- Windows host build 26200, AMD Ryzen 5 4600H, 12 logical CPUs, 15.4 GiB usable RAM.
- Docker Desktop Linux containers: Docker Engine 29.7.2, 12 CPUs, 7.44 GiB available.
- .NET SDK 10.0.203, ASP.NET runtime 10.0.15, PostgreSQL 17.6-alpine; Python 3.11.0 client.
- A fresh isolated Compose project; synthetic seeded DEMO-SIMPLE profile, no regional
  deduction or escrow, one remainder seller, 250.00 USD gross, 1% processor fee.
- 10,000 distinct settlements over localhost HTTP, five concurrent clients. Settlement
  latency includes request serialization, network, API, transactional persistence and
  response download. Login/startup/image builds are outside the timed samples.
- Prepare/export the first 1,000 trades, then simulate and confirm their one frozen
  247,500.00 USD seller instruction. Finish loading the remaining 9,000 trades.
- After the complete dataset, one warm-up and thirty sequential samples per listed
  read API. Default page size is 50; totals cover all matching rows. p95 is the
  nearest-rank sample (`ceil(0.95 * count)`). Batch timings are individual runs,
  not percentile estimates.
- Full journal CSV contains 30,002 lines of financial records (30,000 settlement
  lines plus two payout lines); its header is excluded from record count.

## Results

Final measurement is recorded alongside this document in `performance-results.json`.
Targets are settlement p95 <1,000 ms at five clients, read p95 <1,000 ms, and each
batch preparation/export <30,000 ms. They are reference educational targets, not
production capacity or latency guarantees.

Measured 2026-11-24 at 16:59 UTC. **All measured targets passed.**

| Operation | Samples | Result (ms) | Target (ms) |
| --- | ---: | ---: | ---: |
| Settlement API, five clients (p95) | 10,000 | 246.94 | <1,000 |
| Journal register (p95) | 30 | 896.05 | <1,000 |
| Trial balance (p95) | 30 | 61.91 | <1,000 |
| Payee account balances (p95) | 30 | 70.30 | <1,000 |
| Payee statement (p95) | 30 | 387.70 | <1,000 |
| Batch summary (p95) | 30 | 60.64 | <1,000 |
| Payee payout instructions (p95) | 30 | 47.99 | <1,000 |
| Prepare 1,000-trade batch | 1 | 1,145.28 | <30,000 |
| Export that batch | 1 | 51.42 | <30,000 |

The initial run measured journal-register p95 at 1,106.37 ms and exposed unnecessary
formatting of every journal row before selecting a page. Read pages now format
only their returned rows while retaining complete exact totals. Export still
formats and stores every selected record. Financial scope was not reduced.

## Correctness and operations

The harness asserts the 1,000-trade cut count and amount, reserved-versus-paid
distinction before confirmation, equality of paid/payable debits/bank credits
after confirmation, balanced final trial balance, exact journal CSV SHA-256 and
record count. It backs up the full dataset with pg_dump, restores into a second
database owned by the migration role, then verifies 10,000 settlements, 10,001
journals, one report artifact and zero trial-balance difference.

Reproduce with `python scripts/Measure-Delivery.py` from the repository root. It
explicitly disables both background timers to keep the measured fixture under
manual control; normal Compose startup enables them. It
requires Docker Compose and Python standard library only. The reference fixture
has one currency, one seller and no held escrow; these timings are not claims about
unmeasured currencies/beneficiary counts or concurrent read traffic. Automated
financial tests separately exercise escrow, refunds, three currency precisions,
authorization, retries and rollback. See [delivery runbook](delivery-runbook.md).
