# Session Handoff

**Written:** 2026-08-10 · **Branch:** `main` at `6e198f3` · **Phase 7 merged — start 8.1**

> Phases 0–7 are done. **Phase 8 completes the MVP**: containerize, host, migrate in CI/CD, make
> it observable, prove the backups restore, harden it, and be able to onboard a tenant. After
> that it is a product you can put in front of a paying shop.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

---

## Before anything else

**Read CI for `8d964d3` and `6e198f3`.** `gh run list --branch main --limit 2`.

[PR #16](https://github.com/AliSleiman0/POS/pull/16) was merged **at the owner's explicit
instruction with two checks still pending**. At merge time *API contract*, *frontend (web)* and
*GitGuardian* had passed; **backend (.NET) and e2e (Playwright) had not reported**, and both runs
were still in progress when this file was written. Both suites were green locally on the same
commit — 1288 .NET, 207 Vitest, 60 Playwright — so this is very likely fine, but that is not the
same claim.

**If either job is red, fixing it comes before any Phase 8 work.** Branch protection does not
gate on these checks, which is why the discipline has to come from reading them.

**Migrate before running anything.** Phase 7 added `20260808212506_AuditLog`.

```powershell
dotnet tool restore
docker compose up -d
dotnet ef database update --project src/Pos.Data --startup-project src/Pos.Api `
  --connection "Host=localhost;Port=5432;Database=pos_dev;Username=pos;Password=dev_only_not_a_secret"
dotnet run --project tools/Pos.Seed
```

## Read first

1. [`docs/phases/PHASE-8-deployment.md`](phases/PHASE-8-deployment.md) — all seven milestones.
   **Read 8.3 and 8.5 before writing the Dockerfile in 8.1**: they constrain it. 8.3 forbids
   migrate-on-startup, so the image must not be tempted to do it; 8.5 is the milestone most
   likely to be skipped and the one that matters most.
2. [`DECISIONS.md`](../DECISIONS.md) → the two open items at the top, and the Phase 7 block.
3. [`docs/ARCHITECTURE.md`](ARCHITECTURE.md#authorization) → the policy table, which 8.6's
   production cross-tenant probe re-tests against a deployed instance.

## What 8.1 inherits, and what it has to build

**Nothing exists yet.** No `src/Pos.Api/Dockerfile`, no `.dockerignore`. Both are new files.

What is already true and should stay true:

| Precondition | State |
|---|---|
| `InvariantGlobalization` | `false` in `Directory.Build.props:45` — **see the trap below** |
| Migrate-on-startup | **Absent from `src/` and `tools/`.** Only the two test fixtures call `MigrateAsync`, which is correct. 8.3's "grep and confirm" already passes — keep it that way |
| `/health/live`, `/health/ready` | Built, `Program.cs:214` and `:219`. 8.4 wires them to platform probes |
| JWT signing key | `ValidateOnStart` refuses to boot without a usable one (`Program.cs:54-59`). A container with no key fails loudly, which is what you want |
| Rate limiting | `UseRateLimiter` at `Program.cs:189`. 8.6 tightens and verifies it |
| OpenAPI / Scalar | Development-only (`Program.cs:147-159`), so 8.6's "Swagger not public" holds by construction — verify against the deployed instance anyway |
| `pos_app` role | `NOBYPASSRLS`, no `CREATE` on schema. 8.2 must confirm production connects as it, **not** as the owner |
| CORS | **Not configured at all.** 8.2 adds it, locked to the web origin |

## Things that will bite you

1. **`InvariantGlobalization` must stay `false`, and 8.1 is the milestone most likely to break
   it.** A new `Dockerfile` or a `csproj` edit that sets it back — or an Alpine base image
   without ICU — makes every IANA time zone unresolvable. The failure is asymmetric and nasty:
   **Linux containers keep working while Windows developer machines fail**, or vice versa
   depending on how it breaks, so it can pass CI and break everyone locally. `Tenant.TimeZoneId`,
   every receipt timestamp and every business-day boundary depend on it. If you pick
   `aspnet:10.0-alpine`, you need `icu-libs` installed explicitly.
2. **`pnpm format:check` is its own CI step**, not part of `build` or `test`. A completely green
   local run tells you nothing about it. Run `pnpm --dir src/Pos.Web format` before pushing.
3. **The e2e database accumulates and is never dropped.** It has now broken four things. The
   newest: `admin.spec.ts` edits the shop's **receipt footer**, which is tenant-wide, and
   `receipt.spec.ts` asserts the seeded one — one worker, alphabetical file order. There is an
   `afterEach` in `admin.spec.ts` that restores it, and getting that right took three attempts
   (it must wait for the form to render *and* to seed itself, or it silently no-ops). **Any spec
   that edits tenant-wide settings must restore them.**
4. **Signing in twice without signing out does nothing.** `/login` bounces an authenticated
   visitor to `/`, so the form never renders and a `fill` hangs until the hook times out. Use
   `switchTo` from `e2e/reports.spec.ts`.
5. **Port 5173.** `playwright.config.ts` has `reuseExistingServer: !CI`, so anything else serving
   on 5173 gets tested instead of this app — silently, with every spec failing at
   `getByLabel('Shop')`. That happened this session and cost a full debugging cycle.
6. **Running `dotnet test` and Playwright at once kills Docker.** With ~3 GB free, every
   container was dropped mid-run and four API tests failed with `Failed to connect to
   127.0.0.1`, which looks exactly like a code failure. Run them serially; check
   `docker compose ps` before believing a red run.
7. **A leftover `dotnet run` locks the build** (`Get-Process Pos.Api | Stop-Process -Force`) and
   holds :5013, which also breaks `pnpm generate:api`.
8. **EF's `SqlQuery<T>` maps by snake_case, not by your alias.** `AS "OwnerId"` looks up
   `owner_id` and fails; single-word aliases like `AS "Value"` work.

## What Phase 7 left owing

- **A deactivated user's access token still works for up to ~15 minutes.** Refresh tokens are
  revoked and `IsActive` is checked at login, PIN entry and `/auth/me` — but not while validating
  a JWT. Closing it needs a per-request liveness read or a token-version claim. Documented under
  the deactivate route in `docs/API.md`. **8.4's observability makes this more visible, not less**
  — worth deciding during 8.6 whether to close it before a real client.
- **No password change or reset flow.** An owner sets an initial password and reads it out; the
  person cannot change it afterwards. This is a real gap for a shipped product and it is not on
  any phase's list. 8.7's runbook has to say what to do when somebody's password leaks.
- **`AuthorizationRefused` covers sale adjustments only**, not every `403`.
- **`GET /employees` is unpaginated.** Right for tens of staff; a chain with hundreds would want
  the cursor, which needs `CursorPaging` to stop requiring `TenantEntity`.
- **Two simultaneous receipt requests can both call themselves the first copy.**

## Outstanding from earlier phases

- **`/reports/sales-summary` and `/reports/top-products`** — documented, *"Not built —
  deferred"*, no screen.
- **Margin cost is not snapshotted.** `SaleLine` records no cost price, so `/reports/margins`
  restates itself when a supplier's price changes. A schema change and a decision, not a query.
- **The tender pad has no "clear all"** for a mis-keyed split.

## Not verified, and Phase 8 will not change most of it

- **The beep.** Outstanding since 5.2, now seven sessions. Headless has no audio device; it needs
  a person with speakers on, and it fires in two places.
- **A real printer.** Print output has only ever been checked under Chromium's print emulation,
  which honours the stylesheet but not a thermal printer's unprintable margins.
- **The audit log has never been read by an owner looking for something.** It is tested and it
  renders; whether it answers the question somebody actually asks is unknown.
- **Nobody who has worked a till has used any of it.** Still the real bar, and no phase gate
  clears it. Phase 8 ends with an MVP — `DECISIONS.md` argues for getting a real client on it
  before Phase 9, and that argument gets stronger the more phases go by.

## Two decisions Phase 8 has to make

- **Hosting provider (8.2).** Fly.io, Azure App Service, or a VPS — all three are written up in
  the phase doc with what each is good for. **Record the choice in `DECISIONS.md`**, not just in
  the deploy config.
- **Pricing/business model.** One-time purchase vs. recurring, given that we host. Still open.
  Blocks nothing until Phase 10, but it must be settled before quoting a price to anyone — and
  8.2 picks the cost base it has to cover.
