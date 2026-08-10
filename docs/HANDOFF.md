# Session Handoff

**Written:** 2026-08-09 · **Branch:** `phase-7/employee-management` · **Phase 7 is complete — start 8.1**

> An owner can now hire, fire and configure their own shop without contacting us, and every
> action that moves money leaves a record they cannot edit. **1288 .NET · 207 Vitest · 60
> Playwright**, green locally. **Not yet run on the runner — see "Before anything else".**

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

---

## Before anything else

**Two commits sit on `phase-7/employee-management`, unpushed and un-PR'd.** `main` is untouched
at `2d0a1da`. Nothing has run in CI yet — that is the one gate Phase 7 has not cleared, and
[`ROADMAP.md`](ROADMAP.md#test-the-phase-before-starting-the-next-one) says green means green on
the runner. **Push, open the PR, and read the nine checks before starting 8.1.**

Two things most likely to be red there and green here:

1. **`pnpm format:check` is its own CI step.** Clean as of the last commit, but it is what turned
   PR #15 red. Run `pnpm --dir src/Pos.Web format` before pushing.
2. **The API contract job.** `src/api/schema.d.ts` was regenerated twice this session and is
   committed; if anything touched an endpoint since, it will drift.

**Your database needs migrating.** Phase 7 added one migration, `20260808212506_AuditLog`.
`pos_dev` and `pos_e2e` both have it.

```powershell
dotnet tool restore
docker compose up -d
dotnet ef database update --project src/Pos.Data --startup-project src/Pos.Api `
  --connection "Host=localhost;Port=5432;Database=pos_dev;Username=pos;Password=dev_only_not_a_secret"
dotnet run --project tools/Pos.Seed
```

## What Phase 7 built

| | |
|---|---|
| **7.0** | `AuditEntry` + `IAuditLog`. Entries are **staged** on the shared scoped `AppDbContext`, so each commits inside whatever transaction its action already runs — no call site co-ordinates anything. `ICurrentActor` gained `RegisterId`. |
| **7.1** | `GET/POST /employees`, `PUT /employees/{id}`, `POST /employees/{id}/deactivate`, and the three lock-out guards. `src/features/admin/employees/` and `/admin/tills`, which is the first UI `POST /registers` and revoke have ever had. |
| **7.2** | Fifteen audited actions, `GET /audit`, `/admin/activity`. Closes the Phase 6.2 reprint debt. |
| **7.3** | A (role × endpoint) matrix **derived** from `PolicyCatalog` × the routing table. 229 cases, no writes. |
| **7.4** | `GET`/`PUT /settings` and `/admin/settings`. Added because 7.2 audits `SettingsChanged` and there was no route to audit — and because receipt fields were otherwise reachable only through the unshipped seeder. |

## Things that will bite you

Carried forward, plus what this session cost time on:

1. **`InvariantGlobalization` is `false` and must stay that way.** Under `true`, Windows cannot
   resolve an IANA zone at all while Linux still can — so anything touching `Tenant.TimeZoneId`
   passes on the runner and fails on every developer machine. **Phase 8 writes a Dockerfile.
   Watch for this being set back.**
2. **`pnpm format:check` is its own CI step**, not part of `build` or `test`.
3. **EF's `SqlQuery<T>` maps columns by *snake_case*, not by the alias you write.** The
   employees' owner lock uses `AS "Value"` for this reason; `AS "OwnerId"` would look up
   `owner_id` and fail.
4. **The e2e database accumulates, and it has now broken four things.** New this session:
   `admin.spec.ts` edits the shop's **receipt footer**, which is tenant-wide, and
   `receipt.spec.ts` asserts the seeded one. One worker, alphabetical file order, so `admin` ran
   first and left `receipt` red — and because `pos_e2e` is never dropped it stayed wrong for
   every later run too. **There is now an `afterEach` in `admin.spec.ts` that restores it**, and
   getting that right took three attempts (see below). **Any spec that edits tenant-wide
   settings must restore them.**
5. **Playwright runs one worker**, locally and in CI. Do not turn parallelism back on.
6. **Signing in twice without signing out does nothing** — `/login` bounces an authenticated
   visitor to `/`, so the form never renders and a `fill` hangs until the hook times out. That
   is exactly how the first version of the `afterEach` above failed. Use `switchTo` from
   `e2e/reports.spec.ts`, or no `signIn` at all.
7. **A loaded machine fails the suites.** This session ran `dotnet test` and Playwright at the
   same time with ~3 GB free and **Docker dropped every container mid-run** — four API tests
   failed with `Failed to connect to 127.0.0.1`, which looks exactly like a code failure and is
   not. Re-running serially was clean. Check memory and `docker compose ps` before believing a
   red run.
8. **A leftover `dotnet run` locks the build** (`Get-Process Pos.Api | Stop-Process -Force`) and
   holds :5013, which also breaks `pnpm generate:api`.
9. **Port 5173.** `playwright.config.ts` has `reuseExistingServer: !CI`, so if anything else is
   serving on 5173 locally Playwright will happily test *that* application. This session it
   silently ran the whole suite against an unrelated project until the page snapshot gave it
   away. If every spec fails at `getByLabel('Shop')`, check what owns the port.

## Verified by falsification, and what it found

Four deliberate breaks, each confirmed red and restored:

1. **Removed the `REVOKE UPDATE, DELETE` from the audit migration** → 4 tests red, and the
   `UPDATE` genuinely succeeded. The Phase 1.6 `ALTER DEFAULT PRIVILEGES` grants both on every
   *future* table, so `audit_entry` was created with them. Without that one hand-written line
   the append-only claim was false and nothing said so.
2. **Dropped `FOR UPDATE` from the last-owner count** → the concurrent race failed 4 runs out of
   4, with the shop left with zero owners. Not flaky-red: consistently red.
3. **Moved `AuditEntry` to `Pos.Core.Auditing`** → compile errors confirmed the namespace is
   load-bearing for consumers. The *schema-test* half of the claim could not be isolated: a
   `PendingModelChangesWarning` guard fires first and fails all five `TenantModelTests`. Worth
   knowing that guard exists.
4. **Moved `GET /audit` from `CanManageEmployees` to `CanSell`** → **the matrix stayed green.**
   That is the real finding of 7.3: because it reads each endpoint's expectation off that
   endpoint's own metadata, it cannot catch a *wrong policy*. `NegativeAuthorizationTests` and
   `AuditReadTests.A_manager_cannot_read_the_audit_log` both went red, which is the layering
   that actually covers it. Recorded in `DECISIONS.md` and in the test's own remarks.

Three real defects the tests caught before anything shipped:

- **A spurious `SettingsChanged` entry on every save.** `numeric(19,4)` round-trips `0m` as
  `0.0000m`, and comparing the *rendered* strings reported "0" changing to "0.0000". The
  comparison is now on the decimal, formatted afterwards.
- **The second copy of a receipt was not marked as a reprint.** `useReceipt` had
  `staleTime: Infinity`, correct while the payload was immutable and wrong the moment the server
  started counting issues — the cached payload meant no second GET, so the count never advanced.
- **EF wanted to rename `ak_register_tenant_id_id`.** `Register` was the one tenant-referenced
  entity relying on an *implicit* alternate key, so adding `audit_entry` as a second dependent
  shifted EF's name derivation. Declared explicitly in `RegisterConfiguration`; the migration is
  now `audit_entry` and nothing else.

## What Phase 7 deliberately did not do

- **`AuthorizationRefused` covers sale adjustments only**, not every `403`. A middleware hook
  would be more complete and would bury the entries that matter under a misconfigured client's
  polling.
- **A deactivated user's access token still works for up to ~15 minutes.** Their refresh tokens
  are revoked, and `IsActive` is checked at login, PIN entry and `/auth/me` — but not while
  validating a JWT. Closing it needs a per-request liveness read or a token-version claim.
  Written into `docs/API.md` under the deactivate route.
- **No password change or reset flow.** An owner sets an initial password and reads it out. The
  person cannot change it afterwards, which is a real gap and a real feature, not a line item.
- **Two simultaneous receipt requests can both call themselves the first copy.** A unique index
  would refuse to print a receipt a customer is waiting for. Both are still recorded.

## Not verified, and 8.x will not change most of it

- **The beep.** Outstanding since 5.2, now six sessions. Headless has no audio device.
- **A real printer.** Print output has only ever been checked under Chromium's print emulation.
- **Nobody who has worked a till has used any of it.** Still the real bar.
- **The audit log has never been read by an owner looking for something.** It is tested and it
  renders; whether it answers the question somebody actually asks is unknown.

## Outstanding / deferred

- **`/reports/sales-summary` and `/reports/top-products`** — documented, *"Not built —
  deferred"*, no screen.
- **Margin cost is not snapshotted.** `SaleLine` records no cost price, so `/reports/margins`
  restates itself when a supplier's price changes. A schema change and a decision.
- **The tender pad has no "clear all"** for a mis-keyed split.
- **`GET /employees` is unpaginated.** Right for tens of staff; a chain with hundreds would want
  the cursor, which needs `CursorPaging` to stop requiring `TenantEntity`.

## CI

**Not yet run for this phase.** Last known green: `main` at `6396a69`, all nine checks.

A run whose jobs were *cancelled* reports `conclusion: failure` at the **run** level — inspect
the jobs before believing a red run. Branch protection does **not** gate on these checks.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing
until Phase 10, but must be settled before quoting a price.
