# POS — Architecture

Why these choices were made lives in [`../DECISIONS.md`](../DECISIONS.md). This file describes *how the system is put together* and which rules must not be broken.

## Solution layout

```
Pos.sln
├─ src/
│  ├─ Pos.Core        — domain: entities, value objects, pricing/tax/tender rules, ports (interfaces)
│  ├─ Pos.Data        — EF Core: DbContext, configurations, interceptors, migrations, repositories
│  ├─ Pos.Api         — ASP.NET Core Web API: endpoints, auth, DTOs, composition root
│  └─ Pos.Web         — Vite + React + TS PWA
└─ tests/
   ├─ Pos.Core.Tests  — xUnit, pure and fast, no DB
   ├─ Pos.Data.Tests  — EF behaviour: filters, interceptors, migrations (Testcontainers Postgres)
   └─ Pos.Api.Tests   — WebApplicationFactory end-to-end through HTTP (Testcontainers Postgres)
```

`Pos.Desktop` (Avalonia) joins later and consumes the *same* `Pos.Api`. That is only real if `Core` and `Data` stay client-agnostic — hence the dependency rule below.

## Dependency rule

```
Pos.Api ──> Pos.Data ──> Pos.Core
   └────────────────────────┘
```

- **`Pos.Core` depends on nothing outside the BCL.** No EF Core, no ASP.NET, no HTTP, no file or clock access. Time comes in through `TimeProvider`; persistence through interfaces Core declares and `Data` implements.
- **Arrows never reverse.** `Core` must not know that EF or a web request exists.
- This is enforced by a test in `Pos.Core.Tests` that reflects over assembly references and fails the build if `Core` gains a forbidden dependency. It is a guardrail, not a convention — conventions decay.

**Why bother:** the pricing, tax, rounding, tender and stock rules are the part that must be correct and the part two different clients share. Keeping them free of infrastructure is what makes them exhaustively unit-testable without a database, and what makes the desktop app a client rather than a fork.

## Multi-tenancy

Shared database, every tenant-owned row carries `TenantId`. Isolation is enforced at **three** layers, deliberately redundant.

### Layer 1 — ambient tenant context

```
Request → JWT validated → `tenant_id` claim read → ITenantContext populated (scoped DI)
```

`ITenantContext` exposes the current `TenantId`. It is populated once per request from the **validated token only**. A `TenantId` appearing in a route, query string or request body is never trusted; if a body contains one that disagrees with the token, the request is rejected.

Background jobs and the tenant-onboarding path run with an explicit, opt-in "no tenant / system" context rather than a null one, so an unset tenant can never be mistaken for "all tenants".

### Layer 2 — EF Core global query filters + write interceptor

`AppDbContext.OnModelCreating` iterates the model and applies a query filter to **every** entity deriving from `TenantEntity`, by reflection:

```csharp
// conceptual
foreach (var entity in modelBuilder.Model.GetEntityTypes()
             .Where(e => typeof(TenantEntity).IsAssignableFrom(e.ClrType)))
{
    // e => e.TenantId == _tenantContext.TenantId
}
```

Applied by reflection, not hand-written per entity, so **a newly added entity cannot be forgotten**. That omission is the classic multi-tenant data leak.

A `SaveChangesInterceptor`:
- stamps `TenantId` on every `Added` `TenantEntity`,
- **throws** if a `Modified` or `Deleted` entity's `TenantId` differs from the ambient tenant,
- stamps `CreatedAt`/`CreatedBy`/`UpdatedAt`/`UpdatedBy` audit columns.

### Layer 3 — Postgres row-level security

Global query filters **do not apply** to `FromSqlRaw`, `ExecuteSqlRaw`, Dapper, or anything hand-written. That gap is covered in the database: every table carrying a `tenant_id` column has RLS `ENABLE`d, `FORCE`d, and a `tenant_isolation` policy comparing `tenant_id` to `app.tenant_id`.

`TenantConnectionInterceptor` publishes that setting on every connection open:

```sql
SELECT set_config('app.tenant_id', $1, false)
```

**Session-level, not `SET LOCAL`.** `SET LOCAL` is scoped to the enclosing transaction, and outside an explicit one that is the implicit single-statement transaction — which ends immediately. EF reads open no transaction, so the setting would be gone before the query that needed it. RLS would have looked configured and enforced nothing.

Session scoping is safe only because Npgsql sends `DISCARD ALL` when a pooled connection is returned. Two things must therefore stay as they are: **`No Reset On Close` must not be enabled, and multiplexing must stay off.** `RowLevelSecurityTests.A_pooled_connection_does_not_inherit_the_previous_scopes_tenant` pins both, using `Max Pool Size=1` so connection reuse is guaranteed rather than likely.

