# Session Handoff

**Written:** 2026-08-11 · **Branch:** `phase-8/deployment` (pushed, CI green) · **Phase 8 part-done**

> Everything in Phase 8 that can be built and proven **without a hosting account has been**.
> What is left is not more code — it is running the code against real infrastructure.

> This file is session state, not durable truth. Overwrite it when you finish. Durable
> decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in
> [`ROADMAP.md`](ROADMAP.md), operational procedure in [`RUNBOOK.md`](RUNBOOK.md).

---

## Before anything else

**Create a Render account and apply the blueprint.** Hosting moved from Fly to Render on
2026-08-11 because Fly requires a payment card and the owner is not adding one; the reasoning
and the two limitations accepted with it are in [`DECISIONS.md`](../DECISIONS.md). `flyctl` is
installed and authenticated but nothing uses it — the Fly config has been removed.

1. Sign up at render.com with GitHub (no card), and give it access to this repository.
2. **New → Blueprint**, pick this repo. It reads `render.yaml` and creates three resources.
3. Follow [`RUNBOOK.md § Creating the pos_app role`](RUNBOOK.md#creating-the-pos_app-role),
   then [`§ Secrets`](RUNBOOK.md#secrets-and-where-each-one-lives) — including **turning
   Render's auto-deploy off**, or it races the migration gate.

A Sentry account (free, no card) and its DSN unblocks the last part of 8.4.

Everything is green: **1387 .NET** (288 Core, 134 Data, 965 Api), **225 Vitest**,
**60 Playwright**, web lint/format/build clean — locally *and* on the runner, where all four
CI jobs passed for this branch.

## What is done

| | State |
|---|---|
| **8.1 Containerize** | ✅ Done and verified |
| **8.1b Web base URL** | ✅ Done — `VITE_API_BASE_URL` |
| **8.2 Hosting** | 🔨 Code done. **Provisioning outstanding** |
| **8.3 Migrations in CI/CD** | ✅ Written and locally verified. **Pipeline has never run** |
| **8.4 Observability** | ✅ Done bar a live Sentry DSN |
| **8.5 Backups + restore** | ⏸️ Blocked. Procedure written, drill not executed |
| **8.6 Security pass** | 🔨 Code done. **Production probe outstanding** |
| **8.7 Onboarding** | ✅ Done and verified by using it |

## What is left, in the order to do it

1. **Apply the blueprint and set up the role and secrets**, as above. Render service names
   are unique per account rather than globally, so `pos-api` / `pos-web` should be available —
   but if either changes, **three places must change with it**: `Jwt__Issuer`/`Audience` and
   `Cors__AllowedOrigins__0` on the API, `VITE_API_BASE_URL` on the static site, and the CSP's
   `connect-src` in `render.yaml`. **They are a matched set**: get one wrong and the browser
   blocks every call while the API reports perfectly healthy.
2. **Confirm `CREATE ROLE` actually works** on Render's free Postgres. If it does not, the app
   would connect as the database owner — not fatal, because Phase 1.6 sets `FORCE ROW LEVEL
   SECURITY` so policies still apply to the owner, but it is protection by a different
   mechanism than `RowLevelSecurityHealthCheck` tests for. Record it as a deviation rather
   than accept it quietly.
3. **8.5, the restore drill.** The single most valuable checkbox in the phase.
   `RUNBOOK.md § Restoring from backup` has the procedure with the timing deliberately blank —
   **fill it in from a real run.** Do not tick it from a snapshot existing.
4. **8.6, the production probe.** Not built. The plan was: have `IsolationManifest` emit
   `isolation-manifest.json`, and add `tools/Pos.Probe` to replay the `Collection` and `ById`
   cases over HTTPS against two throwaway tenants. The manifest stays the single source of
   truth so the probe inherits new endpoints for free. Onboard `probe-a`/`probe-b`, run it,
   deactivate them.
5. **8.8, the seven verification steps** in the phase doc, against the deployed environment.

## Things that will bite you

1. **`InvariantGlobalization` must stay `false`, and the base image is now part of that.**
   `aspnet:10.0-noble-chiseled-extra` — the `-extra` carries ICU and tzdata. Plain
   `-chiseled` has neither, and the failure is *only inside the container*: CI and every
   developer machine stay green while every receipt timestamp and business-day boundary
   breaks in production.
2. **`.editorconfig` and `e2e/` are deliberately in the build contexts.** Both were excluded
   first, and both made the container verify *less* than CI does — `.editorconfig` carries the
   analyzer severities (12 errors in untouched migration files), and `pnpm build` type-checks
   `e2e/` through `tsc -b`. Do not "tidy" them back out.
3. **The origin values are a matched set.** Repeated because it is the failure with the least
   helpful symptom: the API reports perfectly healthy while the browser blocks everything.
   Also: a free Render service **sleeps after ~15 minutes** and takes about a minute to wake,
   so the first request after a quiet spell looks like an outage and is not one. The
   post-deploy check in `deploy.yml` retries for five minutes for exactly this reason.
4. **A rate-limited login looks like a broken test.** The login limit is per source address,
   and every Playwright spec comes from `127.0.0.1` — which is the same collision a shop
   behind NAT has. `playwright.config.ts` sets `RateLimits__LoginAttemptsPerWindow` high for
   that reason. If e2e specs start timing out at `waitForURL` with the sign-in form still on
   screen, that is a 429 and not a UI bug.
5. **The .NET suite gives every request a unique client address** (`ClientAddressStartupFilter`).
   Without it the whole assembly is one rate-limit partition and ~600 tests fail for one
   fixture reason.
6. **Still true from before:** `pnpm format:check` is its own CI step; port 5173 with
   `reuseExistingServer`; never run `dotnet test` and Playwright at once (Docker starves); a
   leftover `dotnet run` locks the build and holds :5013; EF's `SqlQuery<T>` maps by
   snake_case, so single-word aliases like `AS "Value"` only.

## Two accepted behaviours in the container

Neither is a defect; both would otherwise be rediscovered from a log at an awkward moment.

- `Cannot load library libgssapi_krb5.so.2`, twice at connection-pool start. Npgsql probes for
  GSSAPI; the chiseled image has no Kerberos library and no package manager to add one.
  Password auth succeeds and every query runs.
- Data Protection keys are not persisted, so each machine generates its own. Nothing depends
  on them — `AddDefaultTokenProviders()` is deliberately absent, auth is JWT with our own
  signing key, and refresh and device tokens are opaque and hashed. **If a password-reset flow
  is ever added this stops being harmless**, and that is the trigger to add a shared key ring.

## What Phase 8 has not changed

- **No password change or reset flow.** An Owner sets an initial password and cannot change
  it. `RUNBOOK.md` now documents the manual procedure and the credential-leak exposure table,
  which is an honest stopgap and not a fix. This is the largest remaining gap for a shipped
  product and it is on no phase's list.
- **A deactivated user's access token still works for ~15 minutes.** Rotating the signing key
  is the only immediate remedy; documented in the runbook.
- **`/reports/sales-summary` and `/reports/top-products`** — documented, not built.
- **Margin cost is not snapshotted.** `SaleLine` records no cost price, so `/reports/margins`
  restates itself when a supplier's price changes.
- **The beep, a real thermal printer, and an owner actually reading the audit log** remain
  unverified — eight sessions for the beep now.
- **Nobody who has worked a till has used any of it.** Still the real bar, and no phase gate
  clears it. `DECISIONS.md` argues for getting a real client on this before Phase 9, and that
  argument gets stronger every phase.

## Still open

- **Pricing/business model.** One-time vs. recurring, given that we host. Blocks nothing until
  Phase 10 but must be settled before quoting anyone — and 8.2 has now fixed the cost base it
  has to cover (roughly $5–15/month per deployment on Fly).
