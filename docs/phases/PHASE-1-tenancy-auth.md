# Phase 1 — Multi-tenancy & Auth Spine

**Goal:** tenant scoping is automatic, unforgeable, and proven by tests; a user can log in and every endpoint knows who they are and what they may do.

**Why this phase is first and cannot be deferred:** every entity added after this inherits the isolation mechanism for free. Every entity added *before* it needs auditing individually. A cross-tenant leak in a POS means one shop reads another shop's takings — the kind of bug that ends the product.

**Depends on:** Phase 0 complete.

---

## 1.1 Tenant primitives

- `Pos.Core/Tenancy/ITenantContext.cs` — `Guid TenantId { get; }`, `bool IsSystemContext { get; }`
- `Pos.Core/Tenancy/TenantEntity.cs` — the abstract base from [DATA-MODEL.md](../DATA-MODEL.md#conventions)
- `Pos.Core/Entities/Tenant.cs` — not itself a `TenantEntity`
- `Pos.Api/Tenancy/HttpTenantContext.cs` — scoped, reads the `tenant_id` claim from the **validated** token
- `Pos.Data/Tenancy/SystemTenantContext.cs` — explicit opt-in for onboarding and migrations

Accessing `TenantId` when no tenant is resolved **throws**. It does not return `Guid.Empty`, because `TenantId == Guid.Empty` silently matches nothing (or, worse, matches a row someone inserted with an empty tenant) instead of failing loudly.

**Exit criteria**
- [x] `ITenantContext` in Core with no HTTP dependency
- [x] Unresolved tenant access throws, with a test proving it
- [x] System context is opt-in, never a fallback

## 1.2 Automatic scoping

`Pos.Data/AppDbContext.cs` — in `OnModelCreating`, iterate the model and apply the filter by reflection:

```csharp
foreach (var entityType in modelBuilder.Model.GetEntityTypes()
             .Where(t => typeof(TenantEntity).IsAssignableFrom(t.ClrType)))
{
    var param = Expression.Parameter(entityType.ClrType, "e");
    var body = Expression.Equal(
        Expression.Property(param, nameof(TenantEntity.TenantId)),
        Expression.Property(Expression.Constant(this), nameof(CurrentTenantId)));
    modelBuilder.Entity(entityType.ClrType)
        .HasQueryFilter(Expression.Lambda(body, param));
}
```

Reflection rather than one hand-written `HasQueryFilter` per entity: the hand-written version works until someone adds the eleventh entity and forgets, and nothing fails — the data just leaks.

`Pos.Data/Interceptors/TenantSaveChangesInterceptor.cs`:
- `Added` + `TenantEntity` → stamp `TenantId`, `CreatedAt`, `CreatedBy`
- `Modified`/`Deleted` + `TenantId != ambient` → **throw** `CrossTenantWriteException`
- `Modified` → stamp `UpdatedAt`, `UpdatedBy`

Also set the naming convention (`snake_case`) and a global `decimal` → `numeric(19,4)` convention here, once, so no property can be mapped with the wrong precision.

**Exit criteria**
- [x] Filter applied to every `TenantEntity` by reflection; a test adds a throwaway entity and confirms it is filtered without any per-entity registration
- [x] Interceptor stamps on insert
- [x] Cross-tenant modify throws, with a test
- [x] Money columns land as `numeric(19,4)` in the generated migration

## 1.3 Tenant-scoped Identity

`ApplicationUser : IdentityUser<Guid>` + `TenantId`, `DisplayName`, `PinHash`, `IsActive`.

The important detail — replace Identity's unique index on `NormalizedEmail`:

```csharp
builder.Entity<ApplicationUser>(b => {
    b.HasIndex(u => u.NormalizedEmail).IsUnique(false);          // drop the default
    b.HasIndex(u => new { u.TenantId, u.NormalizedEmail }).IsUnique();
});
```

Left as-is, the same person cannot hold accounts at two tenants (a franchise owner, a consultant, or you as support), and onboarding fails with an opaque duplicate-key error that looks like a bug in the onboarding code.

> **Amended 2026-07-31 during implementation.** This section originally said `ApplicationUser` sits outside the global query filter, because login had to find a user before a tenant was known. **It is not built that way, and the reasoning no longer applies.** `POST /auth/login` takes a tenant slug, resolves the tenant first, and only then looks for a user — so `ApplicationUser` is tenant-owned like everything else and **no table is an exception**. That is what makes "every tenant table is scoped" a statement a test can check rather than a claim with a footnote. Reasoning in [`DECISIONS.md`](../../DECISIONS.md#resolved-2026-07-31-during-phase-1).
>
> The scoping keys on an `ITenantOwned` interface rather than the `TenantEntity` base class: `ApplicationUser` must inherit `IdentityUser<Guid>`, and C# has single inheritance, so a base-class-only rule would have left the user table outside the mechanism after all.

**Exit criteria**
- [x] Composite unique index in the migration
- [x] A test creates the same email under two tenants successfully
- [x] A test confirms duplicate email *within* one tenant is rejected

## 1.4 JWT + RBAC

`Pos.Api/Auth/`: `TokenService`, `AuthEndpoints`, `AuthorizationPolicies`.

- Access token ~15 min, claims `sub`, `tenant_id`, `role`, `register_id?`
- Refresh token opaque, hashed at rest, **rotated every use**, grouped by `FamilyId`; reuse of a rotated token revokes the family
- Signing key from user-secrets/env — never a literal, and validated non-empty at startup so a misconfigured deploy fails fast instead of signing with a blank key
- Policies registered per the table in [ARCHITECTURE.md](../ARCHITECTURE.md#authorization); endpoints use `RequireAuthorization("CanRefund")`, never `[Authorize(Roles = "Manager")]`

Login failures return an identical response and take comparable time for unknown-email and wrong-password. A distinguishable response is a user-enumeration oracle.

**Exit criteria**
- [x] Login → access + refresh; refresh rotates
- [x] Reusing a rotated refresh token revokes the family, with a test
- [x] All policies registered; a test asserts each role's granted set matches the table
- [x] `grep` finds no role literals in endpoint attributes

## 1.5 PIN login

- `Register` entity + enrollment endpoint returning the device token **once**
- `POST /auth/pin` requires `X-Device-Token` matching an active, non-revoked register
- PIN hashed with the same password hasher (never a plain hash, never plaintext comparison)
- Rate limit per register **and** per-user lockout with an expiry returned to the client
- `GET /employees/pin-eligible` returns id + display name only

**A PIN is authentication to a trusted device, not authentication on its own.** Four digits is 10,000 possibilities — trivially brute-forced without the device token and the lockout. Both are required, and a PIN request from an unenrolled device is rejected before the PIN is even checked.

Three implementation notes worth keeping:

- The device check is a **real authentication scheme**, not a check inside the endpoint, because authentication runs before the endpoint does. An unenrolled caller is turned away before the PIN is read — so the endpoint cannot be used as a PIN oracle, and cannot be used to lock every cashier out by burning their attempt budgets.
- The `EnrolledDevice` policy must **name the scheme** (`.AddAuthenticationSchemes(...)`). Left to the default, a cashier's ordinary access token satisfies "enrolled device" and the second factor evaporates with no test going red. Register it *outside* `PolicyCatalog` — that catalog is role-to-policy and a test asserts its keys match the table in `ARCHITECTURE.md`.
- The rate limit partitions on the **device token**, not the user. Per-user lockout alone caps the wrong thing: an attacker holding one till spends five attempts per cashier and moves on, and `pin-eligible` supplies the names to move on to.

**Exit criteria**
- [x] Enrollment returns the token once; it is unretrievable afterwards
- [x] PIN login works from an enrolled device
- [x] PIN login from an unenrolled/revoked device is rejected, with a test
- [x] Lockout triggers and reports its expiry, with a test
- [x] `pin-eligible` leaks no roles, emails or contact details — asserted on the serialised JSON, not the DTO

## 1.6 RLS hardening

A migration (raw SQL) that, for every tenant table:

```sql
ALTER TABLE product ENABLE ROW LEVEL SECURITY;
ALTER TABLE product FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON product
  USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
```

Two roles: a migration owner that bypasses RLS, and the application role that does not.

> **Corrected 2026-07-31 — this section originally said `SET LOCAL app.tenant_id` per transaction. Do not build that.** Outside an explicit transaction `SET LOCAL` scopes to the implicit single-statement transaction, and EF reads do not open transactions — so the setting would evaporate before the next statement. RLS would have looked configured and enforced nothing, silently, which is the worst available outcome for a security layer.
>
> Use **session-level** `SELECT set_config('app.tenant_id', $1, false)` issued from a `DbConnectionInterceptor` on connection open, parameterised and never interpolated. This is safe with connection pooling because Npgsql sends `DISCARD ALL` on reset — which means the connection string must **not** set `No Reset On Close=true`, and multiplexing must stay off. Write the test that proves a pooled connection does not inherit the previous request's tenant; that is the one that catches a regression here.

**Why this exists even though 1.2 already filters:** EF's global query filters do not apply to `FromSqlRaw`, `ExecuteSqlRaw`, Dapper, or a hand-written report query — and reporting is exactly where someone reaches for raw SQL. RLS is the layer that holds when application code is wrong.

`FORCE ROW LEVEL SECURITY` matters: without it, the table owner silently bypasses its own policy, and if the app connects as owner the policies do nothing while appearing to be in place.

### As built

- The loop lives in `Pos.Data/Migrations/TenantSecurityMigrationExtensions.cs`, not inside the migration. **A migration that has already been applied does not re-run**, so the catalog loop does not cover a table introduced in Phase 2 — that migration has to call `migrationBuilder.ApplyTenantRowLevelSecurity()` itself. `RowLevelSecurityTests.Every_tenant_owned_table_is_covered` is what fails when somebody forgets, which is the point: it is a build failure rather than a code review's job.
- `nullif(current_setting('app.tenant_id', true), '')::uuid`, not the bare `current_setting`. An unset GUC reads as NULL but a **reset** one reads as the empty string, and `''::uuid` raises instead of yielding NULL. Both have to collapse to NULL so "no tenant" means "no rows".
- `WITH CHECK` as well as `USING`, so a raw INSERT cannot write a row into a tenant it could never read back.
- The whole `Pos.Api.Tests` suite now connects as `pos_app`. Run as the container's owner it would pass with no policies at all, because a superuser bypasses RLS unconditionally — `The_application_role_does_not_bypass_row_level_security` guards that.

**Exit criteria**
- [x] RLS enabled and forced on every tenant table — by a `DO` block looping every table with a `tenant_id` column, not a hand-written list, so later phases inherit it
- [x] A test asserts every such table has RLS enabled, forced, and a policy
- [x] The app connects as a non-owner role
- [x] `set_config('app.tenant_id', $1, false)` issued from a connection interceptor
- [x] A test proves a pooled connection does not inherit the previous request's tenant
- [x] A raw-SQL query for another tenant's rows returns nothing, with a test

## 1.7 Isolation test suite

`tests/Pos.Api.Tests/Isolation/` — the tests this whole phase exists to pass. Seed two tenants with deliberately similar data (same product names, same emails, same SKUs) so a leak is unmistakable.

- [x] Every collection endpoint, as tenant B, returns none of tenant A's rows
- [x] `GET /{resource}/{tenantA-id}` as tenant B → **404, not 403** (403 confirms existence)
- [x] A direct `DbContext` query in the test host is filtered
- [x] A raw-SQL path returns nothing (RLS)
- [x] A request body containing tenant A's `TenantId` is **never honoured** — see the note below
- [x] A token with a forged/absent `tenant_id` claim is rejected
- [x] Tenant A's user cannot authenticate into tenant B's data with valid credentials

Uses `WebApplicationFactory` + Testcontainers Postgres so it runs against real RLS, not an in-memory provider that silently ignores it.

### As built

The suite is **driven off a manifest** (`Isolation/IsolationManifest.cs`) listing every endpoint and
how its tenancy is proven; `EndpointCoverageTests` diffs that manifest against the router's own
endpoint table in both directions. Mapping an endpoint without deciding how it is isolation-tested is
therefore a build failure naming the route, which is the same mechanism as
`RowLevelSecurityTests.Every_tenant_owned_table_is_covered` one layer down. **Phase 2 adds a row, not
a test file** — and an `Exempt` row must name the test where the coverage does live, so the manifest
reads as this phase's audit record rather than a list of skips.

Three things worth keeping:

- **Item 5 says "rejected"; what the server does is ignore.** `CreateRegisterRequest` has no
  `TenantId` to bind to, so an extra property in the body goes nowhere and the interceptor stamps the
  tenant from the token regardless. The guarantee asserted is therefore *never honoured*, which is the
  one that matters. Rejecting unknown properties outright (`UnmappedMemberHandling.Disallow`) was
  considered and not done: it breaks every client on the first field the server has not heard of yet,
  and buys nothing here.
- **A 404-asserting test passes when the URL is wrong.** The by-id theory builds its URLs by
  substituting into the route template from the manifest key — the same key `EndpointCoverageTests`
  has already matched against the routing table — so a route that does not exist cannot be addressed.
  Verified by pointing the theory at a *reachable* id and watching the 404 become a 204.
- **The negative-authorization theory probes an id that exists in no tenant.** If an authorization
  check were removed, the answer is 404 rather than a real write against a real till, and 404 fails
  the assertion.

---

## Verification

```powershell
docker compose up -d
dotnet test                                    # isolation suite must be green

# Migrations connect as the schema owner; the app's connection string is pos_app, which has
# no CREATE on the schema. The full command is in CLAUDE.md.
dotnet ef database update --project src/Pos.Data --startup-project src/Pos.Api `
  --connection "Host=localhost;Port=5432;Database=pos_dev;Username=pos;Password=dev_only_not_a_secret"

dotnet run --project src/Pos.Api               # :5013
```

**The manual check is deferred to Phase 4, and this is why.** It reads "create two tenants via the
onboarding path, log in as each, confirm neither can see the other's data through Swagger" — and none
of those three things exists. There is no onboarding path (no platform admin UI until after the first
paying client, by decision), no API docs UI (Development serves the raw OpenAPI document at
`/openapi/v1.json` and nothing renders it), and no login screen until Phase 4. Doing it now means
building a dev seeding script and adding a docs UI first.

It is confirmation rather than coverage: `tests/Pos.Api.Tests/Isolation/` asserts the same thing on
every endpoint, on every run, against real Postgres with row-level security enforcing. The gate below
rests on that, not on this.

## Gate

**Do not start Phase 2 until 1.7 is fully green.** Every entity added afterwards inherits isolation automatically; entities added before it must each be audited by hand.
