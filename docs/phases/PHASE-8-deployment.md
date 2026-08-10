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
- [x] Image builds and runs locally against the Compose Postgres — `/health/ready` answers
      `Healthy` connecting as `pos_app`, and `GET /reports/daily` returns a business day bounded
      at 23:00 UTC for `Europe/Dublin`, which is ICU and real tz data resolving inside the
      container. That last check is the one that matters: it is what `-chiseled-extra` buys and
      what plain `-chiseled` would have broken silently.
- [x] Runs as non-root — UID 1654, inherited from the chiseled base and not overridden
- [x] No secrets baked in — inspected, not assumed: image env carries only the base image's own
      variables, `docker history` names no credential, and the exported filesystem contains only
      the two committed `appsettings*.json` (logging configuration alone), zero `.cs`/`.csproj`,
      no SDK, no shell and no package manager
- [x] Image size sane (<250 MB) — 168 MB exported filesystem, 75.6 MB content size

**Also landed here** (found while building the above, and load-bearing for 8.2):

- **`Hosting:BehindTlsTerminatingProxy`.** Behind an edge that terminates TLS,
  `UseHttpsRedirection` sees plain `http` and answers 307 to the URL the caller was already on —
  every request loops and the API serves nothing while the process stays up and health checks
  keep passing. The flag registers `UseForwardedHeaders` and skips the redirect. A flag rather
  than an environment check because "is something in front of me" is a fact about the
  deployment, not about Production; and rather than always-on because `RateLimitPolicies`
  partitions unauthenticated PIN attempts on the remote address, which a trusted-by-default
  `X-Forwarded-For` would make spoofable. Four tests in `TlsTerminationTests`.
- **`.editorconfig` ships in the build context.** `EnforceCodeStyleInBuild` is on and warnings
  are errors, so it is part of the build contract — it is where CA1861 is disabled for generated
  migrations. Excluding it made the image build apply stricter rules than the host and fail on
  twelve analyzer errors in committed, unmodified files.
- **The web client no longer assumes same-origin.** `VITE_API_BASE_URL`, resolved and validated
  once in `src/api/baseUrl.ts`, replacing `window.location.origin` in both clients *and* in the
  two raw `fetch` calls that bypassed them — `auth/refresh.ts` and the `AppLayout` health
  indicator. The refresh one was the dangerous one: cross-origin it would have fetched the static
  host's `index.html`, got a 200, failed to parse, and signed the cashier out at the first token
  rotation. Unset means same-origin, so development and all 60 Playwright specs are unchanged.

**Two accepted, documented behaviours** (neither is a defect, both would otherwise be
rediscovered from a log):

- `Cannot load library libgssapi_krb5.so.2` appears twice at connection-pool start. Npgsql probes
  for GSSAPI; the chiseled image has no Kerberos library and no package manager to add one.
  Password authentication succeeds and every query runs. Cosmetic.
- Data Protection keys are not persisted, so each machine generates its own. Nothing depends on
  them: `AddDefaultTokenProviders()` is deliberately absent (`IdentityServiceCollectionExtensions`),
  auth is JWT with our own signing key, and refresh and device tokens are opaque and hashed in the
  database. **If a password-reset flow is ever added, this stops being harmless** — that is the
  trigger to add a shared key ring.

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

**Start from `tools/Pos.Seed`, which already does the mechanical half** (added 2026-07-31 for dev). It creates the tenant, roles, users and registers through `AddPosData` and `UserManager`, so the rows it writes are stamped, scoped and loggable-into. What it is *not*, and what this milestone has to add: it takes a connection string rather than talking to a deployed API, it prints a fixed dev password to stdout, and it does not set `TaxMode` or the business-day offset — the two values that are immutable after trading starts. Hardening it beats writing a second tool that drifts from this one.

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
