# Session Handoff

**Written:** 2026-08-07 · **Branch:** `main` at `6396a69` · **Phase 6 is merged — start 7.1**

> A customer leaves with a receipt, and an owner can see what was sold and whether the drawer
> balanced. **954 .NET · 181 Vitest · 48 Playwright**, green locally *and* on the runner.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

---

## Before anything else

**Nothing is in flight.** [PR #15](https://github.com/AliSleiman0/POS/pull/15) is merged, all nine
checks passed, the remote branch is deleted, the working tree is clean and `main` is level with
`origin/main`. `phase-6/receipt-model` still exists locally if you want the history.

**Branch off `main` before touching anything** — `phase-7/employee-management`.

**Your database is behind if you have not migrated.** Phase 6 added one migration,
`20260807162021_ReceiptSettings` (four nullable columns on `tenant`). `pos_dev` has it. If you
work from a fresh clone:

```powershell
dotnet tool restore
docker compose up -d
dotnet ef database update --project src/Pos.Data --startup-project src/Pos.Api `
  --connection "Host=localhost;Port=5432;Database=pos_dev;Username=pos;Password=dev_only_not_a_secret"
dotnet run --project tools/Pos.Seed
```

## Read first

1. [`docs/phases/PHASE-7-employees-audit.md`](phases/PHASE-7-employees-audit.md) → §7.1, which is
   next. Read §7.3 too before you start: it is the phase's real deliverable and it constrains how
   7.1 and 7.2 are built.
2. [`docs/API.md`](API.md#employees--employees) → the `/employees` table. **Three of its five
   routes do not exist yet**; see below.
3. [`DECISIONS.md`](../DECISIONS.md) → the three Phase 6.1 entries. Two matter to you: why
   `InvariantGlobalization` is off, and why receipt reprints are client-marked — **that second one
   is a debt 7.2 is expected to pay.**

## What Phase 7 inherits, and what it has to build

**7.1 is not starting from nothing, and it is not nearly done either.** What exists:

| Piece | State |
|---|---|
| `GET /employees/pin-eligible` | ✅ built (device token, deliberately thin) |
| `POST /employees/{id}/set-pin` | ✅ built |
| `GET/POST /employees`, `PUT /employees/{id}`, `POST /employees/{id}/deactivate` | ❌ **documented in API.md, not built** |
| `/registers` list, create, enroll, revoke | ✅ all four built |
| `src/features/admin/employees/` | ❌ does not exist — there is no employee UI at all |
| Device enrolment UI | ✅ `auth/DeviceEnrollmentPage.tsx` at `/settings/device` |
| `Pos.Core/Auditing/` | Only `ICurrentActor`. **No `AuditEntry` anywhere in `src/`.** |

So 7.1 is: three endpoints, a feature folder, and the two guardrails the phase doc calls out —
an Owner cannot demote or deactivate themselves, and one active Owner must remain. Build those as
*tests first*; a tenant locking itself out is a support call nobody can resolve, because there is
no platform admin tool by decision.

## Things that will bite you

Carried from Phase 6, and every one of these cost real time:

1. **`InvariantGlobalization` is `false` and must stay that way.** Under `true`, Windows cannot
   resolve an IANA time zone at all while Linux still can — so anything touching
   `Tenant.TimeZoneId` passes on the runner and fails on every developer machine. Invariant 8
   depends on it. **If a Dockerfile or a csproj changes in Phase 8, watch for this being set back.**
2. **`pnpm format:check` is its own CI step**, not part of `build` or `test`. A completely green
   local run tells you nothing about it. **Run `pnpm --dir src/Pos.Web format` before you push** —
   this is what turned PR #15 red on the first attempt.
3. **EF's `SqlQuery<T>` maps columns by *snake_case*, not by the alias you write.** With
   `UseSnakeCaseNamingConvention` on, `AS "ChangeGiven"` fails with "the required column
   'change_given' was not present" — single-word aliases pass, because the lookup is
   case-insensitive and only the underscore defeats it. See `ReportQueries`. It *does* map
   multi-column rows onto records, which is why the reports are not raw ADO.NET.
4. **The e2e database accumulates, and that has now broken three things.** `pos_e2e` is seeded and
   never dropped. `saleCount` read one page of 100 and saturated at the hundredth sale; the stock
   screen's row fell off page one at the fiftieth product; a report's shift filter went stale
   because the drawer has been open since the first ever run. **Any e2e assertion that counts, or
   that expects a row on the first page, has a shelf life.** 7.1 creates users — the same trap is
   waiting for an employee list.
5. **Playwright runs one worker**, locally and in CI. Every spec trades in the same shop through
   the same drawer. Do not turn parallelism back on to make the suite faster; that is what the 5.6
   flake was.
6. **Signing in twice without signing out does nothing.** `/login` sits outside the auth guard and
   bounces an authenticated visitor to `/`, so the form is never used and the old identity
   survives — it looks exactly like a session bug. 7.3 will want to switch roles constantly:
   **reuse `switchTo` from `e2e/reports.spec.ts`.** (The app is fine; `adoptSession` refetches
   `/auth/me` before returning.)
7. **A loaded machine fails the e2e suite.** Two runs went red on different specs with 1 GB of
   16 GB free; both passed on a quiet machine. Check memory before believing a red e2e run.
8. **A leftover `dotnet run` locks the build** (`Get-Process Pos.Api | Stop-Process -Force`), and
   Playwright's `webServer` refuses a held :5013. **Docker Desktop also died mid-session once** —
   if `dotnet ef` cannot reach 5432, check the daemon before the connection string.

## What 7.2 is expected to close

Two Phase 6 debts are explicitly waiting on the audit log, and both are written up rather than
hidden:

- **Receipt reprint counting.** Reprints are marked by the *client* today. A client that chose not
  to send the mark would print an unmarked duplicate, and §6.2 is right that this is a
  refund-fraud vector. Closing it needs an append-only record of each issue — which is exactly
  `AuditEntry`'s shape, and is why it was not half-built in 6.1. See `DECISIONS.md`.
- **Refund auditing.** `POST /sales/{id}/refund` stores a reason and an actor on the refund row,
  and that is the entire trail. §7.2's `RefundIssued` is the real thing.

Note also that `SaleLine.IsPriceOverridden` + `OverriddenBy` are currently the *only* record of a
price override. §7.2's `PriceOverridden` supersedes them; do not remove the columns.

## The trap in 7.3

§7.3's last bullet — "a test enumerates routes and fails on any endpoint with no authorization
metadata" — **already exists** as `EndpointCoverageTests` plus the `IsolationManifest`, built in
Phase 1.7 and extended every phase since. Phase 6 added five rows to it. Do not build a second
one; extend what is there, and make the (role × endpoint) matrix drive off the same manifest so a
new endpoint cannot ship missing from both.

## Verified, and not

**Falsified deliberately, restored** — six mechanisms in Phase 6, each confirmed red. The one
worth copying the method from: removing the receipt's tax-residue step went red on random basket 0
in both tax modes **while every hand-written example still passed.** Hand-picked examples cannot
find rounding bugs; a seeded property test over a few hundred awkward baskets can.

**Not verified, and 7.x will not change any of it:**

- **The beep.** Outstanding since 5.2, now five sessions. Headless has no audio device; it needs a
  person with speakers on, and it fires in two places.
- **A real printer.** Print output was checked under Chromium's print emulation, which honours the
  stylesheet but not a thermal printer's own unprintable margins.
- **Nobody who has worked a till has used any of it.** Still the real bar, and no phase gate
  clears it.

## Outstanding / deferred

- **`/reports/sales-summary` and `/reports/top-products`** — documented and marked *"Not built —
  deferred"* in `docs/API.md`. Nothing has a screen for them.
- **Margin cost is not snapshotted.** `SaleLine` records no cost price, so `/reports/margins`
  restates itself when a supplier's price changes. Fixing it is a schema change and a decision,
  not a query. Stated in `docs/API.md`.
- **No `PUT /settings`.** The four receipt fields and the cash-rounding increment are reachable
  only through `tools/Pos.Seed` (`--receipt-footer`, `--cash-rounding`). §7.2 lists
  `SettingsChanged` as an audited action, which implies the route — decide whether that is 7.2's
  or later.
- **The tender pad has no "clear all"** for a mis-keyed split, and everything else from the 5.3
  list stands.

## CI

All nine checks green on `main` at `6396a69`: backend (.NET) 2m21s · frontend (web) 49s ·
e2e (Playwright) 4m17s · API contract (client drift) 1m13s · GitGuardian.

A run whose jobs were *cancelled* reports `conclusion: failure` at the **run** level — inspect the
jobs before believing a red run. Branch protection does **not** gate on these checks, so the
discipline has to come from reading them.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing
until Phase 10, but must be settled before quoting a price.
