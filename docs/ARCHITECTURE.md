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

Global query filters **do not apply** to `FromSqlRaw`, `ExecuteSqlRaw`, Dapper, or anything hand-written. That gap is covered in the database: every tenant table has an RLS policy comparing `tenant_id` to `current_setting('app.tenant_id')`, and the application sets that setting with `SET LOCAL` when a connection is checked out.

If layers 1 and 2 are bypassed by a bug, the database still returns nothing. The migration owner role bypasses RLS; the application role does not.

### Testing the isolation

`Pos.Api.Tests/Isolation/` seeds two tenants with deliberately similar data and asserts that, authenticated as tenant B:

- every collection endpoint returns none of tenant A's rows,
- fetching tenant A's row by its known id returns **404, not 403** (a 403 confirms the row exists — an information leak),
- a raw-SQL query path returns nothing,
- posting a body containing tenant A's `TenantId` is rejected.

These tests are not optional and are re-run against production in Phase 8.6.

## Authentication & authorization

### Two-tier login

Retail reality: a register is a shared device on a counter, and a cashier cannot type an email and password between customers.

1. **Credential login** (`POST /auth/login`) — email + password, for Owner/Manager and for the initial registration of a device. Returns an access token + refresh token.
2. **Device registration** — a register is enrolled once by a Manager/Owner and holds a long-lived device token.
3. **PIN login** (`POST /auth/pin`) — from an enrolled device only, a cashier presents a 4–6 digit PIN to start a session. PINs are hashed (never compared in plaintext), rate-limited, and locked out after repeated failures.

**A PIN is never sufficient authentication on its own.** It authenticates a person *to an already-trusted device*, which is why the device token exists. A 4-digit secret is otherwise trivially brute-forced.

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

Indirection through policies means a customer asking "can my supervisors do refunds?" is a mapping change in one place, not an audit of every controller. The same policy names are exported to the frontend so UI gating and API enforcement cannot disagree — **and the frontend gate is never the only gate.**

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

## Environments & configuration

| | Local | Production |
|---|---|---|
| Postgres | Docker Compose (`docker-compose.yml`) | Managed Postgres |
| Secrets | `dotnet user-secrets` | Env vars / vault |
| Migrations | `dotnet ef database update` by hand | Explicit CI/CD step |
| Web | `pnpm dev` (Vite dev server) | Static build on CDN |

No secret, connection string or signing key is ever committed. `appsettings.json` holds non-sensitive defaults only.

**Migrations never run on API startup.** With more than one API instance, concurrent startup migrations race and can corrupt the schema. They are a deliberate, gated deploy step.
