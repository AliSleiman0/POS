# Session Handoff

**Written:** 2026-08-01 · **Branch:** `phase-2/catalog-api` · **2.2 done — start at 2.3**

> The catalog is behind the API and browsable. **414 tests green locally** — 73 Core, 64 Data, 277 Api. Release build warning-free with `$env:CI="true"`, `pnpm build` clean. **Not yet pushed or confirmed in CI** — do that before ticking anything as finished.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`docs/phases/PHASE-2-catalog-inventory.md`](phases/PHASE-2-catalog-inventory.md) → **§2.3**, the next milestone. §2.2's exit criteria are ticked with what proves each.
2. [`docs/API.md`](API.md#products--products) — the endpoint contracts, now updated with what 2.2 actually built.
3. [`DECISIONS.md`](../DECISIONS.md) → **"Resolved 2026-08-01 (during Phase 2.2)"** — seven decisions, including two that constrain Phase 8.2's hosting.
4. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants.

## State

**414 tests green** — 73 Core, 64 Data, 277 Api. Seven commits on `phase-2/catalog-api`:

- `3973adc` Scalar + `JsonStringEnumConverter`
- `22a0219` Core catalog rules and exceptions
- `11eb232` the `pg_trgm` search index
- `8182d41` cursor pagination
- `8cf053b` test infrastructure (world catalog, sandbox tenant, manifest machinery)
- `d753975` tax class endpoints
- `f638987` category endpoints
- `b809d3d` product endpoints

The dev database is migrated and seeded. `dotnet run --project src/Pos.Api`, then **`http://localhost:5013/scalar/`** — that is new, and it is how you exercise anything by hand now.

---

## What 2.3 is

`GET /products/by-barcode/{code}` — the hottest read in the product — plus `POST /products/{id}/barcodes` and `DELETE /products/{id}/barcodes/{barcodeId}`. Contracts in [`API.md`](API.md#products--products).

### Three things that will stop you on the first afternoon

1. **`IsolationCase.UrlFor` substitutes only the first `{…}` segment.** `DELETE /products/{id}/barcodes/{barcodeId}` has two, so the by-id theory cannot address it as written. Extend `UrlFor`, or give the case a second id — decide which before writing the row, because the theory asserts a 404 and *a mistyped URL also returns 404*.
2. **`ActorExtensions.SendAsync` has no `DELETE`.** It throws `NotSupportedException` by design so the gap is loud. One line.
3. **Barcodes are the first dependent this milestone writes.** There are no navigation properties, so a product's `Id` is `Guid.Empty` until `TenantSaveChangesInterceptor` stamps it — save the product before the barcode, or pre-assign the id. `CatalogFixture` does this in three passes and is the pattern to copy.

### The decision 2.2 deliberately left to you

**"One primary barcode per product" is still not enforced.** A filtered unique index would make EF think the FK index is covered, and would force "make this primary" into a clear-then-set across two `SaveChanges`. The tax-class default is exactly that shape and now has a worked implementation to copy — `TaxClassEndpoints.SaveWithDefaultAsync`, including the execution-strategy wrapper that a retrying connection requires. Decide knowingly; do not inherit it by accident.

### What 2.2 gives you to build on

- **`CursorPaging.ToPageAsync`** — used by all three list endpoints. Give it a key selector, a projection expression and a `PageRequest`; it handles the keyset, the extra row for `hasMore`, and minting the next cursor. `PageQuery.TryRead` is the single place `?limit=` and `?cursor=` are validated.
- **`PostgresErrors.IsUniqueViolation(exception, constraintName)`** — 2.3 needs it for `ux_barcode_tenant_code`. The constraint name is not optional.
- **`CatalogFixture`** writes an identical catalog into any tenant; `TwoTenantWorld` has one in both, and `PosApiFactory.CatalogSandboxAsync()` gives you a mutable tenant with Owner, Manager and Cashier already created.
- **`factory.SignedInAsync(RoleNames.X)`** — a client signed into the sandbox, one line.

---

## Things that will bite you

New in 2.2 (1–7); the rest carried forward and still true.

1. **A stale build can make a correct migration emit wrong SQL.** After restoring files by copy (rather than by editor), `dotnet ef migrations script` kept emitting a plain btree while the source clearly said `gin`. Neither `dotnet build --no-incremental` nor `dotnet test` noticed. `Remove-Item -Recurse src/Pos.Data/obj, src/Pos.Data/bin` fixed it. If generated SQL disagrees with source you have read with your own eyes, clean before debugging.
2. **A model change touches four files, and a partial edit fails the *whole* suite with `PendingModelChangesWarning`.** The configuration, the migration, its `.Designer.cs` and `AppDbContextModelSnapshot.cs` must all agree. Editing only the first two makes every Data and Api test fail at fixture init with an error unrelated to what you were doing. `dotnet ef migrations remove` refuses once the migration is applied to the dev database, so it is not the escape hatch it looks like.
3. **A retrying execution strategy refuses a user-initiated transaction.** `EnableRetryOnFailure` is configured, so `BeginTransactionAsync` throws unless the whole unit is wrapped in `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`. See `TaxClassEndpoints.SaveWithDefaultAsync`.
4. **`EF.Functions.GreaterThan(ITuple, ITuple)` is the only way to express a keyset over a `Guid` tiebreaker.** `Guid` has no `>` in C# and there is no `Guid.CompareTo` translator, so the hand-expanded form does not compile. It is Npgsql-only, which is fine (Postgres is locked) but worth knowing.
5. **`Assert.DoesNotContain('x', someString, StringComparison)` does not compile** — xUnit binds the char overload to `IAsyncEnumerable<char>`. Pass a string.
6. **Two `HasIndex` calls on the same properties configure the *same* index.** EF keys an index by its property list, so the second call reconfigures the first. Use the named overload — `HasIndex(p => new { … }, "ix_name")` — when you want two indexes over the same columns, which the product name btree and its trigram twin both need.
7. **`git checkout -- <tracked> <untracked>` reverts nothing.** Git errors on the unmatched pathspec and does not process the tracked file either. A newly scaffolded migration is untracked, so this silently leaves everything as it was.
8. **`HasPrincipalKey` must be called before `HasForeignKey`.**
9. **There are no navigation properties, so there is no FK fixup.** Save principals before dependents.
10. **EF blocks a delete client-side when the dependent is loaded in the same context.** A test proving the *database* restricts must use a second scope.
11. **`HasDefaultValue(true)` on a bool legitimately inserted as `false` needs `HasSentinel`.**
12. **An applied migration does not re-run.** A migration adding a tenant table calls `ApplyTenantRowLevelSecurity()`; in `Down` call **`Apply`, not `Remove`**. One that adds no table should call neither — `CatalogSearchIndexes` explains why in its remarks.
13. **`UseXminAsConcurrencyToken()` does not exist in Npgsql 10.**
14. **The scaffolded migration lists an `xmin` column the generated SQL does not contain.** Read the DDL with `dotnet ef migrations script`.
15. **Health-check endpoints have no HTTP method metadata**, so they appear as `ANY health/live`.
16. **A test that asserts 404 passes when it reaches nothing.**
17. **A shared `WebApplicationFactory` has no reset between tests.** `TwoTenantWorld` is read-only; `CatalogSandboxAsync()` is writable but its tests must assert *contains*, never counts.
18. **A superuser bypasses RLS unconditionally.** Isolation assertions run on `pos_app`.
19. **A reset Postgres session variable reads as `''`, not NULL.**
20. **`dotnet run` uses `launchSettings.json` and ignores `ASPNETCORE_URLS`.** Port 5013.
21. **An authorization policy that does not name its scheme evaluates the default one.**
22. **A request can carry both a JWT and a device token.** `AmbientTenantContext.Resolve` refuses to switch tenants.
23. **`WebApplicationFactory.ConfigureAppConfiguration` is applied at `Build()`.** Use `builder.UseSetting(...)`.
24. **Never run the test host in `Development`** — it loads user secrets pointing at local Docker.
25. **`problem+json` carries a per-request `traceId`.** Compare `type`/`title`/`status`, never raw strings.
26. **EF names tables from the `DbSet` property name**, so every configuration calls `ToTable()`.
27. **`[CallerFilePath]` is a lie under CI.** `$env:CI="true"` reproduces CI-only behaviour locally.
28. **`dotnet ef database update` needs the owner connection string passed explicitly.**
29. **Kill stray `Pos.Api` processes before rebuilding** — `Stop-Process`, not `pkill`.
30. **Adding shadcn components may reintroduce the two-React-copies error.**
31. **Test fixtures must not contain the strings a test proves absent.**

## What the falsification pass found

Ten deliberate breaks. Seven went red as expected. **Three did not, and two of those changed what I believe about the tests.**

- **`IgnoreQueryFilters()` on the product list left `CollectionIsolationTests` green.** Not a gap — RLS is underneath, the app connects as `pos_app` (`NOBYPASSRLS`), and the policy returns nothing. This is defence in depth working exactly as `RowLevelSecurityTests.IgnoreQueryFilters_reaches_nothing_once_RLS_is_the_layer_underneath` already says. **Consequence: a cross-tenant read cannot be made to leak through the handler**, so those theories cannot fail that way. They are not vacuous — dropping one row from the list, or answering 200 with a fabricated body, does fail them — but understand what they protect: the handler answering *wrongly*, not the data layer leaking.
- **Writing before the 404 check left `ByIdIsolationTests` green**, for the same reason: the write targeted the victim's row, and two layers stop it. **The failure mode that is actually reachable is collateral damage in the caller's *own* tenant** — which is what `TaxClassCrudTests.A_cross_tenant_promotion_does_not_demote_the_callers_own_default` covers, and which `AssertUntouched` cannot see because it only ever reads tenant A. That test exists because the falsification pass found the manifest comment claiming coverage that did not exist.
- **Dropping `.ThenBy(id)` left the pagination walk green**, because Postgres returned equal-named rows in physical order, which coincided with UUIDv7 id order. The test was passing for the wrong reason. The ordering is now asserted structurally in the `ToQueryString` test instead, and the walk test says in its own comments what it does and does not prove.
- The predicted vacuous `costPrice` pass **did not happen**. Pointing the test at a costless product and removing `JsonIgnore` still fails it — removing the attribute makes the key present *with a null value*, and the assertion is on key presence rather than on the value. Better than designed for.

## Outstanding / deferred

- **Not pushed.** Branch is local; CI has not run. Green here is not green.
- **No visual check of Scalar.** The Chrome extension was not connected. It serves 200s with the correct document URL and the real 3.7 MB bundle, but nobody has looked at the page. Do it before relying on it for 2.5.
- **`TaxClass` has no deactivate and no `IsActive`.** A retired legislated rate stays in the picker forever. Recorded in `API.md`; revisit if it bites.
- **`GET /registers` and `GET /employees/pin-eligible` are now the un-paginated outliers.** Every catalog list returns `{ items, nextCursor, hasMore }`. Phase 6 should decide whether those two follow, at which point paginated becomes the default rather than the exception.
- **`limit=abc` returns a bare 400 with no `problem+json` body** — minimal-API `int?` binding fails before the handler. Pinned by a test. A global `UseStatusCodePages` would fix every bare body at once and belongs in its own commit.
- **`dotnet dev-certs https --trust`** still not run.
- **Playwright browsers not installed** — Phase 4.
- **Production `pos_app` password** — Phase 8.2.
- **A validly signed token is trusted for whatever tenant it names** — accepted and pinned by `ForgedTenancyTests`.
- **CI actions emit a Node 20 deprecation warning.**
- **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc.

## Decided recently

See `DECISIONS.md` → "Resolved 2026-08-01 (during Phase 2.2)" for all seven. The two that reach beyond this milestone:

- **Phase 8.2's managed Postgres must allow `pg_trgm` and `btree_gin`.** Both are trusted extensions in PG 13+, so no superuser is needed — but a host that blocks extensions outright would need the search rewritten.
- **A cursor is a position, not a capability, and is deliberately unsigned.** The reasoning is in `PageCursor`'s XML doc and pinned by a test, so signing it later is a deliberate act.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price.