The value is passed as a **parameter**. `set_config` exists precisely so a session variable can be set without string concatenation; `SET` cannot take a parameter, which is how this becomes an injection point.

The policy reads `nullif(current_setting('app.tenant_id', true), '')::uuid` — an unset GUC reads as NULL, a *reset* one as the empty string, and `''::uuid` raises rather than yielding NULL. Both collapse to NULL so that "no tenant" means "no rows". It carries `WITH CHECK` as well as `USING`, so a raw INSERT cannot write into a tenant it could never read back.

**Two roles.** `pos` owns the schema and runs migrations. `pos_app` is `NOBYPASSRLS`, has no `CREATE` on the schema, and is what the API connects as — a role that can create a table can create one with no policy on it. `FORCE` is what stops a non-superuser owner bypassing its own policy; `NOBYPASSRLS` is what stops a superuser doing it.

Role creation needs a password, so it is a bootstrap step (`docker/postgres-init/01-app-role.sh`) rather than a migration — no secrets in committed SQL. The migration does the grants and `ALTER DEFAULT PRIVILEGES`, so tables added later are reachable without anyone remembering.

**One thing that does not take care of itself:** an applied migration does not re-run, so the catalog loop does not reach a table added in a later phase. Any migration introducing a tenant-owned table must call `migrationBuilder.ApplyTenantRowLevelSecurity()`. `RowLevelSecurityTests.Every_tenant_owned_table_is_covered` fails the build when it does not.

### Testing the isolation

`Pos.Api.Tests/Isolation/` seeds two tenants with deliberately similar data and asserts that, authenticated as tenant B:

- every collection endpoint returns none of tenant A's rows,
- fetching tenant A's row by its known id returns **404, not 403** (a 403 confirms the row exists — an information leak),
- a raw-SQL query path returns nothing,
- posting a body containing tenant A's `TenantId` does not put the row in tenant A.

The two tenants hold **identical** data — same emails, same display names, same till names — so a leak
doubles a list rather than being something to spot by comparing ids.

**Adding an endpoint means adding a row to `Isolation/IsolationManifest.cs`.** That manifest lists
every endpoint and how its tenancy is proven, and `EndpointCoverageTests` diffs it against the router's
own endpoint table in both directions — so mapping a route without deciding how it is isolation-tested
fails the build with the route named, and a manifest row for a route that no longer exists fails too.
An endpoint that genuinely needs no isolation test is marked `Exempt` **with the test where its
coverage does live**. The same manifest drives the negative-authorization theory, so both halves of
CLAUDE.md invariant 9 arrive together.

These tests are not optional and are re-run against production in Phase 8.6.

## Authentication & authorization

### Two-tier login

Retail reality: a register is a shared device on a counter, and a cashier cannot type an email and password between customers.

1. **Credential login** (`POST /auth/login`) — **tenant slug** + email + password, for Owner/Manager and for enrolling a device. Returns an access token + refresh token.
2. **Device enrollment** — a register is enrolled once by an Owner and receives a device token, shown exactly once and stored only as a SHA-256 digest.
3. **PIN login** (`POST /auth/pin`) — from an enrolled device only, a cashier presents a 4–6 digit PIN to start a session. PINs are hashed with the password hasher (never compared in plaintext), rate-limited per device, and locked out per user after repeated failures.

**Login names the tenant, and that is load-bearing.** The server resolves the `tenant` row by slug — that table is the tenant list, so it carries no `TenantId`, no query filter and no RLS — and only then looks for a user. The alternative, finding the user first and reading the tenant off their row, would force `application_user`, `refresh_token` and `register` permanently outside both the query filter and RLS: the three tables an attacker would most like to read across tenants. Naming the tenant first is what makes "no table is an exception" true, and therefore checkable by a test.

A slug is a pre-authentication **selector**, not a `TenantId` — it narrows the search, and the credentials still have to match a user inside that tenant. After login, `TenantId` comes from the validated token and nowhere else, so this is not a breach of invariant 2. Login returns one identical response, with comparable timing, for unknown slug, unknown email and wrong password.

**The signing key is the tenancy boundary.** The three layers above all scope a request to the tenant its token *claims*; none of them second-guesses the claim, and nothing re-checks per request that the token's `sub` belongs to its `tenant_id`. That is accepted rather than overlooked — minting such a token needs the key, and a key holder can mint anything — but it is the reason key handling is not merely a deployment detail. The reasoning, and what closing it would cost, is in [`DECISIONS.md`](../DECISIONS.md#resolved-2026-07-31-during-phase-1).

**A PIN is never sufficient authentication on its own.** It authenticates a person *to an already-trusted device*, which is why the device token exists. A 4-digit secret is otherwise trivially brute-forced.

#### The `DeviceToken` scheme and the `EnrolledDevice` policy

