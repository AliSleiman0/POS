# Session Handoff

**Written:** 2026-07-31 · **Branch:** `phase-1/tenancy-auth` (7 commits ahead of `main`, not pushed) · **Phase 1: 1.1–1.6 done, 1.7 next**

> This file is session state, not durable truth. It goes stale — overwrite it at the end of each session. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`docs/phases/PHASE-1-tenancy-auth.md`](phases/PHASE-1-tenancy-auth.md) — **amended and now trustworthy.** The two places it contradicted `DECISIONS.md` (§1.3 on the user query filter, §1.6 on `SET LOCAL`) are corrected in the file itself, and 1.1–1.6 exit criteria are ticked.
2. [`DECISIONS.md`](../DECISIONS.md) → **"Resolved 2026-07-31 (during Phase 1)"** — the four load-bearing isolation decisions. Unchanged this session; everything built matches them.
3. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants.

## State

**Working tree clean, build warning-free, 97 tests green** — 23 Core, 31 Data, 43 Api. Data and Api run against real Postgres via Testcontainers.

Landed this session:

- `25939fe` **1.5 PIN login** — `Register` + `/registers`, `X-Device-Token` as an authentication scheme, `/auth/pin`, `/employees/pin-eligible`, `/employees/{id}/set-pin`, device-partitioned rate limiter.
- **1.6 RLS hardening** — `TenantConnectionInterceptor`, the `RowLevelSecurity` migration, the `pos_app` bootstrap, and `RowLevelSecurityTests`.

### Your machine changed

- **The local dev database was destroyed and rebuilt** (`docker compose down -v && up -d`). It held nothing but migrations. `docker/postgres-init/01-app-role.sh` now runs on first init and creates `pos_app`.
- **`ConnectionStrings:Postgres` in user-secrets now points at `pos_app`**, not `pos`. This is what makes running the API by hand exercise RLS the same way the tests do.
- **`Jwt:SigningKey`, `Jwt:Issuer` and `Jwt:Audience` are now set** in user-secrets (dev-only, randomly generated). The API boots; `/health/ready` returns Healthy and `POST /auth/login` answers.

