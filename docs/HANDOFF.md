# Session Handoff

**Written:** 2026-08-02 · **Branch:** `main` · **Phases 0–4 complete and merged — start Phase 5.1**

> A cash sale can be priced, tendered and committed exactly once behind three layers of tenant isolation, and a real user can log in through a browser and manage their catalog. **854 .NET · 66 Vitest · 11 Playwright**, all four CI jobs green on `main`.
>
> **There is one known open defect. Fix it before building on top of the auth layer** — see [Fix this first](#fix-this-first).

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

---

## Fix this first

**A network blip signs the till out.**

`AuthProvider` treats *any* failure of `GET /auth/me` during session restore as "this token is no good" and calls `clearTokens()`:

```ts
// src/Pos.Web/src/auth/AuthProvider.tsx
useEffect(() => {
  if (me.isError && statusRef.current === 'loading') {
    clearTokens()
  }
}, [me.isError])
```

A dropped connection is not a bad token. Load the app while the API is unreachable and you are bounced to `/login` with a perfectly good refresh token thrown away. Confirmed by hand: every `/api/v1` request aborted, reload → login screen, not an error state.

`refresh.ts` already gets this right — a `fetch` that *throws* deliberately keeps the token and returns `false` — and the same reasoning was not applied one layer up. The fix is to distinguish a transport failure from a `401`: only clear on a response that actually rejected the credential.

**Why it matters for Phase 5 specifically:** a shop's broadband drops for ten seconds mid-queue, and the till returns to a login screen with a cart on it. The re-auth overlay exists precisely so that cannot happen, and this path routes around it.

No test covers it. Add one to `guards.test.tsx` or a new `AuthProvider.test.tsx`, and make it fail first.

---

## Read first

1. [`docs/phases/PHASE-5-web-register.md`](phases/PHASE-5-web-register.md) — the whole phase. §5.1 is next.
2. [`DECISIONS.md`](../DECISIONS.md) → **"Resolved 2026-08-02 (during Phase 4)"**, eight decisions. The three Phase 5 leans on are the single-flight refresh, the `expired`-vs-`anonymous` session split, and idempotency keys being minted per operation.
3. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants. Invariants 3 (money), 6 (idempotency) and 10 (no blocking dialogs) are all load-bearing in the register.
4. [`docs/API.md`](API.md) — the sales, quote and shift contracts.

## State

`main` is at `dd5c583` (Phase 4 merged via PR #9; Phase 3 via PR #8). CI green on all four jobs: `backend`, `frontend`, `contract`, `e2e`.

No migrations pending. `pos_e2e` is created and migrated by the Playwright run itself; `pos_dev` is what `pnpm dev` talks to.

### Running it

```powershell
docker compose up -d
dotnet tool restore                              # dotnet ef is a local tool, not in the SDK
dotnet run --project tools/Pos.Seed              # prints credentials; safe to re-run
dotnet run --project src/Pos.Api                 # http profile, :5013
pnpm --dir src/Pos.Web dev                       # :5173
```

`corner-shop` / `owner@corner-shop.test` / `Dev-Password-1`. Cashier PIN `4821`, manager `7391`.

The **Front Counter** device token was rotated during a screenshot run and the browser holding it is gone. To use PIN login, re-enrol from `/settings/device` — that captures a fresh token into your browser's `localStorage`.

---

## What Phase 5 inherits

The register is a client of everything below. Getting these wrong is how the phase goes sideways.

1. **The cart must live inside the `RequireAuth` tree.** `RequireAuth` renders `<Outlet/>` plus `<ReauthOverlay/>` on session status `expired`, and only *navigates* on `anonymous`. That asymmetry exists solely so a half-built cart survives a token expiring. Put the cart in React state under the layout — a route that remounts, or a redirect, throws it away and the whole mechanism was pointless.
2. **`newIdempotencyKey()` is minted per operation, not per attempt.** `StockAdjustmentDialog` holds it in `useState` for the life of the dialog. A cart must do the same **from the moment tendering begins**, and Phase 5.5 additionally wants it persisted to `sessionStorage` with the cart so a reload mid-submit recovers. A key generated inside the fetch makes the header decorative and charges the customer twice.
3. **`unwrap(api.POST(...))` throws a `ProblemError`.** Branch on `.is(ErrorType.underTender)` etc., never on `detail` — the backend documents `detail` as reworderable prose. `.fieldErrors` is total and returns `{}` on a non-validation problem, so a form can render it unconditionally.
4. **The client never computes a total.** `POST /sales/quote` takes the same cart as `POST /sales` and both build their `Cart` through one function, so they cannot disagree. Where a running subtotal must appear before the quote returns, use the integer minor-unit helpers in `lib/money.ts` and mark it non-authoritative.
5. **Money is `number | string` in `schema.d.ts`** — .NET describes a `decimal` as `type: ["number","string"]`. `lib/money.ts` is the only place that resolves it (`formatMoney`, `formatQuantity`, `formatRate`, `parseServerDecimal`). Nothing else should touch the raw union.
6. **`LIVE_QUERY_OPTIONS`** (`staleTime: 0`, `refetchOnMount: 'always'`) is the tier for anything with a cash consequence. Spread it into shift and stock queries; the 60s default is for catalog reads.
7. **A sale needs an open shift.** `GET /shifts/current?registerId=` answers **404** when there is none — that is the answer, not an error. `getEnrolledRegisterId()` is where the register id comes from, and `AppLayout`'s indicator already reads it. Opening a drawer is the first action of the day and 5.1 says it must be obvious rather than buried.
8. **`deviceApi` cannot attach a bearer token**, by construction. `POST /auth/pin` and `GET /employees/pin-eligible` are authenticated by the DeviceToken *scheme*; an `Authorization` header would be evaluated by the JWT scheme instead and the second factor would silently vanish. A manager-override flow (5.3) needs this same client.
9. **`ConfirmButton` is the pattern for destructive actions.** Two clicks with a visible state change, disarms on blur. Cart void needs it. `alert`/`confirm`/`prompt` are forbidden outright (invariant 10) — they block the event loop, and a scanner firing behind a blocked page queues its keystrokes and replays them into whatever has focus afterwards.

## Phase 5 — how I would sequence it

The phase doc is the plan; this is only a note on risk.

**5.1–5.3 first (layout, scanner, cart), then stop and drive it.** If the scan-to-cart loop feels wrong, everything after it is built on the wrong foundation. **5.4–5.6** (tender, double-submit, tests) in a second pass.

Two milestones are harder than they look:

- **5.2 scan input** is the one that cannot be finished by tests alone. A wedge scanner is indistinguishable from a very fast typist, so the handler keys on inter-keystroke timing, must stand down while a text input has focus, and must debounce double-fires (scanners genuinely double-fire, and that is two units sold). Playwright can simulate fast keystrokes; "does this fight manual entry in practice" is a judgement only a person at a keyboard makes.
- **5.5 double-submit** needs a real page teardown. Proving one sale results from a rapid double-click is easy; proving *reload mid-submit* recovers involves `sessionStorage` and an actual navigation. The phase doc asks for the falsification too: remove idempotency and confirm the test fails.

The doc's closing line is the real bar and it is not something a session can do alone: **"hand it to someone who has worked a till and watch them use it without instructions."**

---

## Things that will bite you

Frontend (new in Phase 4):

1. **Playwright starts `webServer` BEFORE `globalSetup`.** The migration is chained into the API's own command in `playwright.config.ts` for that reason. A migration in `globalSetup` runs after the API has already failed its readiness check.
2. **`playwright install --with-deps` hangs on Windows** — Linux-only flag. Locally: `pnpm exec playwright install chromium`.
3. **`describe.configure({ mode: 'serial' })` shares the worker, not storage.** Every test gets a fresh context. `enrolDevice(page)` in `e2e/fixtures/actors.ts` is how a spec arranges a device token.
4. **A failed PIN burns one of that user's five lockout attempts.** The wrong-PIN spec uses Sam Cole, not Robin Vale, because Robin Vale is who the authorization specs sign in as and CI runs `retries: 2`.
5. **`e2e/` is its own tsconfig project.** It is deliberately not in `tsconfig.app.json`: app code must not have `process` or `node:fs` in scope, since reaching for them compiles and then fails in a browser.
6. **`schema.d.ts` is generated and committed, and CI fails on drift.** Change a DTO → run the API → `pnpm --dir src/Pos.Web generate:api` → commit. The generate script runs Prettier so the diff is byte-stable.
7. **Every `JsonIgnore(WhenWritingNull)` field arrives as `undefined`, not `null`.** Use `??`, never `=== null`. `String(undefined)` is the six letters "undefined" and it will reach a form field.
8. **.NET describes every numeric as `number | string`** in OpenAPI, including `int32`. Coerce before arithmetic — `Date.now() + '900' * 1000` is `NaN`.
9. **`queryClient.clear()` removes the `/auth/me` query too**, and a query that no longer exists cannot be refetched. `AuthProvider` uses `removeQueries` with a predicate excluding `['auth']`, then `refetchQueries`.
10. **A `.tsx` that also exports a hook breaks Fast Refresh.** `useAuth`/`useToast` live in `authContext.ts`/`toastContext.ts`.
11. **`base-ui` has no `asChild`** — it uses a `render` prop. For a link styled as a button use `buttonVariants({...})` as a `className` on `<Link>`.
12. **The API must run on the `http` profile.** The `https` profile also binds 7091, which switches `UseHttpsRedirection` on and 307s the Vite proxy out of the proxy.

Backend (carried forward, all still true):

13. **`dotnet ef` is a local tool** pinned in `.config/dotnet-tools.json`. `dotnet tool restore` after cloning.
14. **A namespace may not share a name with a type inside it.** The namespace is `Pos.Core.Monetary`.
15. **EF cannot aggregate a value-converted property.** Shift arithmetic reads its sums with `db.Database.SqlQuery<decimal>`, writing the `tenant_id` predicate by hand.
16. **`Properties<decimal>()` does not cover `Money`.** A `Money` property with no `HavePrecision` maps at `numeric(18,2)` and silently truncates.
17. **Minimal-API endpoint filters run *after* model binding.** `Program.cs` calls `EnableBuffering()` before routing.
18. **EF's `SqlQuery` composes into a subquery**, which Postgres refuses for a data-modifying statement. The sale-number upsert goes through raw ADO.
19. **A `SqlQuery<T>` result column must be aliased `"Value"`.**
20. **A model change touches four files** — configuration, migration, `.Designer.cs`, `AppDbContextModelSnapshot.cs`. A partial edit fails the whole Data and Api suite at fixture init.
21. **An applied migration does not re-run**, so a migration adding a tenant table must call `ApplyTenantRowLevelSecurity()` — and in `Down` call **`Apply`, not `Remove`**.
22. **A retrying execution strategy refuses a user-initiated transaction.** Wrap it in `CreateExecutionStrategy().ExecuteAsync(...)`.
23. **There are no navigation properties, so there is no FK fixup.** Save principals before dependents.
24. **xUnit here is 2.9.3** — no `TestContext.Current`.
25. **`InvariantGlobalization` is on**, so `CultureInfo.GetCultureInfo("fr-FR")` throws.
26. **A shared `WebApplicationFactory` has no reset between tests.** Anything asserting an exact count uses `factory.TradingTenantAsync()`.
27. **`[CallerFilePath]` is a lie under CI.** `$env:CI="true"` reproduces CI-only behaviour.
28. **Kill stray `Pos.Api` processes before rebuilding** — `Stop-Process`, not `pkill`. Bites on every rebuild while the API is running.
29. **A superuser bypasses RLS unconditionally.** Isolation assertions run on `pos_app`.

---

## Verified, and not

**Verified in a browser.** 19 screenshots at 1280×900 and tablet width, reviewed by eye — login (empty and error), owner and cashier overviews, catalog list, product detail with barcodes, stock (including negative on-hand in red), the adjustment dialog with both validation errors, tax classes, categories, device enrolment, the PIN screen enrolled and unenrolled, and success and error toasts. All render correctly.

**Driven end to end** by the 11 Playwright specs against a real API and real Postgres.

**Not verified:**

- The **re-auth overlay** over a populated screen. `guards.test.tsx` proves the route stays mounted, which is the property Phase 5 depends on — not that the modal looks right on top of real content. Worth confirming early, since 5.x builds on it.
- The **error boundary**, which only fires on a render-time crash.
- The **stock ledger panel**, rendered but never screenshotted.
- **Nobody who has worked a till has used any of it.**

---

## Outstanding / deferred

- **PR #10** carries this file and a docs correction. Merge it.
- **`auth/policies.ts` is a hand-written list** and can drift from `PolicyCatalog` — `/auth/me` types `policies` as `string[]`, so nothing in the type system catches it. The Playwright authorization specs are the guard. Stated in the file.
- **Cosmetic:** the header shift indicator reads "Not a register" on a non-till browser, which is opaque; a toast overlaps the footer's API badge.
- **Existing dev databases carry stock drift.** `tools/Pos.Seed` used to write stock rows with an opening balance and no matching movement. Fixed for fresh seeds; **do not "fix" an old one with `RebuildOnHand`** — that discards the seeded opening stock. E2E sidesteps it with a fresh `pos_e2e`.
- **`pos_dev` holds leftovers** from earlier walkthroughs ("Walkthrough Cheese", "Walkthrough 28257037"). Harmless; re-seed a fresh database if they bother you.
- **No audit log.** Phase 7.2. Price overrides are recorded on the sale line; a *rejected* attempt is recorded nowhere, which 5.3 will make more visible.
- **`GET`/`PUT /settings` are not built**, so `TaxMode` and `CashRoundingIncrement` are settable only by SQL or the seeder.
- **`GET /sales/{id}/receipt` and `GET /shifts/{id}/report` are documented and unbuilt** — Phase 6.1 and 6.3. 5.4's "receipt action" has nothing to call yet; plan for a stub.
- **`?from=`/`?to=` on `GET /sales` are not implemented**, and `CursorPaging` is ascending-only so `GET /sales` pages oldest-first. Phase 6.
- **Percentage discounts do not exist** — absolute amounts only, so 5.3's line discount is an amount and a percentage is a client computation.
- **`Tender.Method` accepts `Cash` only.**
- **`RebuildOnHand` has no route**; **`TaxClass` has no deactivate**.
- **`limit=abc` returns a bare 400 with no `problem+json` body.** `toProblem` handles it client-side; a global `UseStatusCodePages` would fix every bare body at once and belongs in its own commit.
- **`dotnet dev-certs https --trust`** still not run — use the `http` profile.
- **Production `pos_app` password** — Phase 8.2, along with the deferred httpOnly-cookie decision.
- **A validly signed token is trusted for whatever tenant it names** — accepted and pinned by `ForgedTenancyTests`.
- **CI actions emit a Node 20 deprecation warning**; **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc; **`openapi-typescript` warns about its TypeScript peer** (wants ^5, repo is on 6) — works, noise.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price.
