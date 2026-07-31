# CLAUDE.md — Working conventions for this repo

## Read these first

| File | For |
|---|---|
| [`docs/HANDOFF.md`](docs/HANDOFF.md) | **Start here.** State from the last session: what's done, what's next, what will bite you. Session state, not durable truth — overwrite it when you finish. |
| [`DECISIONS.md`](DECISIONS.md) | **Why** the architecture is what it is. Locked decisions — challenge them explicitly, don't drift from them silently. |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | **Where we are** and what's next. Update the checkboxes as work lands. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Layering, multi-tenancy mechanism, auth flow, API conventions. |
| [`docs/DATA-MODEL.md`](docs/DATA-MODEL.md) | Entities, money rules, indexes, invariants. |
| [`docs/API.md`](docs/API.md) | Endpoint contracts and semantics. |
| `docs/phases/PHASE-N-*.md` | The phase you're working on: tasks, exit criteria, verification. |

**Start of a session:** read `docs/HANDOFF.md`, then `docs/ROADMAP.md` for current state, then the relevant phase doc. Don't re-derive the plan.

**End of a session:** overwrite `docs/HANDOFF.md` with the new state.

## Project shape

Vendor-hosted multi-tenant POS. ASP.NET Core API + React PWA now; Avalonia desktop later against the same API. Retail first, restaurant later, cash-only checkout in the MVP.

```
src/Pos.Core   — domain logic. Pure C#, no infrastructure.
src/Pos.Data   — EF Core: DbContext, configs, interceptors, migrations.
src/Pos.Api    — ASP.NET Core Web API.
src/Pos.Web    — Vite + React + TS PWA.
tests/         — Pos.Core.Tests, Pos.Data.Tests, Pos.Api.Tests
```

## Commands

```powershell
docker compose up -d                                   # Postgres + pgAdmin
dotnet build                                           # warnings are errors
dotnet test                                            # all .NET tests
dotnet run --project src/Pos.Api                       # API + /swagger
dotnet ef migrations add <Name> --project src/Pos.Data --startup-project src/Pos.Api
dotnet ef database update  --project src/Pos.Data --startup-project src/Pos.Api

pnpm --dir src/Pos.Web dev
pnpm --dir src/Pos.Web build
pnpm --dir src/Pos.Web test          # Vitest
pnpm --dir src/Pos.Web test:e2e      # Playwright
pnpm --dir src/Pos.Web generate:api  # regenerate the typed client from OpenAPI
```

Bash is available too, but PowerShell is the primary shell on this machine.

---

## Invariants — do not break these without a design discussion

### 1. `Pos.Core` stays pure

No EF Core, no ASP.NET, no HTTP, no file or clock access. Time enters through `TimeProvider`; persistence through interfaces Core declares and `Data` implements. An architecture test enforces this. If it fails, the fix is to move the code, not to relax the test.

### 2. Never bypass tenant scoping

`IgnoreQueryFilters()` needs an explicit justification in review. Raw SQL is covered by RLS, but prefer LINQ so both layers apply. `TenantId` comes from the validated token only — never from a route, query string or request body.

New entity that belongs to a tenant → derive from `TenantEntity`. That is the whole registration; the filter is applied by reflection.

### 3. Money is `decimal`, via the `Money` type

`numeric(19,4)` in Postgres. `MidpointRounding.AwayFromZero`. Round **once**, when producing an amount a person pays — not per line. Never `float`/`double`. Raw `decimal` arithmetic on prices outside `Money` is a review failure.

On the frontend: **money is never a JS `number` in a calculation.** The server computes every total; the client displays it. Where a provisional subtotal is needed, use integer minor units.

### 4. Financial records are append-only

A `Completed` sale is never updated or deleted. Voids are a status flag plus compensating stock movements. Refunds are new linked `Sale` rows. Same for `StockMovement` and `AuditEntry`.

### 5. Historical amounts are snapshots

`SaleLine` stores description, unit price, tax rate and discount as of sale time. **Never join a report to the current `Product.Price`** — that retroactively rewrites past revenue and the reports silently stop matching the cash taken.

### 6. Money- and stock-moving writes are idempotent

Client-generated GUID, unique index enforcing it, replay returns the original response. Applies to sales, voids, refunds, stock adjustments, shift open/close.

### 7. Authorization is by named policy

`RequireAuthorization("CanRefund")`, never `[Authorize(Roles = "Manager")]`. Policies are listed in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#authorization). Every endpoint has one — a test fails the build on any endpoint with no authorization metadata.

Frontend gating is convenience. The server always re-checks. Fields a role may not see are **omitted from the response**, not hidden in the UI.

### 8. UTC everywhere, business day at the edge

`timestamptz`, UTC in storage and transit. A tenant's trading-day boundary (timezone + day-start offset) is applied at query/presentation time so a 02:00 shift close lands on the right day.

### 9. Tests ship with the behaviour

Not a later cleanup pass. Every new endpoint gets a tenant-isolation test and a negative authorization test. Concurrency tests must actually run concurrently.

### 10. No blocking browser dialogs in the register

No `alert()`, `confirm()` or `prompt()`. They block the page and stall a queue. Use in-page confirmations.

---

## Style

**C#**
- File-scoped namespaces, primary constructors where they read well, `sealed` by default
- Nullable reference types on; no `!` without a comment explaining why it's safe
- One `IEntityTypeConfiguration<T>` per entity in `Pos.Data/Configurations/`, applied by assembly scan
- Minimal APIs grouped per resource in `Pos.Api/Endpoints/`
- Async all the way down; no `.Result`, no `.Wait()`
- Throw domain exceptions from Core; map them to `problem+json` in one place in the API

**TypeScript**
- `strict: true`. No `any` — `unknown` plus narrowing.
- Server state in TanStack Query; local state in React. No global store for server-owned data.
- **Never hand-write API types** — run `pnpm generate:api`.
- Feature-first folders (`features/register/`), shared primitives in `components/`

**SQL / migrations**
- Review every generated migration before committing; EF sometimes generates a destructive change for an innocuous model edit
- Destructive migrations need deliberate sign-off
- Migrations never run at API startup — they are a gated deploy step

## Git

- Branch per phase or milestone: `phase-3/pricing-engine`
- Commit messages state what changed and why; reference the milestone (`Phase 3.2: pricing engine with inclusive/exclusive tax modes`)
- Don't commit unless asked. Never commit secrets, connection strings or signing keys.
- Co-author trailer on commits made by Claude Code

## When a milestone is done

1. Exit criteria in the phase doc all met
2. `dotnet build` and `pnpm build` warning-free
3. All tests pass
4. Tick the checkboxes in `docs/ROADMAP.md` **and** the phase doc
5. If a decision changed along the way, update `DECISIONS.md` — the next session reads it as truth

## Things that have already been decided — don't relitigate silently

- Postgres, not SQL Server
- Shared DB + `TenantId`, not database- or schema-per-tenant
- Cash-only checkout for the MVP; card is Phase 11
- ASP.NET Core Identity + JWT, not an external IdP
- Online-only first; offline is Phase 9
- No platform admin UI until after the first paying client
- Retail schema first; restaurant mode is a separate model, not a bolt-on to `Sale`

Rationale for each is in `DECISIONS.md`. If evidence emerges that one is wrong, say so explicitly and update the file — but don't quietly build as if it were decided differently.

## Still open

- Pricing/business model: one-time purchase vs. recurring, given that we host. No billing code exists yet, so this blocks nothing until Phase 10 — but it needs deciding before pricing is quoted to a customer.
- Hosting provider (decided in Phase 8.2).
- Payment processor (decided in Phase 11).
