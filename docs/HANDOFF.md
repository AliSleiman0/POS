# Session Handoff

**Written:** 2026-08-01 · **Branch:** `phase-2/catalog-inventory` (not merged) · **Phase 2.1 complete — 2.2 next**

> The catalog tables exist and are proven. 178 tests green locally. **Not yet run in CI** — the branch has not been pushed, and Phase 1's rule is that green means green on the runner. Push and check before calling 2.1 shipped.

> This file is session state, not durable truth. Overwrite it at the end of each session. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`docs/phases/PHASE-2-catalog-inventory.md`](phases/PHASE-2-catalog-inventory.md) → **2.2 CRUD API**, the next milestone.
2. [`DECISIONS.md`](../DECISIONS.md) → **"Resolved 2026-08-01 (during Phase 2)"** — foreign keys carry the tenant, and why that is not optional.
3. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants.

## State

**178 tests green locally** — 41 Core, 58 Data, 79 Api (was 133). Release build with `$env:CI="true"` is warning-free, `pnpm build` is clean.

Landed this session:

- `506d1e5` **Phase 2 prep** — `tools/Pos.Seed`, and card payments dropped from the plan (see below).
- **2.1 catalog & inventory** — five entities, five configurations, the `CatalogAndInventory` migration, 27 new Data tests and 18 new Core tests, plus a seeded catalog.

### The one thing to know before writing 2.2

**Foreign keys carry `tenant_id`.** `barcode(tenant_id, product_id) → product(tenant_id, id)`, pointing at `ak_*_tenant_id_id` alternate keys, `RESTRICT` on delete.

The reason is not obvious and will get "simplified" away by anyone who does not know it: **Postgres exempts referential integrity checks from row-level security.** RI triggers run as the referenced table's owner with row security off, so a plain `product_id` key would let tenant B reference tenant A's product and the check would *accept* it. RLS is not a backstop here — it is bypassed by design. Pinned by `CatalogForeignKeyTests`, which runs as `pos_app` for exactly that reason.

Practical consequence for 2.2: `HasPrincipalKey` must be called **before** `HasForeignKey`, or EF matches against the single-column primary key and throws about the number of properties.

### There are no navigation properties, so there is no FK fixup

A product's `Id` is `Guid.Empty` until `TenantSaveChangesInterceptor` stamps it during `SaveChanges`. So `db.Products.Add(p); db.Barcodes.Add(new Barcode { ProductId = p.Id })` in one call writes `ProductId = Guid.Empty` and fails the foreign key. **Principals are saved before dependents** — `CatalogGraph` in the tests and `CatalogSeeder` both do this deliberately. `POST /products` with inline barcodes has to do the same, or pre-assign the id.

### Falsification results

All three deliberate breaks were run and reverted; nothing from them remains.

| Break | Result |
|---|---|
| Pointed a cross-tenant FK test at the *same* tenant's product | No exception — so the `23503` is caused by the cross-tenancy, not by something incidental |
| Pointed `TenantModelTests` at the Identity namespace | Named `ix_user_claim_user_id`, `ix_user_login_user_id`, `ix_user_role_role_id` and five foreign keys — the detector finds offenders and reports them |
| Removed `ApplyTenantRowLevelSecurity()` from the migration | Listed exactly the five new tables |

The second one is worth repeating in Phase 3: a model-wide test that enumerates and finds nothing is indistinguishable from one that enumerates nothing.

## Things that will bite you

New this session (1–5); the rest carried forward and still true.