**`dotnet ef database update` now needs the owner connection string passed explicitly** — `pos_app` has no `CREATE` on the schema, by design. The full command is in [`CLAUDE.md`](../CLAUDE.md#commands). Forgetting it fails with "permission denied for schema public".

## Next: 1.7, the isolation test suite

The last milestone of the phase, and the one the phase exists to pass. `tests/Pos.Api.Tests/Isolation/`. **Do not start Phase 2 until it is green.**

Seed two tenants with deliberately similar data — same product names, same emails, same SKUs — so a leak is unmistakable rather than something to be reasoned about from ids. The checklist is in the phase doc; the parts not already covered elsewhere:

- Every collection endpoint, as tenant B, returns none of tenant A's rows.
- `GET /{resource}/{tenantA-id}` as tenant B → **404, not 403**. (`RegisterEnrollmentTests.Another_tenants_till_is_not_found_rather_than_forbidden` is the pattern to copy.)
- A request body containing tenant A's `TenantId` is rejected, not honoured.
- A token with a forged or absent `tenant_id` claim is rejected.

Much of this is already true and partly proven — the value of 1.7 is that it is proven *systematically*, across every endpoint, in one place that Phase 2 extends rather than reinvents. With only `/registers` and `/employees` existing so far, consider writing it so adding an endpoint in Phase 2 means adding a row to a table rather than a new test.

## Things that will bite you

New this session (1–4); the rest carried forward and still true.

1. **An applied migration does not re-run.** The RLS catalog loop covers the tables that existed when it ran — it does **not** reach a table added in Phase 2. Any migration introducing a tenant-owned table must call `migrationBuilder.ApplyTenantRowLevelSecurity()`. `RowLevelSecurityTests.Every_tenant_owned_table_is_covered` fails the build if it does not; that test is the mechanism, not a nicety.
2. **A superuser bypasses RLS unconditionally, `FORCE` or not.** The test container's owner account is a superuser, so a suite run as the owner passes with no policies in existence at all. Both fixtures now connect the *application* as `pos_app`. `The_application_role_does_not_bypass_row_level_security` guards it. Verified by deliberately pointing one isolation test at the owner connection and watching it fail.
3. **A reset Postgres session variable reads as `''`, not NULL** — and `''::uuid` raises. The policies use `nullif(current_setting('app.tenant_id', true), '')::uuid` so both unset and reset collapse to NULL, i.e. no rows. Fails closed, which is the only acceptable direction.
4. **`dotnet run` uses `launchSettings.json` and ignores `ASPNETCORE_URLS`** set in the environment. It listened on 5013 while I was polling 5199. Use `--urls` or read the port out of the startup log.
5. **An authorization policy that does not name its scheme evaluates the default one.** `EnrolledDevice` must say `.AddAuthenticationSchemes(DeviceTokenAuthenticationHandler.SchemeName)`, or a cashier's ordinary JWT satisfies "enrolled device". Covered by `An_ordinary_access_token_does_not_stand_in_for_a_device_token`.
6. **A request can carry both a JWT and a device token.** `AmbientTenantContext.Resolve` refuses to switch tenants, so tenant A's bearer token plus tenant B's device token threw a 500 until it was guarded. Anything else resolving a tenant pre-authentication needs the same check.
7. **`WebApplicationFactory.ConfigureAppConfiguration` is applied at `Build()`**, but `Program.cs` reads `builder.Configuration` before it. **Use `builder.UseSetting(...)`.**
8. **Never run the test host in `Development`.** It loads your user secrets, whose connection string points at local Docker. `PosApiFactory` uses `"Testing"`.
9. **`problem+json` carries a per-request `traceId`.** Compare `type`/`title`/`status`/`detail`, never raw strings.
10. **EF names tables from the `DbSet` property name**, not the entity type.
11. **`[CallerFilePath]` is a lie under CI.** Anchor on `AppContext.BaseDirectory`. `$env:CI="true"` reproduces CI-only build behaviour locally.
12. **`pnpm/action-setup` ignores `defaults.run.working-directory`.**
13. **`docker compose ps` says "Running" for a crash-looping container.** Verify with `docker inspect -f '{{.State.Status}} restarts={{.RestartCount}}'`.
14. **Kill stray `Pos.Api` processes before rebuilding** — they lock `Pos.Data.dll` (`MSB3027`). Use `Stop-Process`, not `pkill -f`.
15. **Adding shadcn components may reintroduce the two-React-copies error.** Fix is `test.server.deps.inline` in `vite.config.ts`.

**A test-fixture trap worth remembering:** the `pin-eligible` leak test asserts the response contains no role name, and the fixture's display name was "Cal Cashier". Fixtures need names that do not contain the strings the test proves absent, or the assertion is about the fixture.

## Working preferences noted

- **Don't run `dotnet build` and `dotnet test` back to back as a habit** — `dotnet test` builds. A separate build is only worth it while iterating on compile errors.
- **UI testing must not accumulate.** In Phases 4–7 a milestone is not done until its Vitest/Playwright coverage exists *and* someone has looked at the screen. jsdom does not paint.
- **Prove a security test can fail.** Twice this session a green test meant nothing until it was falsified deliberately. Worth the thirty seconds every time.

## Outstanding / deferred

- **`dotnet dev-certs https --trust`** still not run. Opens a Windows dialog that cannot be scripted.
- **Playwright browsers not installed** — `pnpm exec playwright install --with-deps` in Phase 4, a ~500 MB download nothing needs before then.
- **No visual verification of the web UI.** Phase 1 is backend only. Eyeball it before Phase 4 builds on top.
- **Production `pos_app` password.** `docker-compose.yml` defaults `POSTGRES_APP_PASSWORD` to the same throwaway as everything else in it. Phase 8.2 must supply a real one — the compose file already reads it from the environment, so this is configuration, not a code change.
- **FK constraint names in the Identity migration read `fk_role_claim_asp_net_roles_role_id`** — cosmetic leftover from Identity's default table names. Harmless.
- **CI actions emit a Node 20 deprecation warning.** Bump to `@v5` when available.
- **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc. Remove the pin when ASP.NET Core ships a patched dependency.
- **CI has never run the RLS suite.** It should just work — the fixtures create `pos_app` themselves — but this branch has not been pushed, so that is unverified.
- **Smart App Control is off** (irreversible, done 2026-07-31). Local `dotnet test` works.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price: "lifetime hosting for a one-time fee" is a liability that grows with every tenant.
