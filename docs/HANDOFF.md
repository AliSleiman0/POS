# Session Handoff

**Written:** 2026-08-02 · **Branch:** `phase-4/web-shell-catalog` · **Phase 4 complete — start Phase 5.1**

> A real user logs in through a browser and manages their catalog. **854 .NET tests, 66 Vitest, 11 Playwright** — the last against a real API and a real Postgres in a dedicated `pos_e2e` database, not mocks. **All four CI jobs green** (run 30747058123): `backend`, `frontend`, `contract` and `e2e`.
>
> **Generating the API client found two contract gaps and the E2E suite found two real bugs.** None of the four was visible from the backend, and all four are now pinned by tests. They are the most useful thing in this document — see [What the client and the browser found](#what-the-client-and-the-browser-found).

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`DECISIONS.md`](../DECISIONS.md) → **"Resolved 2026-08-02 (during Phase 4)"**. Eight decisions; the ones Phase 5 inherits are under [What Phase 5 inherits](#what-phase-5-inherits).
2. [`docs/phases/PHASE-5-web-register.md`](phases/PHASE-5-web-register.md) → **§5.1**, the next milestone.
3. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants.

## State

Four commits on `phase-4/web-shell-catalog`, branched from `phase-3/checkout-sales`:

```
d373824  4.1–4.3  app shell, generated client, auth flow and catalog UI
547f108  4.4      tests, CI jobs, and the two bugs the suite found
60c7d53  4.4      pin dotnet-ef in a tool manifest so CI can run the migration
af91a39  4.4      restore and build before the e2e suite runs
```

The last two are CI-only failures worth knowing about, because both were
invisible locally — this machine had state the runner does not:

- **`dotnet ef` is a tool, not part of the SDK.** It was installed globally here.
  Now pinned in `.config/dotnet-tools.json`; run `dotnet tool restore` after cloning.
- **The `e2e` job had never restored NuGet packages.** Both surfaced as
  "Process from config.webServer was not able to start", which names nothing —
  the job now builds up front so a compile failure is reported as a build failure.

**Phase 3's CI was already green** — PR #8, both the push and pull_request runs. The previous handoff's "CI has not run" was stale by the time this session started.

**PR #8 is still open against `main`, and this branch is stacked on it.** Merge order matters.

No new migrations. `pos_e2e` is created and migrated by the Playwright run itself.

---

## What the client and the browser found

The interesting part of the phase. Each of these compiled, passed every existing test, and was wrong.

### 1. Five `/auth` endpoints had no response schema

They returned `Task<IResult>`. OpenAPI infers the response schema from the declared return type, so the document described them as having **no response content at all**, and `openapi-typescript` typed the login and `/me` bodies as `never` — the two calls every client makes first.

Nothing on the backend noticed, because the endpoints returned exactly the right JSON. It surfaces only as a frontend that cannot name what it is receiving, at which point the pressure is to hand-write the DTO — which CLAUDE.md forbids for precisely this reason.

Fixed by giving them `Results<…>` unions. `ResponseSchemaContractTests` now checks **every** endpoint.

### 2. `Idempotency-Key` was enforced but undocumented

Read by an endpoint filter rather than bound as a parameter, so nothing told OpenAPI it existed. `openapi-fetch` types `params.header` as `undefined` for an operation declaring no headers — so the generated client had **no typed way to send the one header that makes a retry safe** on six money- and stock-moving endpoints.

Fixed by an operation transformer driven off the same `IdempotentEndpointMetadata` marker `RequireIdempotency` attaches, so the document cannot claim the header where it is not enforced or omit it where it is. `IdempotencyDocumentTests` asserts both directions.

### 3. Concurrent logins for one account returned 500

`LoginAsync` stamped `LastLoginAt` via `UserManager.UpdateAsync`, which checks Identity's `ConcurrencyStamp` and — this is what made it invisible — **returns a failed `IdentityResult` rather than throwing**. Nothing checked the result. The user entity stayed `Modified` in the tracker, so the next `SaveChangesAsync` (inserting the refresh token) retried it and threw `DbUpdateConcurrencyException`. The caller was refused *after* their password had been verified.

Two tills sharing an owner login, or one person double-clicking Sign in, would have reproduced it in a shop. Found by Playwright running its workers in parallel; no sequential test could have caught it. Fixed with `ExecuteUpdateAsync`, pinned by `ConcurrentLoginTests`, and falsified (both tests go red on the old code).

### 4. `String(undefined)` in the product form

`costPrice` is **omitted** from the response for a caller without `CanViewMargins` — invariant 7, not nulled — so it arrives as `undefined`. The form seeded `String(product.costPrice)`, which is the literal six letters `"undefined"`, and saving then failed validation on a field nobody had touched.

The lesson generalises: **every `JsonIgnore(WhenWritingNull)` field arrives as `undefined`, not `null`.** `??`, never `=== null`.

## What Phase 5 inherits

The register screen is a client of all of this. These shape it:

1. **`unwrap(api.GET(...))` throws a `ProblemError`.** Branch on `.is(ErrorType.x)`, never on `detail` — the backend documents `detail` as reworderable prose. `.fieldErrors` is total and returns `{}` on a non-validation problem, so a form can render it unconditionally.
2. **`newIdempotencyKey()` is minted per *operation*, not per attempt.** `StockAdjustmentDialog` holds it in `useState` for the life of the dialog; a cart must do the same from first tender. A key generated inside the fetch makes the header decorative.
3. **Session expiry does not unmount the route tree.** `RequireAuth` renders `<Outlet/>` plus `<ReauthOverlay/>` on status `expired`, and only navigates on `anonymous`. **The cart is safe as long as it lives inside that tree** — put it in React state under the layout, not in a route that remounts.
4. **`LIVE_QUERY_OPTIONS`** (`staleTime: 0`, `refetchOnMount: 'always'`) is the tier for anything with a cash consequence. Spread it into shift and stock queries; the 60s default is for catalog reads.
5. **Money is `number | string` in `schema.d.ts`**, because .NET describes a `decimal` as `type: ["number","string"]`. `lib/money.ts` is the only place that resolves it — `formatMoney`, `formatQuantity`, `formatRate`, `parseServerDecimal`. Nothing else touches the raw union.
6. **`deviceApi` cannot attach a bearer token.** `POST /auth/pin` and `GET /employees/pin-eligible` are authenticated by the DeviceToken *scheme*; sending `Authorization` instead would be evaluated by the JWT scheme and the second factor would silently vanish. Keeping them on a separate client makes that mistake unavailable.
7. **A sale needs an open shift.** `GET /shifts/current?registerId=` answers 404 when there is none — `AppLayout`'s indicator already reads it, and `getEnrolledRegisterId()` is where the register id comes from.

## Things that will bite you

New in Phase 4 (1–13); the rest carried forward and still true.

1. **Playwright starts `webServer` entries BEFORE `globalSetup`.** The migration is therefore chained into the API's own command in `playwright.config.ts`, not done in setup — a migration in `globalSetup` runs after the API has already failed its readiness check against a database that does not exist.
2. **`playwright install --with-deps` hangs on Windows.** It is a Linux-only flag. Locally: `pnpm exec playwright install chromium`. CI keeps `--with-deps`.
3. **`describe.configure({ mode: 'serial' })` shares the worker, not storage.** Every test gets a fresh browser context, so `localStorage` does not carry between them. `enrolDevice(page)` in `e2e/fixtures/actors.ts` is how a spec arranges a device token.
4. **A failed PIN burns one of that user's five lockout attempts.** The wrong-PIN spec deliberately uses Sam Cole, not Robin Vale, because Robin Vale is who the authorization specs sign in as and CI runs with `retries: 2`.
5. **`tsc -b` covers `e2e/` through its own project** (`tsconfig.e2e.json`). It is deliberately *not* in `tsconfig.app.json`: app code must not have `process` or `node:fs` in scope, since reaching for them there compiles and then fails in a browser.
6. **`schema.d.ts` is generated and committed, and CI fails on drift.** Change a DTO → run the API → `pnpm --dir src/Pos.Web generate:api` → commit. The generate script runs Prettier so the diff is byte-stable.
7. **The generate script needs the API running** on 5013 with the `http` profile. The `https` profile also binds 7091, which switches `UseHttpsRedirection` on and 307s the Vite proxy out of the proxy.
8. **The Vite dev proxy pointed at 5199 from Phase 0.5 until this branch.** The API is on 5013. Nothing but the health probe had used it.
9. **`.NET` describes every numeric as `number | string`** in OpenAPI, including `int32`. `expiresIn` is coerced in `refresh.ts`, because `Date.now() + '900' * 1000` is `NaN` and the session then looks permanently expired.
10. **`queryClient.clear()` removes the `/auth/me` query too**, and a query that no longer exists cannot be refetched — a PIN swap then left an owner's name on screen with a cashier's token behind it. `AuthProvider` uses `removeQueries` with a predicate excluding `['auth']`, then `refetchQueries`.
11. **A component file that also exports a hook breaks Fast Refresh.** `useAuth` and `useToast` live in `authContext.ts` / `toastContext.ts` for that reason; the `.tsx` files export components only.
12. **oxlint's `exhaustive-deps` was right about `policies`.** `me.data?.user.policies ?? []` allocates a new array every render, so the context value was new every render and re-rendered every screen reading it. It is memoised.
13. **`base-ui` has no `asChild`.** It uses a `render` prop. For a link styled as a button, use `buttonVariants({ ... })` as a `className` on the `<Link>`.
14. **A namespace may not share a name with a type inside it.** The namespace is `Pos.Core.Monetary`.
15. **EF cannot aggregate a value-converted property.** Shift arithmetic reads its sums with `db.Database.SqlQuery<decimal>`, writing the `tenant_id` predicate by hand.
16. **`Properties<decimal>()` does not cover `Money`.** A `Money` property with no `HavePrecision` maps at `numeric(18,2)` and silently truncates.
17. **Minimal-API endpoint filters run *after* model binding.** `Program.cs` calls `EnableBuffering()` before routing.
18. **EF's `SqlQuery` composes into a subquery**, and Postgres will not accept a data-modifying statement there. The sale-number upsert goes through raw ADO.
19. **`ON CONFLICT (tenant_id)` needs a unique constraint on `(tenant_id)` alone.**
20. **A `SqlQuery<T>` result column must be aliased `"Value"`.**
21. **`CatalogFixture` seeds stock rows with no matching movements**, so `OnHand == SUM(movements)` is false in the Api test fixture. Assert `openingBalance + ledger` there.
22. **A model change touches four files** — configuration, migration, `.Designer.cs` and `AppDbContextModelSnapshot.cs`. A partial edit fails the whole Data and Api suite at fixture init.
23. **An applied migration does not re-run**, so a migration adding a tenant table must call `ApplyTenantRowLevelSecurity()` — and in `Down` call **`Apply`, not `Remove`**.
24. **A retrying execution strategy refuses a user-initiated transaction.** Wrap it in `CreateExecutionStrategy().ExecuteAsync(...)`.
25. **There are no navigation properties, so there is no FK fixup.** Save principals before dependents.
26. **A stale build can make a correct migration emit wrong SQL.** Remove `obj`/`bin` before debugging generated SQL.
27. **xUnit here is 2.9.3**, so there is no `TestContext.Current`.
28. **`InvariantGlobalization` is on**, so `CultureInfo.GetCultureInfo("fr-FR")` throws.
29. **A shared `WebApplicationFactory` has no reset between tests.** Anything asserting an exact count uses `factory.TradingTenantAsync()`.
30. **`problem+json` carries a per-request `traceId`.** Compare `type`/`title`/`status`.
31. **`[CallerFilePath]` is a lie under CI.** `$env:CI="true"` reproduces CI-only behaviour.
32. **`dotnet ef database update` needs the owner connection string passed explicitly.**
33. **Kill stray `Pos.Api` processes before rebuilding** — `Stop-Process`, not `pkill`. This bites on every rebuild while the API is running.
34. **A superuser bypasses RLS unconditionally.** Isolation assertions run on `pos_app`.

## Verified in a browser

**Driven by Playwright, and separately *looked at*.** Those are different things and the
distinction matters: Playwright asserts on the accessibility tree and on text, so a screen could
render with white-on-white text, a zero-height container or overlapping panels and all eleven
specs would still pass. So twelve screenshots were captured at 1280×900 and at tablet width and
reviewed by eye.

Driven end to end by `e2e/`:

- **The full owner flow** — log in → tax class, category and product → two barcodes → search →
  edit the price → adjust stock with a reason → the movement in the ledger.
- **A Cashier** sees no catalog navigation, is refused in place at `/catalog`, and has no
  cost-price field.
- **Device enrolment and PIN swap** — paste the seeder's token at `/settings/device`, then swap
  to Robin Vale with PIN `4821`. The navigation changes without a reload.

Confirmed by eye: login (empty, and with the error state), the catalog list, product detail with
barcodes, stock, the adjustment dialog including both validation errors at once, tax classes,
categories, the Cashier's overview, and login at tablet width. All render correctly.

### Not verified

- **Failure states have never been seen rendered.** The API being killed mid-session, a mutation
  failing, and the re-auth overlay appearing over a populated screen are all covered by unit or
  component tests, but nobody has watched them happen in a browser. The re-auth overlay in
  particular is the one Phase 5 depends on, and `guards.test.tsx` proves the route stays mounted
  — not that the modal looks right on top of a real screen.
- **Nobody who has worked a till has used any of it.** Phase 5's doc asks for that and it is the
  right bar; the catalog screens have not had it either.

## Outstanding / deferred

- **Secret scanning flagged the E2E connection string**, correctly by pattern. The credentials are the documented Compose throwaways, but a literal `Username=…;Password=…` in a `.ts` file is against CLAUDE.md's own rule regardless of the value. Now: both passwords come from the environment (`POSTGRES_PASSWORD`, `POSTGRES_APP_PASSWORD`) with the Compose default as the local fallback; the JWT signing keys for the E2E and contract runs are **generated per run** rather than written down; and CI creates `pos_app` with a random password masked out of the log. The one literal left is the Postgres *service container's* password in `ci.yml`, which cannot be generated — service `env:` is evaluated before any step runs.
- **The `e2e` CI job creates the `pos_app` role in a step**, because a service container cannot run `docker/postgres-init/01-app-role.sh` — that needs a mounted entrypoint directory.
- **The `e2e` job builds in Debug**, matching what `dotnet run` and `dotnet ef` use. The `backend` job builds Release separately; the two do not share a cache beyond NuGet.
- **`auth/policies.ts` is a hand-written list** and can drift from `PolicyCatalog`. Nothing in the type system catches it; the Playwright authorization specs do, which is stated in the file.
- **Existing dev databases carry stock drift.** `tools/Pos.Seed` used to write stock rows with an opening balance and no matching movement. Fixed for fresh seeds; an existing one needs the opening receipts adding by hand. **Do not "fix" it with `RebuildOnHand`** — that discards the seeded opening stock. E2E sidesteps it with a fresh `pos_e2e`.
- **No audit log.** Phase 7.2. Price overrides are recorded on the sale line; a *rejected* attempt is recorded nowhere. `docs/API.md` now says so.
- **`GET /settings` and `PUT /settings` are not built**, so `TaxMode` and `CashRoundingIncrement` are settable only by SQL or the seeder.
- **`GET /sales/{id}/receipt` and `GET /shifts/{id}/report` are documented and unbuilt** — Phase 6.1 and 6.3.
- **`?from=`/`?to=` on `GET /sales` are not implemented.** Phase 6.
- **`CursorPaging` is ascending-only**, so `GET /sales` pages oldest-first. Phase 6 has the screen to argue from.
- **Percentage discounts do not exist.** Absolute amounts only.
- **`Tender.Method` accepts `Cash` only.**
- **`RebuildOnHand` still has no route.**
- **`TaxClass` has no deactivate and no `IsActive`** — the UI reflects that rather than offering a button that would 404.
- **`limit=abc` returns a bare 400 with no `problem+json` body.** `toProblem` handles it on the client; a global `UseStatusCodePages` would fix every bare body at once and belongs in its own commit.
- **`dotnet dev-certs https --trust`** still not run. Use the `http` profile.
- **Production `pos_app` password** — Phase 8.2.
- **A validly signed token is trusted for whatever tenant it names** — accepted and pinned by `ForgedTenancyTests`.
- **CI actions emit a Node 20 deprecation warning.**
- **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc.
- **`openapi-typescript` warns about its TypeScript peer** (wants ^5, the repo is on 6). It works; the warning is noise.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price.