`X-Device-Token` is validated by a real authentication scheme (`DeviceTokenAuthenticationHandler`), not by a check inside the endpoint — **authentication runs before the endpoint does**, so a PIN request from an unenrolled till is rejected before the PIN is read. It cannot be used as a PIN oracle, and it cannot be used to lock every cashier out by burning their attempt budgets.

The `EnrolledDevice` policy **names the scheme explicitly** (`.AddAuthenticationSchemes(...)`). Left to the default, a cashier's ordinary access token would satisfy "enrolled device" and the second factor would disappear with nothing going red. It is registered outside `PolicyCatalog`, which maps policies to *roles* and is pinned to the table below by a test; a device has no role.

Two independent caps, and neither replaces the other:

| | Counts | Stops |
|---|---|---|
| Identity lockout | failures per **user** | one person's PIN being guessed from anywhere |
| `pin-attempts` rate limit | attempts per **device token** | one till walking the staff list, 5 attempts per name — the list being served by `GET /employees/pin-eligible` |

#### Opaque tokens

Refresh tokens and device tokens share one format: `base64url(tenantId) "." base64url(32 random bytes)`. Both are presented when no tenant is known yet, and without the prefix, looking one up would mean querying across every tenant — the exact unscoped read this design exists to prevent.

The prefix is **not** a credential. It selects which tenant to search; the 256-bit half authenticates, matched by digest inside that tenant. A forged prefix lands the caller in a tenant where their hash matches nothing.

Note the deliberate asymmetry: PINs and passwords use the slow salted password hasher because a human chose them; device and refresh tokens use a plain SHA-256 because they are 256 random bits and a *deterministic* digest is required to find the row at all.

### Tokens

- **Access token**: JWT, short-lived (~15 min), claims `sub`, `tenant_id`, `role`, `register_id` (when applicable). Signed with a key from configuration/vault — never a checked-in literal.
- **Refresh token**: opaque, long-lived, stored hashed server-side, **rotated on every use**. Reuse of an already-rotated token invalidates the whole family (a replay means it leaked).

### Authorization

Roles `Cashier` / `Manager` / `Owner` exist, but endpoints do **not** test role literals. They require named policies:

| Policy | Cashier | Manager | Owner |
|---|---|---|---|
| `CanSell` | ✅ | ✅ | ✅ |
| `CanApplyDiscount` | ❌ | ✅ | ✅ |
| `CanOverridePrice` | ❌ | ✅ | ✅ |
| `CanVoidSale` | ❌ | ✅ | ✅ |
| `CanRefund` | ❌ | ✅ | ✅ |
| `CanManageCatalog` | ❌ | ✅ | ✅ |
| `CanViewMargins` | ❌ | ❌ | ✅ |
| `CanManageEmployees` | ❌ | ❌ | ✅ |
| `CanCloseShift` | ❌ | ✅ | ✅ |
| `CanTakeOrders` | ✅ | ✅ | ✅ |
| `CanWorkKitchen` | ✅ | ✅ | ✅ |
| `CanVoidFiredLine` | ❌ | ✅ | ✅ |
| `CanManageFloor` | ❌ | ✅ | ✅ |

The four restaurant policies (Phase 10) split along one line: **taking an order costs the shop nothing, cancelling food that is already cooking costs it a plate.** `CanTakeOrders` and `CanWorkKitchen` are everyone's, because the people doing both are the people on the floor and a till that needed a manager to seat a table would not be used. `CanVoidFiredLine` is a supervisor's for the same reason `CanVoidSale` is — it is the moment stock leaves without money arriving, and it is where theft hides. `CanManageFloor` is a supervisor's because renaming a table changes what every historical report about it says — and it covers the **stations** too, because changing what those are re-routes the whole menu. Reading them is `CanWorkKitchen`: everybody cooking has to be able to pick which screen they are standing at.

Indirection through policies means a customer asking "can my supervisors do refunds?" is a mapping change in one place, not an audit of every controller. The same policy names are exported to the frontend so UI gating and API enforcement cannot disagree — **and the frontend gate is never the only gate.**

#### Override grants — the one way a policy is satisfied other than by role

`CanApplyDiscount` and `CanOverridePrice` can also be satisfied by an **override grant**: a manager's PIN, entered at the till on the cashier's screen, exchanged for a single-use token (`POST /auth/override`) that `POST /sales` accepts as `X-Override-Authorization` and consumes inside the sale's transaction.

The cashier's session is untouched throughout, and that is the whole point. `POST /sales` takes the cashier from the token, so a flow that swapped the session would attribute the sale to the manager and make the drawer's Z-report reconcile the wrong person. Instead the sale stays the cashier's and `SaleLine.OverriddenBy` names who approved the exception.

Three properties keep this from being a hole in the policy model:

