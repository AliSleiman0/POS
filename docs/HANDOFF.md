# Session Handoff

**Written:** 2026-08-01 · **Branch:** `phase-2/catalog-inventory`, pushed, not merged · **2.1 done — start at 2.2**

> The catalog tables exist and are proven. **178 tests green locally and in CI** — run `30695851197`, 41/58/79, the same counts on the runner as on the machine. Continue on this branch: 2.2–2.5 are the same phase and should land together before a PR.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`docs/phases/PHASE-2-catalog-inventory.md`](phases/PHASE-2-catalog-inventory.md) → **§2.2**, the next milestone. §2.1's exit criteria are ticked with what proves each.
2. [`docs/API.md`](API.md#products--products) — the endpoint contracts for 2.2, already written. Routes and policies are decided; don't reinvent them.
3. [`DECISIONS.md`](../DECISIONS.md) → **"Resolved 2026-08-01 (during Phase 2)"** — foreign keys carry the tenant, and why that is not optional.
4. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants.

## State

**178 tests green, locally and in CI** — 41 Core, 58 Data, 79 Api. Release build warning-free with `$env:CI="true"`; `pnpm build` clean; oxlint 0 warnings. CI runs the Data and Api suites against real Postgres via Testcontainers on the ubuntu runner, so the migration and its RLS hand-edit are exercised there too.

- `506d1e5` Phase 2 prep — `tools/Pos.Seed`, card payments dropped from the plan.
- `8a785f2` **Phase 2.1** — five entities, five configurations, the `CatalogAndInventory` migration, 45 new tests, a seeded catalog.

The dev database is migrated and seeded: tenant `corner-shop`, Owner/Manager/Cashier (`Dev-Password-1`, PINs 7391/4821), two registers, and six products. Re-run `dotnet run --project tools/Pos.Seed` any time — it is idempotent.

---

## What 2.2 is

Three endpoint files in `src/Pos.Api/Endpoints/`: `ProductEndpoints.cs`, `CategoryEndpoints.cs`, `TaxClassEndpoints.cs`. Routes, policies and semantics are already specified in [`API.md`](API.md#products--products) — `GET /products` (`CanSell`, `?q=`, `?categoryId=`, `?activeOnly=`, paginated), `POST`/`PUT` (`CanManageCatalog`), `POST /products/{id}/deactivate`, and the same shape for categories and tax classes.

**Four things that milestone gets judged on:**

1. **Cursor pagination via a shared helper**, written once. It is used by every list endpoint in Phases 2, 6 and 7, so it is worth getting right here rather than copying.
2. **`costPrice` is omitted from the response** for callers without `CanViewMargins` — not sent-and-hidden. A Cashier's payload does not contain the field. Invariant 7.
3. **No `DELETE` verb for products.** `POST /products/{id}/deactivate` sets `IsActive = false`.
4. **Validation returns `problem+json` with per-field errors** — prices non-negative, SKU non-empty and unique per tenant.

### Two mechanisms will fail your build, by design

- **`tests/Pos.Api.Tests/Isolation/IsolationManifest.cs`** — map an endpoint without adding a row and `EndpointCoverageTests` fails, naming the route. Adding the row gives you the cross-tenant test *and* the negative-authorization test at once. An `Exempt` row must name the test where its coverage actually lives.
- **`RowLevelSecurityTests.Every_tenant_owned_table_is_covered`** — 2.4 adds `StockMovement`, so that migration must call `ApplyTenantRowLevelSecurity()`. In `Down`, call **`Apply`, not `Remove`** (see bite 6).

### What 2.1 already gives you

- Entities and configurations for all five catalog types, with `Product.NormalizeSku` (trim + invariant uppercase) — **use it on every write path**, or `abc` and `ABC` become two products and the unique index will not stop it.
- `CatalogGraph` in `tests/Pos.Data.Tests/Catalog/` writes a complete five-entity graph for a tenant. The Api suite will want something similar against `TwoTenantWorld`.
- `TenantModelTests` and `DatabaseSchemaTests` are model-wide: new Phase 2 entities are checked for query filters, tenant-leading indexes, tenant-carrying foreign keys and `numeric(19,4)` without anyone editing them.
- A seeded catalog with the awkward cases already in it: two barcodes on one product, a `Kilogram` item with fractional stock, a price of `0.1650`, and a product with no category that does not track stock.

### Decide these at the start of 2.2, not in the middle

- **Scalar (the API docs UI).** Still not added. `MapOpenApi()` serves the raw document at `/openapi/v1.json` and nothing renders it. One package, ~3 dev-gated lines. **2.5's verification is a click-through of the catalog and cannot be done without it** — and now that the catalog is seeded, it is the only thing missing. Also on the critical path for Phase 4.1's generated client.
- **Case-insensitive search.** `ix_product_tenant_name` is a plain btree: it serves ordering and `LIKE 'x%'` only. The `?q=` that 2.2 promises is a *contains* search, and `ILIKE '%x%'` will sequential-scan straight past that index. Decide `lower(name)` + an expression index, or `pg_trgm`, before writing the query — retrofitting means another migration.
- **"One primary barcode per product" is not enforced** in the schema, deliberately. A filtered unique index would make EF think the FK index is covered, and would force "make this primary" into a clear-then-set across two `SaveChanges`. 2.3 builds those endpoints and should decide knowingly.
- **Category cycles.** `fk_category_parent` guarantees the parent is in the same tenant and nothing more; no SQL constraint can prevent a cycle. The endpoint that sets `ParentCategoryId` has to check.

---

## Things that will bite you

New in 2.1 (1–5); the rest carried forward and still true.

1. **`HasPrincipalKey` must be called before `HasForeignKey`.** The other way round, EF matches the two FK properties against the principal's single-column primary key and throws about the number of properties not matching.
2. **There are no navigation properties, so there is no FK fixup.** A product's `Id` is `Guid.Empty` until `TenantSaveChangesInterceptor` stamps it during `SaveChanges`, so `db.Products.Add(p); db.Barcodes.Add(new Barcode { ProductId = p.Id })` in one call writes `Guid.Empty` and fails the foreign key. **Save principals before dependents** — `POST /products` with inline barcodes has to, or pre-assign the id.
3. **A model/migration mismatch fails the entire Data and Api suites at fixture init** with `PendingModelChangesWarning`, not with the assertion you were looking at. Add the migration in the same commit as the configuration change.
4. **EF blocks a delete client-side when the dependent is loaded in the same context**, before any SQL is issued. A test that means to prove the *database* restricts must use a second scope — this was found by falsification, having passed for the wrong reason.
5. **`HasDefaultValue(true)` on a bool that is legitimately inserted as `false` needs `HasSentinel`.** EF omits a property equal to the sentinel from the INSERT, so `TrackStock = false` would be written as `true` by the column default. Stated explicitly on `Product.TrackStock`; the failure is otherwise silent.
6. **An applied migration does not re-run.** Any migration adding a tenant table calls `ApplyTenantRowLevelSecurity()`. In `Down` call **`Apply`, not `Remove`** — `Remove` is catalog-driven and would strip RLS from every *other* tenant table, with no migration left to restore it.
7. **`UseXminAsConcurrencyToken()` does not exist in Npgsql 10.** The replacement convention matches `uint` + generated-on-add-or-update + concurrency token. Phase 3.6 depends on this; `StockItemConcurrencyTests` pins it.
8. **The scaffolded migration lists an `xmin` column the generated SQL does not contain.** Correct — Npgsql suppresses system columns. Read the DDL with `dotnet ef migrations script`, not the C#.
9. **Health-check endpoints have no HTTP method metadata**, so they appear as `ANY health/live`. Anything enumerating `EndpointDataSource` must handle it.
10. **A test that asserts 404 passes when it reaches nothing.** Any "not found" assertion needs independent evidence the route was real.
11. **A shared `WebApplicationFactory` has no reset between tests.** Create your own tenant with a unique slug, or treat `TwoTenantWorld` as read-only — the exact-count assertions depend on it.
12. **A superuser bypasses RLS unconditionally, `FORCE` or not.** Isolation assertions must run on `pos_app` (`postgres.AppConnectionString`), never the owner.
13. **A reset Postgres session variable reads as `''`, not NULL.** The policies use `nullif(...)`, so both collapse to NULL. Fails closed.
14. **`dotnet run` uses `launchSettings.json` and ignores `ASPNETCORE_URLS`.** Port 5013.
15. **An authorization policy that does not name its scheme evaluates the default one.**
16. **A request can carry both a JWT and a device token.** `AmbientTenantContext.Resolve` refuses to switch tenants.
17. **`WebApplicationFactory.ConfigureAppConfiguration` is applied at `Build()`**, but `Program.cs` reads `builder.Configuration` before it. Use `builder.UseSetting(...)`.
18. **Never run the test host in `Development`** — it loads user secrets pointing at local Docker.
19. **`problem+json` carries a per-request `traceId`.** Compare `type`/`title`/`status`/`detail`, never raw strings.
20. **EF names tables from the `DbSet` property name**, so every configuration calls `ToTable()` explicitly.
21. **`[CallerFilePath]` is a lie under CI.** Anchor on `AppContext.BaseDirectory`; `$env:CI="true"` reproduces CI-only behaviour locally.
22. **`dotnet ef database update` needs the owner connection string passed explicitly.** Full command in [`CLAUDE.md`](../CLAUDE.md#commands).
23. **Kill stray `Pos.Api` processes before rebuilding** — they lock `Pos.Data.dll` (`MSB3027`). `Stop-Process`, not `pkill`.
24. **Adding shadcn components may reintroduce the two-React-copies error.** Fix is `test.server.deps.inline` in `vite.config.ts`.
25. **Test fixtures must not contain the strings a test proves absent.** The seeder's display names avoid the words Owner/Manager/Cashier for this reason.

## Working preferences noted

- **Don't run `dotnet build` and `dotnet test` back to back** — `dotnet test` builds.
- **UI testing must not accumulate.** Phases 4–7: a milestone is not done until its Vitest/Playwright coverage exists *and* someone has looked at the screen. jsdom does not paint.
- **Prove a security test can fail.** Three deliberate breaks in 2.1; one found a test passing without reaching the database at all. Do this for every isolation and authorization test in 2.2.

## Outstanding / deferred

- **Scalar, case-insensitive search, primary-barcode uniqueness, category cycles** — see "Decide these at the start of 2.2" above.
- **`dotnet dev-certs https --trust`** still not run (opens a Windows dialog that cannot be scripted).
- **Playwright browsers not installed** — `pnpm exec playwright install --with-deps` in Phase 4, a ~500 MB download nothing needs before then.
- **No visual verification of the web UI.** Eyeball it before Phase 4 builds on top.
- **Production `pos_app` password** — Phase 8.2 supplies a real one; the compose file already reads it from the environment.
- **A validly signed token is trusted for whatever tenant it names** — accepted and pinned by `ForgedTenancyTests`, reasoning in `DECISIONS.md`. The signing key is the tenancy boundary.
- **CI actions emit a Node 20 deprecation warning.** Bump to `@v5` when available.
- **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc. Remove when ASP.NET Core ships a patched dependency.

## Decided recently

**Card payments are out of scope for the product — not deferred.** The shop takes cash across a counter, so there is no processor to integrate and **Phase 11 is dropped**. `Tender` still stays a collection of rows with a `Method` discriminator: split tender and change due need that shape for cash alone, and it keeps a future terminal from being a `Sale` migration. Nothing in Phases 2–8 changes.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price: "lifetime hosting for a one-time fee" is a liability that grows with every tenant.
