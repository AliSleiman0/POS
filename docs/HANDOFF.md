# Session Handoff

**Written:** 2026-07-31 · **Branch:** `phase-1/tenancy-auth` (4 commits ahead of `main`, not pushed) · **Phase 1: 1.1–1.4 done, 1.5 part-written**

> This file is session state, not durable truth. It goes stale — overwrite it at the end of each session. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`DECISIONS.md`](../DECISIONS.md) → **"Resolved 2026-07-31 (during Phase 1)"** — four decisions taken this session. Two of them contradict what the phase doc says. Read them before writing any 1.5/1.6 code.
2. [`docs/phases/PHASE-1-tenancy-auth.md`](phases/PHASE-1-tenancy-auth.md) — the phase, **with two corrections noted below**.
3. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants.

## ⚠️ The working tree does not compile

This is the first thing you will hit. It is one missing type, not a mess:

```
src/Pos.Api/Endpoints/AuthEndpoints.cs(53): error CS0103:
    The name 'RateLimitPolicies' does not exist in the current context
```

1.5 was interrupted mid-write. Six new files are **untracked** and two are modified; nothing is lost, nothing is committed:

```
?? src/Pos.Core/Entities/Register.cs                  written, believed complete
?? src/Pos.Core/Security/Pin.cs                       written, believed complete
?? src/Pos.Data/Configurations/RegisterConfiguration.cs   written, believed complete
?? src/Pos.Api/Auth/DeviceTokenAuthentication.cs      written, believed complete
?? src/Pos.Api/Endpoints/RegisterEndpoints.cs         written, believed complete
?? src/Pos.Api/Endpoints/EmployeeEndpoints.cs         written, believed complete
 M src/Pos.Api/Endpoints/AuthEndpoints.cs             /auth/pin added
 M src/Pos.Data/AppDbContext.cs                       Registers DbSet added
```

**None of it has ever been compiled or run.** Treat it as a draft that reads plausibly, not as working code.

## Finishing 1.5 — what is actually missing

In rough order:

1. **`RateLimitPolicies`** — the one compile error. A static class holding `public const string PinAttempts = "pin-attempts"`, plus the registration: `AddRateLimiter` with a partitioned fixed window keyed on the `X-Device-Token` header (~10 attempts/minute). Caps total guessing from one till, which per-user lockout alone does not — an attacker can otherwise walk the staff list, 5 attempts each.
2. **`Program.cs` wiring**, none of which exists yet:
   - `.AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(DeviceTokenAuthenticationHandler.SchemeName, null)` on the existing `AddAuthentication` chain
   - an `EnrolledDevice` policy that **names the scheme explicitly**: `.AddAuthenticationSchemes(DeviceTokenAuthenticationHandler.SchemeName).RequireAuthenticatedUser()`. Register it *outside* `PolicyCatalog` — that catalog is role-to-policy and a test asserts its keys match the table in `ARCHITECTURE.md`, so adding a non-role policy there breaks it.
   - `app.UseRateLimiter()`, `app.MapRegisterEndpoints()`, `app.MapEmployeeEndpoints()`
3. **Migration** for the `register` table: `dotnet ef migrations add Registers --project src/Pos.Data --startup-project src/Pos.Api`. Read it before committing.
4. **Tests** (`tests/Pos.Api.Tests/`), per the 1.5 exit criteria: enrollment returns the token once and it is unretrievable; PIN login works from an enrolled till; **rejected from an unenrolled and from a revoked till**; lockout engages and reports its expiry; `pin-eligible` contains no role, email or contact field — assert on the serialised JSON, not the DTO, or the test passes while the field ships.

`PosApiFactory` will need a helper to create + enroll a register and set a PIN; everything it needs is already there.

## Two corrections to the phase doc — it is wrong on both

The phase doc has not been amended yet. **Amend it as you go.**

- **§1.3 says `ApplicationUser` is deliberately outside the query filter** ("the one deliberate exception… documented here so nobody fixes it"). That is no longer true and the reasoning behind it no longer applies: login names the tenant by slug first, so the user table is scoped like everything else and there are **zero** exempt tables. See `DECISIONS.md`.
- **§1.6 says `SET LOCAL` per transaction.** This is wrong in a way that would have shipped silently — outside an explicit transaction it applies to the implicit single-statement transaction, and EF reads do not open transactions. RLS would have looked configured and enforced nothing. Use session-level `set_config('app.tenant_id', $1, false)` from a `DbConnectionInterceptor`, parameterised. Safe with pooling because Npgsql sends `DISCARD ALL` on reset — so the connection string must **not** set `No Reset On Close=true`, and multiplexing must stay off. **Write the test that proves a pooled connection does not inherit the previous request's tenant**; that is the one that would catch a regression here.

## Then 1.6 and 1.7

Both are still entirely unwritten. The plan for them is in `C:\Users\Admin\.claude\plans\plan-full-clean-implementation-velvet-hejlsberg.md`.

**1.6 has a step that touches your machine:** it needs a `pos_app` role that does not bypass RLS. Postgres init scripts only run against an empty data directory, so this means `docker compose down -v` — destroying the local dev database — and then one `dotnet user-secrets set` to point the connection string at `pos_app`. The database currently holds nothing but migrations, so now is the cheapest this will ever be.

Two things in 1.6 are worth more than the rest:
- The RLS migration should be a `DO` block **looping every table with a `tenant_id` column**, not a hand-written list. Later phases then inherit it.
- Pair it with a test asserting every such table has RLS enabled, forced, and a policy. That is what makes Phase 2 fail loudly instead of leaking.

**Do not start Phase 2 until 1.7 is green.**

## Where 1.1–1.4 got to

Four commits, each with its tests passing at the time: `6048475` `4b62d4f` `c843e2e` `5b0ba36`.