1. **Only those two policies are grantable**, enforced by an allow-list checked before the PIN. Anything else is a `400`, so this is never a route to `CanManageEmployees`.
2. **Single use**, tracked by `override_grant.ConsumedAt` in the same transaction as the sale. The five-minute expiry is hygiene, not the control.
3. **Bound to the register** it was minted at, so a grant cannot be carried to another drawer.

`POST /sales/quote` honours a grant without consuming one — see [`API.md`](API.md#sales--sales).

## API conventions

- **Versioned base path**: `/api/v1/...`.
- **Errors**: RFC 9457 `application/problem+json` for every failure, with a stable machine-readable `type`. Validation failures list per-field errors. Internal exception details never reach the client.
- **Pagination**: cursor-based (opaque cursor over a stable sort key), not `skip`/`take` — offsets skip and duplicate rows when data is being written underneath, which is normal during trading hours.
- **Idempotency**: any endpoint that moves money or stock requires a client-generated GUID. A replay returns the original result with the original status. See [DATA-MODEL.md](DATA-MODEL.md#idempotency).
- **OpenAPI**: generated by the API and used to generate the frontend's typed client. Hand-written frontend DTOs are forbidden — they drift silently.
- **Time**: all timestamps are `timestamptz` in UTC. A tenant's business-day boundary (for Z-reports) is applied at query/presentation time, so a shift closing at 02:00 lands on the correct trading day.

## Frontend architecture

```
Pos.Web/src/
├─ api/          — generated client + thin wrappers
├─ auth/         — token storage, refresh, guards, PIN flow
├─ features/
│  ├─ register/  — the checkout screen
│  ├─ catalog/
│  ├─ sales/     — history, receipts
│  ├─ shifts/
│  └─ admin/     — employees, settings
├─ components/   — shadcn/ui primitives + shared composites
└─ lib/          — money formatting, keyboard/scanner handling, offline (Phase 9)
```

- **Server state** is TanStack Query; local UI state is React state. No global store for data the server owns.
- **Money is never a JS `number` in calculations.** The server computes every total; the client displays what it is given. Where the client must show a running subtotal, it uses integer minor units. `0.1 + 0.2 !== 0.3` is not acceptable at a till.
- **The register screen must be fully keyboard-operable.** Scanners are keyboards; staff are fast; mouse-only flows fail at a real counter.
- Phase 9 adds the service worker, IndexedDB mirror and outbox. Until then the app is online-only **by design** — proving the product first, per `DECISIONS.md`.

### What survives a reload, and what must not

A counter tablet gets reloaded, slept and crashed, so the register is built to come back rather than to avoid going away.

| | Where | Why there |
|---|---|---|
| Cart, selection, cart discount, **sale GUID** | `sessionStorage`, written by `CartProvider` on every change | The basket is expensive to rebuild with a queue waiting, and the GUID riding along is what keeps a post-reload retry the *same* sale rather than a second charge |
| In-flight sale record | `sessionStorage`, written immediately **before** `POST /sales` | The only thing that can answer "was that payment taken?" afterwards. Written after the response it would exist only in the cases that do not need it |
| Manager override grant | React state in `OverrideProvider`, **nowhere else** | A five-minute credential on a shared device. It is mounted *beside* the cart rather than in it precisely so cart persistence cannot write it to disk as a side effect; a Vitest case fails if it ever reaches storage |
| Access token | Memory | Short-lived, never readable after a reload |
| Refresh token | `sessionStorage` | Trade-off written up in `DECISIONS.md`; revisited in 8.2 |
| Device token, register id | `localStorage` | A device credential — it must survive a browser restart, unlike a session |

`sessionStorage` over `localStorage` throughout, so a shared till hands the next shift nothing. **Everything read back is untrusted input**: versioned, structurally validated, and dropped rather than thrown on when it does not match — a till that crashes on load cannot be fixed by reloading, which is the only remedy a shop floor has.

**An interrupted payment is resolved by asking, never by re-submitting.** `GET /sales/by-client-transaction/{id}` on load, with three outcomes: taken (show the sale), not taken (restore the tender pad), and *cannot be determined* (say so, tell the cashier not to re-ring it, keep the record). Re-POSTing to find out is correct when the sale landed and takes the money when it did not.

## Environments & configuration

| | Local | Production |
|---|---|---|
| Postgres | Docker Compose (`docker-compose.yml`) | Managed Postgres |
| Secrets | `dotnet user-secrets` | Env vars / vault |
| Migrations | `dotnet ef database update` by hand | Explicit CI/CD step |
| Web | `pnpm dev` (Vite dev server) | Static build on CDN |

No secret, connection string or signing key is ever committed. `appsettings.json` holds non-sensitive defaults only.

**Migrations never run on API startup.** With more than one API instance, concurrent startup migrations race and can corrupt the schema. They are a deliberate, gated deploy step.
