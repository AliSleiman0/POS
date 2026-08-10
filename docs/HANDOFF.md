# Session Handoff

**Written:** 2026-08-10 · **Branch:** `phase-8/deployment` (6 commits, unpushed) · **Phase 8 part-done**

> Everything in Phase 8 that can be built and proven **without a Fly account has been**. What
> is left is not more code — it is running the code against real infrastructure.

> This file is session state, not durable truth. Overwrite it when you finish. Durable
> decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in
> [`ROADMAP.md`](ROADMAP.md), operational procedure in [`RUNBOOK.md`](RUNBOOK.md).

---

## Before anything else

**`fly auth login`.** `flyctl v0.4.79` is installed (`winget install --id Fly-io.flyctl` —
note the id is `Fly-io.flyctl`, not `Fly.Flyctl`). It is not authenticated, and that one
command is what unblocks the remaining four items. A Sentry account (free, no card) and its
DSN unblocks the last part of 8.4.

Everything is green as of this commit: **1375 .NET** (288 Core, 122 Data, 965 Api),
**225 Vitest**, **60 Playwright**, web lint/format/build clean. CI on `main` was green at
`d1ee916` before the branch started — the two red runs the previous handoff warned about were
superseded by that commit, so that warning is closed.

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

1. **`fly auth login`**, then provision. App names on Fly are global, so `pos-api` / `pos-web`
   are probably taken — pick a suffix and **change it in four places**: `fly.toml`,
   `src/Pos.Web/fly.toml` (the `VITE_API_BASE_URL` build arg), `src/Pos.Web/Caddyfile` (the
   CSP's `connect-src`) and `.github/workflows/deploy.yml` (`API_APP`, `WEB_APP`,
   `API_ORIGIN`). **They are a matched set**: get one wrong and the browser blocks every call
   while the API reports perfectly healthy.
2. **Create the `pos_app` role by hand** on the Fly Postgres, mirroring
   `docker/postgres-init/01-app-role.sh` *and* the Phase 7.0 `REVOKE UPDATE, DELETE`. The
   readiness probe refuses to serve if the app connects as anything with `BYPASSRLS`, so a
   mistake here shows up as a machine that never takes traffic rather than as a silent leak.
3. **Set the secrets** (`fly secrets set`): `ConnectionStrings__Postgres` (as `pos_app`),
   `Jwt__SigningKey`, `Jwt__Issuer`, `Jwt__Audience`, `Cors__AllowedOrigins__0`, `Sentry__Dsn`.
   And the GitHub secrets `deploy.yml` reads: `FLY_API_TOKEN`, `POSTGRES_APP`,
   `POSTGRES_OWNER_USER`, `POSTGRES_OWNER_PASSWORD`, `POSTGRES_DATABASE`.
4. **8.5, the restore drill.** The single most valuable checkbox in the phase.
   `RUNBOOK.md § Restoring from backup` has the procedure with the timing deliberately blank —
   **fill it in from a real run.** Do not tick it from a snapshot existing.
5. **8.6, the production probe.** Not built. The plan was: have `IsolationManifest` emit
   `isolation-manifest.json`, and add `tools/Pos.Probe` to replay the `Collection` and `ById`
   cases over HTTPS against two throwaway tenants. The manifest stays the single source of
   truth so the probe inherits new endpoints for free. Onboard `probe-a`/`probe-b`, run it,
   deactivate them.
6. **8.8, the seven verification steps** in the phase doc, against the deployed environment.

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
3. **The four app-name/origin places above are a matched set.** Repeated because it is the
   failure with the least helpful symptom.
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
