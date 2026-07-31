# Session Handoff

**Written:** 2026-07-31 · **Branch:** `phase-1/tenancy-auth` (7 commits ahead of `main`, not pushed) · **Phase 1 complete — Phase 2 next**

> **1.7 is written, green and uncommitted.** Everything below is in the working tree; the last commit is 1.6. Commit it before starting Phase 2.

> This file is session state, not durable truth. It goes stale — overwrite it at the end of each session. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`docs/phases/PHASE-2-catalog-inventory.md`](phases/PHASE-2-catalog-inventory.md) — the phase to start. §2.5 already says to extend the 1.7 suite rather than start a parallel one; that is now a concrete instruction, see below.
2. [`DECISIONS.md`](../DECISIONS.md) → **"Resolved 2026-07-31 (during Phase 1)"** — five load-bearing isolation decisions. The fifth is new this session and is a *limit*, not a mechanism: read it.
3. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants.

## State

**Working tree clean, build warning-free, 133 tests green** — 23 Core, 31 Data, 79 Api. Data and Api run against real Postgres via Testcontainers.

Landed this session:

- `5e963fd` **1.6 RLS hardening** — `TenantConnectionInterceptor`, the `RowLevelSecurity` migration, the `pos_app` bootstrap, `RowLevelSecurityTests`.
- **1.7 isolation suite** — `tests/Pos.Api.Tests/Isolation/`, 36 tests. **Phase 1's gate is passed.**

### How 1.7 is built, because Phase 2 has to extend it

`Isolation/IsolationManifest.cs` is a table of **every endpoint** and how its tenancy is proven. `EndpointCoverageTests` diffs it against the router's own endpoint table in both directions.

**So: map an endpoint in Phase 2, and the build fails until you add a manifest row.** Adding the row gives you, at once, the cross-tenant test (collection or by-id) and the negative-authorization test. An endpoint that genuinely needs no isolation test is `Exempt` **and must name the test where its coverage does live** — the manifest is meant to read as an audit record, not a list of skips.

Two details that are easy to undo by accident:

- By-id URLs are **built by substituting into the route template from the manifest key**, never hand-written. A test asserting 404 passes when the URL is wrong, so a hand-written path could reach nothing and look green. The key is the thing the coverage test already matched against the routing table.
- The negative-authorization theory probes `Guid.Empty`, an id in no tenant. If an authorization check were removed the answer is 404, which fails the assertion — rather than a real write against a real till.

### The world is read-only

`TwoTenantWorld` seeds `iso-a` and `iso-b` with **identical** data — same emails, same display names, same till names, same PINs. That is what lets the collection assertions be exact set equality: a leak doubles the list. It is shared across the assembly and **no test may add to or remove from the collections the theories assert on**. The one test that genuinely creates a row (`A_tenant_id_in_the_request_body_is_never_honoured`) creates its own throwaway tenants. Break that rule and the exact-count assertions start depending on the order xUnit happens to run classes in.

### Falsification results

All three deliberate breaks were run and reverted; `src/` is untouched by them.

| Break | Result |
|---|---|
| Mapped a dummy `GET /api/v1/falsification-probe` | `EndpointCoverageTests` failed, naming the route |
| Pointed a collection's expected ids at tenant A | `CollectionIsolationTests` failed on the id sets |
| Pointed a by-id victim at tenant B's own row | 404 became **204** — so the 404 is the tenant filter, not a routing miss |

The third is the one worth keeping in mind: it is the only evidence that the by-id tests reach a live endpoint at all.

## One finding, recorded not fixed

**A validly signed token is trusted for whatever tenant it names.** Nothing re-checks per request that the token's `sub` belongs to its `tenant_id`, so a token minted with another tenant's id and `role: Owner` reads that tenant's data on any path that does not itself load the user — `GET /registers` needs only the role claim. `/auth/me` happens to catch it because it looks the subject up.

