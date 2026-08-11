# Session Handoff

**Written:** 2026-08-11 · **Branch:** `phase-8/deployment` (pushed, CI green, **not merged**) ·
**Phase 8 complete — the MVP line is crossed**

> Phases 0–8 are done. The product is **deployed, verified end to end, and recoverable**.
> A cash sale has been rung on it, a restore has actually been performed, and a cross-tenant
> probe has been run against the deployed instance.
>
> Next is **Phase 9 (offline)** — or a real shop. [`DECISIONS.md`](../DECISIONS.md) argues for
> the shop, and now there is a URL to hand somebody.

> This file is session state, not durable truth. Overwrite it when you finish. Durable
> decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in
> [`ROADMAP.md`](ROADMAP.md), operational procedure in [`RUNBOOK.md`](RUNBOOK.md).

---

## Before anything else

1. **Merge `phase-8/deployment`.** Pushed, all four CI jobs green, 12 commits, ~60 files.
2. **`deploy.yml` fires on the first green CI run on `main`** and will fail at its first step,
   because none of `DATABASE_OWNER_URL`, `RENDER_API_DEPLOY_HOOK`, `RENDER_WEB_DEPLOY_HOOK`,
   `API_ORIGIN` or `WEB_ORIGIN` exists as a GitHub secret yet. A loud, harmless failure — but
   set them before merging, or the first thing `main` does is go red. See
   [`RUNBOOK.md § Secrets`](RUNBOOK.md#secrets-and-where-each-one-lives).
3. **Render's auto-deploy is still ON.** The runbook says to turn it off so the pipeline's
   migration gate is real. It was left on deliberately to iterate on a branch. **Turn it off
   when the pipeline takes over**, or the gate is decorative.
4. **The deployed database is deleted on 2026-09-10.** Free plan, 30 days, no automatic
   backups. If anything on it matters, `pg_dump` it first — and read
   [`RUNBOOK.md § Restoring from backup`](RUNBOOK.md#restoring-from-backup) before you do,
   because the obvious command produces a broken file.

## Where it is deployed

| | |
|---|---|
| Web | https://pos-web-lcc5.onrender.com |
| API | https://pos-api-jc43.onrender.com |
| Login | `harbour-stores` / `owner@harbourstores.example` / `Harbour-Verify-1` |

**This is a development environment and is recorded as one in `DECISIONS.md`.** The API sleeps
after ~15 minutes (about a minute to wake — the first page load renders an empty shell before
recovering), and the database expires. Upgrading the API instance is the one change that makes
it production-viable; it is not a rewrite.

## What Phase 8 actually proved

Not "the code was written" — each of these was run against the deployed instance:

- A **cash sale**: €1.20 = €0.98 + €0.22 at 23% inclusive, €2.00 tendered, €0.80 change from
  the server, stock 50 → 49, receipt rendered with the shop's address and tax number.
- The **trading day** bounded at 03:00–03:00 UTC = 04:00 Dublin. ICU and real IANA data, in a
  container, in production.
- `/health/ready` green — which is the database **and** the row-level-security role check, so
  "production connects as the non-owner role" is proven rather than claimed.
- CORS echoes the allowed origin and stays silent for any other. `/openapi/v1.json` and
  `/scalar/` both 404.
- A **restore drill**, which found a real defect (below).
- A **cross-tenant probe**: 33 claims, no violations, by-id routes answering 404 not 403.

## The two findings worth carrying forward

1. **`pg_dump` silently produces a broken backup.** `FORCE ROW LEVEL SECURITY` applies to the
   table owner, and managed Postgres gives no superuser — so pg_dump exits 1 **and still
   leaves a plausible file**. The first one was 89 KB, `pg_restore --list` read it happily and
   reported 27 tables, and the users table's data was missing: a backup nobody can log in
   from. The corrected procedure, with the exit-code check, is in the runbook. **This applies
   to any host that does not give you a superuser**, so it survives a move off Render.

2. **Render's proxy breaks SCRAM channel binding.** Npgsql fails with
   `28000: SCRAM channel binding check failed` until the connection string carries
   `Channel Binding=Disable`. `SSL Mode=VerifyFull` is used alongside it so the certificate is
   still validated. `PostgresConnectionString` now also translates libpq's spellings
   (`sslmode=verify-full` → `SSL Mode=VerifyFull`), which is what pasting the platform's own
   URL would otherwise trip over.

## Phase 9 — read these first

[`docs/phases/PHASE-9-offline.md`](phases/PHASE-9-offline.md), all five milestones, and the
**Note at the bottom** — it names the honest fallback if this proves harder than expected, and
it is the most important paragraph in the file.

Then [`DECISIONS.md`](../DECISIONS.md) → "Offline Requirements", which already settled the
shape: PWA, IndexedDB, client-generated GUIDs, and **oversell flagged for staff review rather
than silently corrected**.

### Why this is an addition rather than a rewrite

**Phase 3.5's idempotency is the whole reason this phase is tractable.** The outbox replays the
*same* `POST /sales` with its original `Idempotency-Key`. That means no bulk-upload endpoint,
no separate sync API, and no second server-side code path where the pricing rules could
disagree with themselves. Do not invent one.

Already true and load-bearing for 9.3:

| Inherited | Where |
|---|---|
| `Idempotency-Key` fingerprints the **raw body**; same key + different body is `409 idempotency-key-reused` | `IdempotencyFilter` |
| The cart and its sale GUID already survive a reload | Phase 5.5, `features/register/storage.ts` |
| "Did my sale land?" is answered by **asking**, never by re-POSTing | `GET /sales/by-client-transaction/{id}` |
| A sale that oversells already raises a `StockDiscrepancy` instead of failing | Phase 3.6, surfaced at `GET /stock/discrepancies` |
| `SaleLine` snapshots price, tax and discount, so a queued sale keeps what the customer was charged | Phase 3.3 |

**9.4's "duplicate submission" is therefore already solved.** The genuinely new work is
oversell reconciliation and the review UI.

### Current code Phase 9 will touch

- `src/Pos.Web/src/api/client.ts` — the generated client and its single-flight refresh. An
  offline request must not trigger a refresh storm; `refreshSession` already collapses
  concurrent callers, but the offline path is a new caller.
- `src/Pos.Web/src/features/register/storage.ts` — `sessionStorage` today. 9.3 needs IndexedDB
  and **durability across a browser close**, which `sessionStorage` deliberately does not give
  (invariant 11: a shared till hands the next shift nothing). **Reconcile those two intentions
  explicitly** rather than quietly widening the storage — a queued sale surviving a shift
  change is a different decision from a cart surviving one.
- `RegisterPage.tsx` barcode lookup — 9.2 wants IndexedDB first, always.
- `vite.config.ts` — no PWA plugin yet.

## Things that will bite you

1. **`InvariantGlobalization` must stay `false`**, and the container base image is part of
   that: `aspnet:10.0-noble-chiseled-extra`. Plain `-chiseled` has no ICU and no tzdata, and
   fails *only inside the container* while CI and every dev machine stay green.
2. **`.editorconfig` and `e2e/` are deliberately in the Docker build contexts.** Both were
   excluded first and both made the container verify *less* than CI does. Do not tidy them out.
3. **A rate-limited login looks like a broken test.** The login limit is per source address and
   every Playwright spec comes from `127.0.0.1` — the same collision a shop behind NAT has.
   `playwright.config.ts` sets `RateLimits__LoginAttemptsPerWindow` high for that reason. If
   e2e specs time out at `waitForURL` with the sign-in form still on screen, that is a 429.
4. **The .NET suite gives every request a unique client address** (`ClientAddressStartupFilter`).
   Without it the whole assembly is one rate-limit partition and ~600 tests fail for one
   fixture reason.
5. **Origin values are a matched set** — `Jwt__Issuer`/`Audience`, `Cors__AllowedOrigins__0`,
   `VITE_API_BASE_URL` and the CSP's `connect-src`. Render appends a random suffix to service
   names, which is how all four were wrong on the first deploy. The symptom is the least
   helpful in the system: the API reports perfectly healthy while the browser blocks
   everything.
6. **Still true from before:** `pnpm format:check` is its own CI step; port 5173 with
   `reuseExistingServer`; never run `dotnet test` and Playwright at once (Docker starves); a
   leftover `dotnet run` locks the build and holds :5013; EF's `SqlQuery<T>` maps by
   snake_case, so single-word aliases like `AS "Value"` only.

## Accepted debts — decided, not forgotten

All three are recorded with their triggers in [`DECISIONS.md`](../DECISIONS.md):

- **`SentryScrubber` does not scrub exception messages**, and Npgsql puts the connection string
  in one. Harmless while no DSN is configured — the SDK is inert. **The DSN is the trigger:**
  wiring real error tracking without closing this sends a live database credential to a third
  party on the first connection failure.
- **`pos_app` keeps `DELETE` on `sale`, `sale_line`, `tender`, `stock_movement`**, which
  invariant 4 calls append-only. Enforced by code and by `No_route_updates_or_deletes_a_sale`;
  the grant is a missing second layer, not an open door.
- **The deployment is dev.** Sleeping API, expiring database, dev-grade credentials.

## Longer-standing gaps Phase 8 did not change

- **No password change or reset flow.** An Owner sets an initial password and cannot change it.
  `RUNBOOK.md` documents the manual procedure and a credential-leak exposure table, which is a
  stopgap and not a fix. On no phase's list, and still the largest gap for a shipped product.
- **A deactivated user's access token works for up to ~15 minutes.** Rotating the signing key
  is the only immediate remedy.
- **`/reports/sales-summary` and `/reports/top-products`** — documented, not built.
- **Margin cost is not snapshotted.** `SaleLine` records no cost price, so `/reports/margins`
  restates itself when a supplier's price changes.
- **The beep** (unverified since 5.2 — eight sessions), **a real thermal printer**, and
  **whether the audit log answers the question somebody actually asks**.
- **Nobody who has worked a till has used any of it.** No phase gate clears this, it is still
  the real bar, and it is now the cheapest thing on this list to fix.

## Still open

- **Pricing/business model.** One-time vs. recurring, given that we host. Blocks nothing until
  Phase 10, but must be settled before quoting anyone. Phase 8 fixed the cost base it has to
  cover: **$0/month as deployed**, and the price of one small instance plus a paid Postgres
  for a shop that cannot tolerate a sleeping till.
