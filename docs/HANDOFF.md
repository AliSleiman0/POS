# Session Handoff

**Written:** 2026-08-01 · **Branch:** `phase-2/barcode-lookup` · **Phase 2 complete — start Phase 3.1**

> The catalog is behind the API, scannable, and stock is a ledger. **569 tests green locally** — 119 Core, 77 Data, 373 Api. Release build warning-free with `$env:CI="true"`, `pnpm build` clean. **CI has not run yet: the branch is unpushed and there is no PR.** Do that first, and do not call Phase 2 done until the runner agrees.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`docs/phases/PHASE-3-checkout-sales.md`](phases/PHASE-3-checkout-sales.md) → **§3.1**, the next milestone. Phase 2's exit criteria are all ticked with what proves each.
2. [`DECISIONS.md`](../DECISIONS.md) → **"Resolved 2026-08-01 (during Phase 2.3 and 2.4)"** — nine decisions, three of which Phase 3 inherits directly.
3. [`docs/API.md`](API.md#stock--stock) — the stock contracts, including two endpoints that are documented and deliberately unbuilt.
4. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants.

## State

**569 tests green locally.** Three commits on `phase-2/barcode-lookup`, none pushed:

- `c09d954` Phase 2.3 — barcode lookup, and the manifest arity check
- `49f036d` Phase 2.4 — the stock movement ledger and `IStockLedger`
- `6250bcf` Phase 2.4 — the stock endpoints

The dev database is migrated (`StockLedger`) and seeded. The phase doc's whole verification path was run against it by hand, outside the test host — tax class → category → product → two barcodes → scan each → receive → waste → read the ledger → confirm `OnHand == SUM(quantity)`. It holds. Scalar was also **looked at in a browser** this time, which closes 2.2's outstanding item: it renders, it navigates, and the Products and Stock groups list all ten and all three operations with their summaries.

---

## What 3.1 is

The `Money` type: `decimal` over `numeric(19,4)`, `MidpointRounding.AwayFromZero`, **rounded once** when producing an amount a person pays. Contracts in [`ROADMAP.md`](ROADMAP.md#phase-3--checkout-sales-cash).

### Three things worth knowing before you start

1. **`CatalogRules` is `Money`'s validation-only predecessor and says so in its own remarks.** It answers "would the column hold this exactly?" and owns no arithmetic. When `Money` lands, decide explicitly whether `CatalogRules` delegates to it or stays separate — it is called from three endpoints' validation paths, and quietly having two rounding rules is exactly the failure invariant 3 exists to prevent.
2. **`CatalogRules.IsStorableSignedAmount` was added in 2.4** for signed ledger quantities, with `IsStorableAmount` now defined in terms of it. A quantity is not money, and `Money` should not swallow it.
3. **`IStockLedger` is the port 3.6 has to join.** A sale writes its lines, its tender and its stock movements in one transaction; `StockLedger.RecordAsync` currently opens its own. Extending the port to accept an ambient transaction — or moving the sale to call it inside one — is a design decision 3.6 should take deliberately, not discover.

### What 2.3 and 2.4 give you to build on

- **`IStockLedger`** (`Pos.Core/Inventory/`) — `RecordAsync`, `RebuildOnHandAsync`, `RebuildAllOnHandAsync`. Registered by `AddPosData`, so a host with no HTTP gets a working ledger. It owns `OccurredAt` and `PerformedBy`; callers cannot supply them.
- **`StockRules`** — signed-quantity storability, sign-vs-type consistency, and which types a human may write. Pure and tested without a database.
- **`ConcurrentStockUpdateException` → 409**, raised from `StockItem`'s `xmin` token. The mechanism 3.6's last-unit-sale detection is built on, already proven under two contexts.
- **`IsolationCase.VictimPath`** — for a route with more than one parameter, or one that is not a `Guid`. `UrlFor` throws on an arity mismatch.
- **`CatalogFixture`** now writes barcodes (water gets two, coffee one, the bag none) and stock rows (coffee is below its reorder point on purpose). `SeededCatalog` gained `WaterBarcodeIds` and `StockedProductIds`.

---

## Things that will bite you

New in 2.3/2.4 (1–6); the rest carried forward and still true.

1. **EF renders a tenant query filter on a joined entity as an uncorrelated derived table.** The barcode lookup's SQL therefore contains **three** `SELECT`s for one round trip, and a test counting them asserts the shape of the query filter rather than the cost of the read. Count `FROM <driving table>` and `INNER JOIN` instead — and while you are there, assert the tenant predicate appears once per table, because a join is the one place a tenant-scoped read quietly widens.
2. **A test that only reads the victim tenant cannot see collateral damage in the caller's own.** Carried forward from 2.2's falsification pass and now acted on: `AssertNeitherTenantsWaterGainedACode` reads **both** tenants. New `AssertUntouched` helpers should do the same.
3. **`ScopedDbContext.ForTenant` takes an optional actor id.** Without it `ICurrentActor` is `SystemActor` and `PerformedBy` is null — which looks like a bug in the ledger rather than in the fixture.
4. **xUnit here is 2.9.3, so there is no `TestContext.Current`.** Pass `CancellationToken.None` explicitly where a method demands a token; the EF calls in this suite pass none at all.
5. **A rebuild test is insensitive to the write path being broken.** Deliberately breaking `OnHand +=` failed four tests and left both `RebuildOnHand` tests green — correctly, since a rebuild repairs drift. They protect the repair, not the write.
6. **`Assert.Equal(expected, actual.Order())` needs both sides ordered.** Generated codes go in in whatever order their GUIDs fall.
7. **A stale build can make a correct migration emit wrong SQL.** `Remove-Item -Recurse src/Pos.Data/obj, src/Pos.Data/bin` before debugging generated SQL that disagrees with source you have read.
8. **A model change touches four files** — the configuration, the migration, its `.Designer.cs` and `AppDbContextModelSnapshot.cs`. A partial edit fails the *whole* Data and Api suite at fixture init with `PendingModelChangesWarning`, on an error unrelated to what you were doing.
9. **A retrying execution strategy refuses a user-initiated transaction.** Wrap the unit in `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`. `StockLedger.RecordAsync` and `TaxClassEndpoints.SaveWithDefaultAsync` are the two worked examples.
10. **`EF.Functions.GreaterThan(ITuple, ITuple)` is the only way to express a keyset over a `Guid` tiebreaker**, and `CursorPaging` is ascending-only because of it.
11. **`Assert.DoesNotContain('x', someString, StringComparison)` does not compile** — pass a string.
12. **Two `HasIndex` calls on the same properties configure the *same* index.** Use the named overload.
13. **`git checkout -- <tracked> <untracked>` reverts nothing.**
14. **`HasPrincipalKey` must be called before `HasForeignKey`.**
15. **There are no navigation properties, so there is no FK fixup.** Save principals before dependents. `CatalogFixture` is now four passes.
16. **EF blocks a delete client-side when the dependent is loaded in the same context.** A test proving the *database* restricts must use a second scope.
17. **`HasDefaultValue(true)` on a bool legitimately inserted as `false` needs `HasSentinel`.**
18. **An applied migration does not re-run.** A migration adding a tenant table calls `ApplyTenantRowLevelSecurity()`; in `Down` call **`Apply`, not `Remove`**.
19. **`UseXminAsConcurrencyToken()` does not exist in Npgsql 10**, and the scaffolded migration lists an `xmin` column the generated SQL does not contain. Both are correct.
20. **Health-check endpoints have no HTTP method metadata**, so they appear as `ANY health/live`.
21. **A test that asserts 404 passes when it reaches nothing.** `UrlFor` now throws rather than building an unfillable URL — but the trap is structural, not gone.
22. **A shared `WebApplicationFactory` has no reset between tests.** `TwoTenantWorld` is read-only; `CatalogSandboxAsync()` is writable but its tests must assert *contains*, never counts.
23. **A superuser bypasses RLS unconditionally.** Isolation assertions run on `pos_app`.
24. **A reset Postgres session variable reads as `''`, not NULL.**
25. **`dotnet run` uses `launchSettings.json` and ignores `ASPNETCORE_URLS`.** Port 5013.
26. **An authorization policy that does not name its scheme evaluates the default one.**
27. **A request can carry both a JWT and a device token.** `AmbientTenantContext.Resolve` refuses to switch tenants.
28. **`WebApplicationFactory.ConfigureAppConfiguration` is applied at `Build()`.** Use `builder.UseSetting(...)`.
29. **Never run the test host in `Development`** — it loads user secrets pointing at local Docker.
30. **`problem+json` carries a per-request `traceId`.** Compare `type`/`title`/`status`, never raw strings.
31. **EF names tables from the `DbSet` property name**, so every configuration calls `ToTable()`.
32. **`[CallerFilePath]` is a lie under CI.** `$env:CI="true"` reproduces CI-only behaviour locally.
33. **`dotnet ef database update` needs the owner connection string passed explicitly.**
34. **Kill stray `Pos.Api` processes before rebuilding** — `Stop-Process`, not `pkill`.
35. **Adding shadcn components may reintroduce the two-React-copies error.**
36. **Test fixtures must not contain the strings a test proves absent.**

## What the falsification pass found

Six deliberate breaks, six caught. Two were informative:

- **The by-id isolation row on `GET /stock/{productId}/movements` is not vacuous.** Removing the product-exists check failed its own test *and* the isolation theory: without the check a cross-tenant request answers 200 with an empty ledger rather than 404. Contrast 2.2's finding that the *collection* rows cannot be made to leak — they are protected by RLS underneath — so these two shapes are not equally strong, and it is worth knowing which is which.
- **Breaking the ledger write left both rebuild tests green**, correctly. See trap 5.

The rest went red where expected: dropping the `TrackStock` filter (caught by its own test *and* the collection theory, since the expected ids are the stocked products), serving the scan from the cost-bearing projection (three tests including the SQL one), matching a barcode delete on the barcode id alone, and letting a receipt carry a negative quantity (caught in Core *and* at the endpoint).

## Outstanding / deferred

- **CI has not run on this work.** Push the branch and open the PR; that is the first thing to do.
- **`POST /stock/adjustments` is not idempotent.** Deferred to 3.5 by decision, with the 🔒 removed from `API.md` until then. `StockAdjustmentTests.A_resubmitted_adjustment_writes_a_second_movement` pins today's behaviour and **should be the test 3.5 breaks**.
- **`GET /stock/discrepancies` is documented and unbuilt.** Phase 3.6 creates the flag it would report.
- **`RebuildOnHand` has no route** and no way to run it in production. If Phase 8 wants one, it needs a decision about who may run it.
- **`TaxClass` has no deactivate and no `IsActive`.** A retired legislated rate stays in the picker forever.
- **`GET /registers`, `GET /employees/pin-eligible` and `GET /products/{id}/barcodes` are the un-paginated endpoints.** The third is deliberate (a bounded sub-resource); Phase 6 should decide about the first two.
- **`limit=abc` returns a bare 400 with no `problem+json` body** — minimal-API `int?` binding fails before the handler. A global `UseStatusCodePages` would fix every bare body at once and belongs in its own commit.
- **`dotnet dev-certs https --trust`** still not run.
- **Playwright browsers not installed** — Phase 4.
- **Production `pos_app` password** — Phase 8.2.
- **A validly signed token is trusted for whatever tenant it names** — accepted and pinned by `ForgedTenancyTests`.
- **CI actions emit a Node 20 deprecation warning.**
- **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc.

## Decided recently

See `DECISIONS.md` → "Resolved 2026-08-01 (during Phase 2.3 and 2.4)" for all nine. The three that reach into Phase 3:

- **Idempotency is built once, in 3.5, for sales, voids, refunds and adjustments together.** The stock endpoint is knowingly missing it until then.
- **A lost stock race is a 409 and is never retried inside the ledger**, because re-applying a delta the caller may already have applied is indistinguishable, from inside, from the same request arriving twice. 3.5's keys are what make an automatic retry safe.
- **`IStockLedger` is a narrow precedent, not a repository pattern.** Endpoints still use `AppDbContext` directly.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price.