**71 tests green as of 1.4** — 23 Core, 23 Data, 25 Api. Data and Api tests run against real Postgres via Testcontainers (one container per assembly, ~4s for the Api suite after startup).

Things worth knowing about what is already built:

- The query filter keys on an **`ITenantOwned` interface**, not the `TenantEntity` base class. `ApplicationUser` must inherit `IdentityUser<Guid>` and C# has single inheritance, so a base-class-only rule would have left the user table outside the mechanism.
- The filter is proved by a `ThrowawayEntity` that exists only in the test assembly with no configuration, no migration and no `HasQueryFilter` anywhere. Testing entities we wrote filters for only proves we wrote them.
- The write interceptor **rejects** an insert carrying another tenant's id rather than overwriting it. Silently correcting turns an attempted cross-tenant write into a successful in-tenant one with nothing recording that it was tried.
- Identity's satellite tables were subclassed to carry `TenantId`. `user_token` is where password-reset tokens live; reading another tenant's row there is account takeover, not an information leak.
- .NET 10 Identity adds a **passkey table**, which is `Ignore`d outright. It would otherwise be the only table with no tenant column. If you ever need WebAuthn, it comes back as a tenant-owned subclass — and note that subclassing `IdentityUserPasskey<Guid>` fails with *"entity type 'IdentityPasskeyData' requires a primary key"* unless you also configure its `Data` property.

## Things that will bite you

New this session (1–5); the rest carried forward.

1. **`WebApplicationFactory.ConfigureAppConfiguration` is applied at `Build()`** — but `Program.cs` reads `builder.Configuration` *before* `Build()`, to fail fast on a missing connection string and signing key. Values added that way arrive too late and the app starts with neither. **Use `builder.UseSetting(...)`.** Cost me the entire API suite failing with 500s.
2. **Never run the test host in the `Development` environment.** Development loads *your* user secrets, whose connection string points at the local Docker database — the suite passes while proving nothing, against real data. `PosApiFactory` uses `"Testing"`.
3. **`problem+json` carries a per-request `traceId`.** Comparing two error responses as raw strings can never pass; compare `type`/`title`/`status`/`detail`.
4. **EF names tables from the `DbSet` property name**, not the entity type — `DbSet<ThrowawayEntity> Throwaways` gives you `throwaways`, not `throwaway_entity`.
5. **`AddDefaultTokenProviders()` is not available in `Pos.Data`** — it lives in the ASP.NET-level Identity package, and Data only references `Identity.EntityFrameworkCore`. Not needed until password reset exists.
6. **`[CallerFilePath]` is a lie under CI.** `ContinuousIntegrationBuild` bakes in `/_/tests/...`, which exists nowhere. Anchor on `AppContext.BaseDirectory`. **`$env:CI="true"` reproduces CI-only build behaviour locally** — no push needed.
7. **`pnpm/action-setup` ignores `defaults.run.working-directory`.** Any action input needing a path must name it explicitly.
8. **`docker compose ps` says "Running" for a crash-looping container.** Verify with `docker inspect -f '{{.State.Status}} restarts={{.RestartCount}}'`.
9. **Kill stray `Pos.Api` processes before rebuilding** — they lock `Pos.Data.dll` (`MSB3027`). `pkill -f` misses them; use `Stop-Process`.
10. **User secrets load only in Development.** `dotnet run --no-launch-profile` defaults to Production and the connection string is not found.
11. **Adding shadcn components may reintroduce the two-React-copies error.** Fix is `test.server.deps.inline` in `vite.config.ts`.

## Working preferences noted this session

- **Don't run `dotnet build` and `dotnet test` back to back as a habit** — `dotnet test` builds. A separate build is only worth it while iterating on compile errors, where waiting for Testcontainers to start before seeing a syntax error is the slower loop.
- **UI testing must not accumulate.** The standing rule (now in `ROADMAP.md` and `CLAUDE.md`) is specifically about the frontend: in Phases 4–7 a milestone is not done until its Vitest/Playwright coverage exists *and* someone has looked at the screen. jsdom does not paint.

## Before running the API by hand

`Jwt:SigningKey` is validated at startup and the app **will not boot without it**. It is not in user-secrets yet:

```powershell
dotnet user-secrets set "Jwt:SigningKey" "<32+ bytes>" --project src/Pos.Api
dotnet user-secrets set "Jwt:Issuer"  "https://localhost:7080" --project src/Pos.Api
dotnet user-secrets set "Jwt:Audience" "https://localhost:7080" --project src/Pos.Api
```

## Outstanding / deferred

- **`dotnet dev-certs https --trust`** still not run. Opens a Windows dialog that cannot be scripted.
- **Playwright browsers not installed** — `pnpm exec playwright install --with-deps` in Phase 4, a ~500 MB download nothing needs before then.
- **No visual verification of the web UI.** Unchanged this session; Phase 1 is backend only. Eyeball it before Phase 4 builds on top.
- **`docs/API.md` still documents `POST /auth/login` without `tenantSlug`.** Update it — the frontend will be generated against this. Same for the `X-Device-Token` format.
- **`ARCHITECTURE.md` still describes the old login flow.** A test already pins its authorization table to the code; the auth-flow prose is not covered by anything.
- **FK constraint names in the Identity migration read `fk_role_claim_asp_net_roles_role_id`** — a cosmetic leftover from Identity's default table names. Harmless.
- **CI actions emit a Node 20 deprecation warning.** Warnings only; bump to `@v5` when available.
- **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc. Remove the pin when ASP.NET Core ships a patched dependency.
- **Smart App Control is off** (irreversible, done 2026-07-31). Local `dotnet test` works.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price: "lifetime hosting for a one-time fee" is a liability that grows with every tenant.