Not reachable from outside: minting one needs the signing key, and a key holder can mint anything. But it means **the signing key is the tenancy boundary**, and the three layers all sit below it. Pinned by `ForgedTenancyTests.A_validly_signed_token_is_trusted_for_whatever_tenant_it_names`, which goes red if anyone adds the check — deliberately, so it is a decision rather than a surprise. Full reasoning and the cost of closing it: [`DECISIONS.md`](../DECISIONS.md#resolved-2026-07-31-during-phase-1).

## Things that will bite you

New this session (1–3); the rest carried forward and still true.

1. **Health-check endpoints have no HTTP method metadata**, so they appear in the routing table as `ANY health/live`, not `GET`. Anything enumerating `EndpointDataSource` has to handle it.
2. **A test that asserts 404 passes when it reaches nothing.** See the by-id note above. Any new "not found" assertion needs some independent evidence the route was real.
3. **A shared `WebApplicationFactory` has no reset between tests.** There is no `ResetAsync` on `PosApiFactory` (unlike `PostgresFixture`), so every test either creates its own tenant with a unique slug or treats the shared world as read-only.
4. **An applied migration does not re-run.** The RLS catalog loop covers the tables that existed when it ran — it does **not** reach a table added in Phase 2. Any migration introducing a tenant-owned table must call `migrationBuilder.ApplyTenantRowLevelSecurity()`. `RowLevelSecurityTests.Every_tenant_owned_table_is_covered` fails the build if it does not; that test is the mechanism, not a nicety.
5. **A superuser bypasses RLS unconditionally, `FORCE` or not.** The test container's owner account is a superuser, so a suite run as the owner passes with no policies in existence at all. Both fixtures connect the *application* as `pos_app`. Guarded by `The_application_role_does_not_bypass_row_level_security` and, for the API host specifically, `HostScopeIsolationTests.The_hosted_application_connects_as_a_role_that_cannot_bypass_row_level_security`.
6. **A reset Postgres session variable reads as `''`, not NULL** — and `''::uuid` raises. The policies use `nullif(current_setting('app.tenant_id', true), '')::uuid` so both unset and reset collapse to NULL, i.e. no rows. Fails closed.
7. **`dotnet run` uses `launchSettings.json` and ignores `ASPNETCORE_URLS`** set in the environment. It listens on 5013. Use `--urls` or read the port out of the startup log.
8. **An authorization policy that does not name its scheme evaluates the default one.** `EnrolledDevice` must say `.AddAuthenticationSchemes(DeviceTokenAuthenticationHandler.SchemeName)`, or a cashier's ordinary JWT satisfies "enrolled device".
9. **A request can carry both a JWT and a device token.** `AmbientTenantContext.Resolve` refuses to switch tenants, so tenant A's bearer plus tenant B's device token threw a 500 until it was guarded. Anything else resolving a tenant pre-authentication needs the same check.
10. **`WebApplicationFactory.ConfigureAppConfiguration` is applied at `Build()`**, but `Program.cs` reads `builder.Configuration` before it. **Use `builder.UseSetting(...)`.**
11. **Never run the test host in `Development`.** It loads your user secrets, whose connection string points at local Docker. `PosApiFactory` uses `"Testing"`.
12. **`problem+json` carries a per-request `traceId`.** Compare `type`/`title`/`status`/`detail`, never raw strings.
13. **EF names tables from the `DbSet` property name**, not the entity type.
14. **`[CallerFilePath]` is a lie under CI.** Anchor on `AppContext.BaseDirectory`. `$env:CI="true"` reproduces CI-only build behaviour locally.
15. **`dotnet ef database update` needs the owner connection string passed explicitly** — `pos_app` has no `CREATE` on the schema, by design. Full command in [`CLAUDE.md`](../CLAUDE.md#commands).
16. **`pnpm/action-setup` ignores `defaults.run.working-directory`.**
17. **`docker compose ps` says "Running" for a crash-looping container.** Verify with `docker inspect -f '{{.State.Status}} restarts={{.RestartCount}}'`.
18. **Kill stray `Pos.Api` processes before rebuilding** — they lock `Pos.Data.dll` (`MSB3027`). Use `Stop-Process`, not `pkill -f`.
19. **Adding shadcn components may reintroduce the two-React-copies error.** Fix is `test.server.deps.inline` in `vite.config.ts`.
20. **Test fixtures must not contain the strings a test proves absent.** The `pin-eligible` leak test asserts no role name appears, and the fixture was called "Cal Cashier".

## Working preferences noted

- **Don't run `dotnet build` and `dotnet test` back to back as a habit** — `dotnet test` builds.
- **UI testing must not accumulate.** In Phases 4–7 a milestone is not done until its Vitest/Playwright coverage exists *and* someone has looked at the screen. jsdom does not paint.
- **Prove a security test can fail.** Three times this session a green test meant nothing until it was falsified deliberately, and one of the three found a real weakness in the test.

## Outstanding / deferred

- **This branch has never been pushed and CI has never run the RLS or isolation suites.** They should just work — the fixtures create `pos_app` themselves — but that is unverified, and it is the last thing standing between Phase 1 and "done" in the sense CLAUDE.md means.
- **Manual two-tenant check through Swagger** not done. The phase doc asks for it; the automated suite covers the same ground, so this is confirmation rather than coverage.
- **`dotnet dev-certs https --trust`** still not run. Opens a Windows dialog that cannot be scripted.
- **Playwright browsers not installed** — `pnpm exec playwright install --with-deps` in Phase 4, a ~500 MB download nothing needs before then.
- **No visual verification of the web UI.** Phase 1 is backend only. Eyeball it before Phase 4 builds on top.
- **Production `pos_app` password.** `docker-compose.yml` defaults `POSTGRES_APP_PASSWORD` to the same throwaway as everything else in it. Phase 8.2 must supply a real one — the compose file already reads it from the environment, so this is configuration, not a code change.
- **FK constraint names in the Identity migration read `fk_role_claim_asp_net_roles_role_id`** — cosmetic leftover from Identity's default table names. Harmless.
- **CI actions emit a Node 20 deprecation warning.** Bump to `@v5` when available.
- **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc. Remove the pin when ASP.NET Core ships a patched dependency.
- **Smart App Control is off** (irreversible, done 2026-07-31). Local `dotnet test` works.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price: "lifetime hosting for a one-time fee" is a liability that grows with every tenant.
