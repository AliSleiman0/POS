# Phase 8 — Deployment & Hardening

**Goal:** the MVP runs on real infrastructure, holding real money data, with backups that have been proven to restore.

**Completing this phase completes the MVP.**

**Depends on:** Phases 0–7.

---

## 8.1 Containerize

`src/Pos.Api/Dockerfile` — multi-stage: SDK image restores and publishes, runtime image (`aspnet:10.0-alpine` or `-noble`) runs it.

- Non-root user
- No SDK, no source, no secrets in the final image
- `.dockerignore` excluding `bin/`, `obj/`, `node_modules/`, `.env*`
- Web built to static assets (`pnpm build` → `dist/`), served by a CDN/static host — **not** by the API. Different scaling and caching characteristics, and serving a SPA from your API means a frontend deploy restarts your backend.

**Exit criteria**
- [ ] Image builds and runs locally against the Compose Postgres
- [ ] Runs as non-root
- [ ] No secrets baked in (inspect the layers, don't assume)
- [ ] Image size sane (<250 MB)

## 8.2 Hosting

Pick and record the choice here. Candidates, all viable:

| Option | Fits when |
|---|---|
| **Fly.io + managed Postgres** | Cheapest credible start, simple deploys, regions near clients |
| **Azure App Service + Azure Postgres** | Familiar .NET tooling, easiest if you already have Azure credit |
| **VPS (Hetzner/DO) + managed Postgres** | Cheapest at scale, most operational work — you own patching and uptime |

Whatever is chosen:

- **HTTPS only**, HSTS, TLS termination at the edge
- **CORS locked to the web origin.** Not `*` — a POS API accepting any origin is an open door.
- Managed Postgres with automated backups and connection pooling
- Secrets from env/vault. **Never** in the image, the repo, or `appsettings.json`.
- The app connects as the non-owner, RLS-subject role from Phase 1.6 — verify this explicitly in production, because connecting as owner silently disables every RLS policy while leaving them visibly "enabled"

**Exit criteria**
- [ ] API reachable over HTTPS at a stable URL
- [ ] Web served from its host, calling the API successfully
- [ ] CORS rejects other origins, verified
- [ ] Production connects as the non-owner role, verified with `SELECT current_user`
- [ ] No secret present in the repo or image

## 8.3 Migrations in CI/CD

An explicit, gated deploy step:

```
build → test → publish image → **run migrations** → deploy API → deploy web
```

Run via `dotnet ef database update` from a one-off job, or a generated idempotent SQL script applied by the pipeline.

**Not `Database.EnsureCreated()`. Not migrate-on-startup.** With more than one API instance, concurrent startup migrations race: two processes apply the same migration, and the schema ends up in a state neither expected. It works in development with one instance, which is exactly why it survives to production undetected.

- Migrations reviewed before merge; a destructive migration (dropped column, narrowed type) needs deliberate sign-off
- Rollback plan documented per release: for additive migrations, redeploy the previous image; for destructive ones, restore from backup — which is why 8.5 must be real

**Exit criteria**
- [ ] Migrations are a separate pipeline step
- [ ] No `EnsureCreated` or startup migration anywhere (grep and confirm)
- [ ] A full deploy from a clean database succeeds
- [ ] Rollback documented

## 8.4 Observability

- **Structured logging** (Serilog or the built-in structured logger) with `TenantId`, `UserId` and `RequestId` on every scope. Without `TenantId` on log lines, "tenant X reports a wrong total" is uninvestigable.
- Health checks: `/health/live` (process) and `/health/ready` (database). Neither leaks version or configuration.
- Error tracking (Sentry or equivalent) with **PII and payment amounts scrubbed**
- Metrics worth alerting on: sale-submission failure rate, p95 barcode-lookup latency, refresh-token failures, stock discrepancies created

**Never logged:** passwords, PINs, tokens, full card data. A PIN in a log file is a PIN in every log aggregator, backup and support screenshot forever.

**Exit criteria**
- [ ] `TenantId` on every request-scoped log line
- [ ] Health checks respond and are wired to the platform's probes
- [ ] Error tracking receives a deliberately triggered test error
- [ ] A grep for logged secrets finds nothing
- [ ] Alerts on sale-submission failures

## 8.5 Backups + restore drill

- Automated daily backups plus point-in-time recovery, retention set deliberately (30 days is a reasonable default)
- **Perform an actual restore into a scratch database and verify the data.** Document the steps and the time it took.

**An untested backup is not a backup.** Most backup failures are discovered during the first restore, which is the worst possible moment. This is the single most valuable checkbox in the phase: you host every client's sales history, and losing it is not a bug you can ship a fix for.

- Also document a tenant-level export ("give me my data"), which is both a support need and, in some jurisdictions, a legal one

**Exit criteria**
- [ ] Automated backups running and verified present
- [ ] **A restore has actually been performed**, with the runbook and timing written down
- [ ] Restore time is known and acceptable
- [ ] Per-tenant export documented

## 8.6 Security pass

- **Rate limiting**: strict on `/auth/login` and `/auth/pin` (brute force), sane on the rest
- **Security headers**: HSTS, `X-Content-Type-Options`, `Referrer-Policy`, and a CSP on the web app
- **Dependency audit**: `dotnet list package --vulnerable`, `pnpm audit`; wire both into CI so a new advisory fails the build rather than sitting unnoticed
- **Re-run the Phase 1.7 isolation suite against the deployed instance.** Local RLS passing does not prove production RLS is active — a misconfigured role is invisible until probed.
- Confirm error responses leak no stack traces or SQL
- Confirm no debug/Swagger UI exposed publicly in production without auth
- PIN and password lockouts verified live

**Exit criteria**
- [ ] Rate limits enforced, verified by hammering login
- [ ] Headers present (check with an external scanner)
- [ ] No known vulnerable dependencies; audit in CI
- [ ] **Cross-tenant probe against production returns nothing**
- [ ] Errors leak nothing; Swagger not public

## 8.7 Tenant onboarding

A repeatable path to create a tenant + its first Owner: a CLI command or a protected endpoint requiring a platform-level secret (not a tenant token).

Sets: tenant name, slug, currency, timezone, **`TaxMode`** (immutable afterwards — Phase 3.2), business day offset, first Owner user.

Per `DECISIONS.md`, **no platform admin UI yet** — direct DB inspection is the accepted stopgap for a handful of tenants, and the real tool gets built after the first paying client, once the operational needs are actually known rather than guessed.

Document, in a runbook: onboarding a tenant, resetting a locked-out Owner, revoking a lost device, investigating "my total is wrong" (which is: find the sale, read its snapshots, read the audit log).

**Exit criteria**
- [ ] Onboarding is one repeatable command
- [ ] It cannot be invoked with a tenant token
- [ ] `TaxMode` is set at onboarding and rejected on later change
- [ ] Runbook written for the four scenarios above
- [ ] A fresh tenant can log in and complete a sale end to end

---

## Verification

Against the deployed environment, not localhost:

1. Onboard a fresh tenant; log in as its Owner
2. Create a product with a barcode; open a shift
3. Complete a cash sale; print the receipt
4. Close the shift; read the Z-report
5. Run the isolation suite against production — confirm no cross-tenant visibility
6. Trigger a test error; confirm it appears in error tracking with `TenantId`
7. Restore a backup into a scratch DB and verify the sale from step 3 is present

**Step 7 is the one to not skip.** It is the difference between believing you have backups and having them.

## MVP complete

Phases 0–8 done means: catalog, barcode checkout, cash payment, receipts, inventory ledger, daily reporting, roles and audit — deployed, monitored, and recoverable. That is a product you can put in front of a paying shop.

Next: Phase 9 (offline), or stop and get a real client using it. `DECISIONS.md` argues for the latter — a platform admin tool and offline sync are both much easier to design once you know what a real user actually needs.