1. **`UseXminAsConcurrencyToken()` does not exist in Npgsql 10.** The string `Xmin` is absent from the assembly. The replacement is a convention matching `uint` + generated-on-add-or-update + concurrency token, i.e. an explicit property with `IsRowVersion()`. Change any of the three and it silently becomes an ordinary column.
2. **The scaffolded migration lists an `xmin` column that the generated SQL does not contain.** That is correct — Npgsql suppresses migration operations for system columns. Do not "fix" the C# by deleting it; the property has to be in the model for the mapping to exist. Read the DDL with `dotnet ef migrations script`, not the C#, when in doubt.
3. **A model/migration mismatch fails the entire Data and Api suites at fixture init**, with `PendingModelChangesWarning` — not with the assertion you were trying to see. Add the migration in the same commit as the configuration change; do not run integration tests in between.
4. **EF blocks a delete client-side when the dependent is loaded in the same context**, with "the association has been severed", before any SQL is issued. A test that means to prove the *database* restricts has to use a second scope, or it proves only that EF is careful.
5. **`HasDefaultValue(true)` on a bool that is legitimately inserted as `false` needs `HasSentinel`.** EF omits a property equal to the sentinel from the INSERT, so `TrackStock = false` could be written as `true` by the column default. Stated explicitly on `Product.TrackStock` and round-tripped by a test — the failure is otherwise silent.
6. **An applied migration does not re-run.** Any migration adding a tenant table must call `ApplyTenantRowLevelSecurity()`. In `Down`, call **`Apply`, not `Remove`** — `Remove` is catalog-driven and would strip RLS from every other tenant table with no migration left to restore it.
7. **Health-check endpoints have no HTTP method metadata**, so they appear as `ANY health/live`.
8. **A test that asserts 404 passes when it reaches nothing.** Any new "not found" assertion needs independent evidence the route was real.
9. **A shared `WebApplicationFactory` has no reset between tests.** Create your own tenant with a unique slug, or treat the shared world as read-only.
10. **A superuser bypasses RLS unconditionally, `FORCE` or not.** Both fixtures connect the application as `pos_app`. An isolation assertion on `postgres.ConnectionString` proves nothing.
11. **A reset Postgres session variable reads as `''`, not NULL** — the policies use `nullif(...)` so both collapse to NULL. Fails closed.
12. **`dotnet run` uses `launchSettings.json` and ignores `ASPNETCORE_URLS`.** It listens on 5013.
13. **An authorization policy that does not name its scheme evaluates the default one.**
14. **A request can carry both a JWT and a device token.** `AmbientTenantContext.Resolve` refuses to switch tenants.
15. **`WebApplicationFactory.ConfigureAppConfiguration` is applied at `Build()`**, but `Program.cs` reads `builder.Configuration` before it. Use `builder.UseSetting(...)`.
16. **Never run the test host in `Development`** — it loads user secrets pointing at local Docker.
17. **`problem+json` carries a per-request `traceId`.** Compare `type`/`title`/`status`/`detail`.
18. **EF names tables from the `DbSet` property name**, not the entity type — so every configuration calls `ToTable()` explicitly.
19. **`[CallerFilePath]` is a lie under CI.** Anchor on `AppContext.BaseDirectory`; `$env:CI="true"` reproduces CI-only behaviour.
20. **`dotnet ef database update` needs the owner connection string passed explicitly.** Full command in [`CLAUDE.md`](../CLAUDE.md#commands).
21. **Kill stray `Pos.Api` processes before rebuilding** — they lock `Pos.Data.dll` (`MSB3027`).
22. **Adding shadcn components may reintroduce the two-React-copies error.** Fix is `test.server.deps.inline`.
23. **Test fixtures must not contain the strings a test proves absent.** The seeder's display names avoid the words Owner/Manager/Cashier for this reason.

## Working preferences noted

- **Don't run `dotnet build` and `dotnet test` back to back** — `dotnet test` builds.
- **UI testing must not accumulate.** Phases 4–7: a milestone is not done until its Vitest/Playwright coverage exists *and* someone has looked at the screen.
- **Prove a security test can fail.** Three breaks this session; one of them (the delete test) found a test that was passing without reaching the database at all.

## Outstanding / deferred

- **Push the branch and confirm CI is green.** 2.1 is not done until it is.
- **There is still no API docs UI, and 2.5's verification is a click-through.** `MapOpenApi()` serves the raw document at `/openapi/v1.json` and nothing renders it. Adding Scalar is one package and about three dev-gated lines. **Decide at the start of 2.2**, when there are endpoints worth clicking — the seeded catalog is now the other half of making that walkthrough possible.
- **`ix_product_tenant_name` does not serve 2.2's case-insensitive search.** A plain btree answers ordering and `LIKE 'x%'`; the `ILIKE '%x%'` that milestone promises will sequential-scan. Needs `lower(name)` or `pg_trgm` — a 2.2 decision, recorded now so it is not a surprise.
- **"One primary barcode per product" is not enforced.** Deliberately left to 2.3: a filtered unique index would make EF think the FK index is covered, and would force "make this primary" into a clear-then-set across two `SaveChanges`. The milestone that builds those endpoints should pay for it knowingly.
- **Category cycles are not preventable by a foreign key.** `fk_category_parent` guarantees the parent is in the same tenant and nothing more. 2.2's endpoint has to check.
- **`dotnet dev-certs https --trust`** still not run (opens a Windows dialog).
- **Playwright browsers not installed** — `pnpm exec playwright install --with-deps` in Phase 4.
- **No visual verification of the web UI.** Eyeball it before Phase 4 builds on top.
- **Production `pos_app` password** — Phase 8.2 supplies a real one; the compose file already reads it from the environment.
- **CI actions emit a Node 20 deprecation warning.** Bump to `@v5` when available.
- **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc.

## Decided recently

**Card payments are out of scope for the product — not deferred.** The shop takes cash across a counter, so there is no processor to integrate and **Phase 11 is dropped**. `Tender` still stays a collection of rows with a `Method` discriminator: split tender and change due need that shape for cash alone. Nothing in Phases 2–8 changes.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price.
