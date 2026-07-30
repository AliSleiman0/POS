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
- [ ] `ITenantContext` in Core with no HTTP dependency
- [ ] Unresolved tenant access throws, with a test proving it
- [ ] System context is opt-in, never a fallback

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
- [ ] Filter applied to every `TenantEntity` by reflection; a test adds a throwaway entity and confirms it is filtered without any per-entity registration
- [ ] Interceptor stamps on insert
- [ ] Cross-tenant modify throws, with a test
- [ ] Money columns land as `numeric(19,4)` in the generated migration

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

`ApplicationUser` is *not* covered by the global query filter for Identity's own login lookups — those must resolve a user before a tenant is known. Login therefore resolves tenant **from the user record**, and every other user query goes through a tenant-scoped repository. This is the one deliberate exception, and it is documented here so nobody "fixes" it.

**Exit criteria**
- [ ] Composite unique index in the migration
- [ ] A test creates the same email under two tenants successfully
- [ ] A test confirms duplicate email *within* one tenant is rejected

## 1.4 JWT + RBAC

`Pos.Api/Auth/`: `TokenService`, `AuthEndpoints`, `AuthorizationPolicies`.

- Access token ~15 min, claims `sub`, `tenant_id`, `role`, `register_id?`
- Refresh token opaque, hashed at rest, **rotated every use**, grouped by `FamilyId`; reuse of a rotated token revokes the family
- Signing key from user-secrets/env — never a literal, and validated non-empty at startup so a misconfigured deploy fails fast instead of signing with a blank key
- Policies registered per the table in [ARCHITECTURE.md](../ARCHITECTURE.md#authorization); endpoints use `RequireAuthorization("CanRefund")`, never `[Authorize(Roles = "Manager")]`

Login failures return an identical response and take comparable time for unknown-email and wrong-password. A distinguishable response is a user-enumeration oracle.

**Exit criteria**
- [ ] Login → access + refresh; refresh rotates
- [ ] Reusing a rotated refresh token revokes the family, with a test
- [ ] All policies registered; a test asserts each role's granted set matches the table
- [ ] `grep` finds no role literals in endpoint attributes

## 1.5 PIN login

- `Register` entity + enrollment endpoint returning the device token **once**
- `POST /auth/pin` requires `X-Device-Token` matching an active, non-revoked register
- PIN hashed with the same password hasher (never a plain hash, never plaintext comparison)
- Rate limit per (register, user): N attempts per window, then lockout with an expiry returned to the client
- `GET /employees/pin-eligible` returns id + display name only

**A PIN is authentication to a trusted device, not authentication on its own.** Four digits is 10,000 possibilities — trivially brute-forced without the device token and the lockout. Both are required, and a PIN request from an unenrolled device is rejected before the PIN is even checked.

**Exit criteria**
- [ ] Enrollment returns the token once; it is unretrievable afterwards
- [ ] PIN login works from an enrolled device
- [ ] PIN login from an unenrolled/revoked device is rejected, with a test
- [ ] Lockout triggers and expires, with a test
- [ ] `pin-eligible` leaks no roles, emails or contact details

## 1.6 RLS hardening

A migration (raw SQL) that, for every tenant table:

```sql
ALTER TABLE product ENABLE ROW LEVEL SECURITY;
ALTER TABLE product FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON product
  USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
```

Two roles: a migration owner that bypasses RLS, and the application role that does not. On connection checkout, the app issues `SET LOCAL app.tenant_id = '<guid>'` inside the transaction.

**Why this exists even though 1.2 already filters:** EF's global query filters do not apply to `FromSqlRaw`, `ExecuteSqlRaw`, Dapper, or a hand-written report query — and reporting is exactly where someone reaches for raw SQL. RLS is the layer that holds when application code is wrong.

`FORCE ROW LEVEL SECURITY` matters: without it, the table owner silently bypasses its own policy, and if the app connects as owner the policies do nothing while appearing to be in place.

**Exit criteria**
- [ ] RLS enabled and forced on every tenant table
- [ ] The app connects as a non-owner role
- [ ] `SET LOCAL` issued per transaction
- [ ] A raw-SQL query for another tenant's rows returns nothing, with a test

## 1.7 Isolation test suite

`tests/Pos.Api.Tests/Isolation/` — the tests this whole phase exists to pass. Seed two tenants with deliberately similar data (same product names, same emails, same SKUs) so a leak is unmistakable.

- [ ] Every collection endpoint, as tenant B, returns none of tenant A's rows
- [ ] `GET /{resource}/{tenantA-id}` as tenant B → **404, not 403** (403 confirms existence)
- [ ] A direct `DbContext` query in the test host is filtered
- [ ] A raw-SQL path returns nothing (RLS)
- [ ] A request body containing tenant A's `TenantId` is rejected, not honoured
- [ ] A token with a forged/absent `tenant_id` claim is rejected
- [ ] Tenant A's user cannot authenticate into tenant B's data with valid credentials

Uses `WebApplicationFactory` + Testcontainers Postgres so it runs against real RLS, not an in-memory provider that silently ignores it.

---

## Verification

```powershell
docker compose up -d
dotnet ef database update --project src/Pos.Data --startup-project src/Pos.Api
dotnet test                                    # isolation suite must be green
dotnet run --project src/Pos.Api               # /swagger, exercise /auth/login
```

Manual: create two tenants via the onboarding path, log in as each, confirm neither can see the other's data through Swagger.

## Gate

**Do not start Phase 2 until 1.7 is fully green.** Every entity added afterwards inherits isolation automatically; entities added before it must each be audited by hand.
