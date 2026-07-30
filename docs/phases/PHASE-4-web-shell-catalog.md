# Phase 4 — Web: Shell, Auth, Catalog

**Goal:** a real user can log in through the browser and manage their product catalog.

**Depends on:** Phases 1–2 (Phase 3 not strictly required, but running after it means the API is stable).

---

## 4.1 App shell

`src/Pos.Web/src/` per the layout in [ARCHITECTURE.md](../ARCHITECTURE.md#frontend-architecture).

- React Router routes + an authenticated layout (nav, current user, register/shift indicator)
- Error boundary + a toast system; a failed API call must never be a silent no-op at a till
- **TanStack Query** for all server state. No global store for data the server owns — a cache with invalidation is the right tool, and hand-rolled sync between a store and the server is where staleness bugs live.
- **Generated API client.** Add an OpenAPI codegen step (`openapi-typescript` + a thin fetch wrapper, or `openapi-fetch`) wired to a `pnpm generate:api` script.

**Hand-written frontend DTOs are forbidden.** They drift from the backend silently: a renamed field compiles fine and produces `undefined` at runtime, which in a money context means a blank total on a receipt.

- A fetch wrapper that attaches the access token, retries once after a 401 via silent refresh, and maps `problem+json` to typed errors branching on `type` (not `detail`)

**Exit criteria**
- [ ] Routing + layout + error boundary
- [ ] `pnpm generate:api` produces types from the running API's OpenAPI doc
- [ ] Zero hand-written request/response interfaces
- [ ] `problem+json` surfaces as readable errors, branching on `type`
- [ ] TanStack Query configured with sensible staleness (catalog can be stale; a shift's state cannot)

## 4.2 Auth flow

`src/auth/`

- Login page (email + password)
- **Token storage**: access token in memory, refresh token in an `httpOnly` cookie if the deployment topology allows it, otherwise storage with the XSS risk documented. `localStorage` for an access token is readable by any injected script — a real risk, and a deliberate trade-off rather than an accident.
- Silent refresh before expiry; a single in-flight refresh shared by concurrent 401s (otherwise five parallel requests trigger five rotations and four of them invalidate the family)
- Route guards from the policy list returned by `GET /auth/me`
- **PIN swap screen** — the register's default idle state. Pick a name from `/employees/pin-eligible`, enter a PIN, session swaps without a full logout. This is the flow a shop actually uses all day.
- Session expiry: a clear re-auth prompt that **preserves an in-progress cart**. Losing a half-built basket because a token expired is the kind of thing that gets a POS replaced.

**Exit criteria**
- [ ] Login, logout, silent refresh
- [ ] Concurrent 401s trigger exactly one refresh, with a test
- [ ] Guards hide controls the user's policies don't grant
- [ ] PIN swap works from an enrolled device
- [ ] Token expiry mid-cart does not lose the cart

## 4.3 Catalog UI

`src/features/catalog/`

- Product list: search, category filter, active/inactive toggle, infinite scroll on the cursor pagination
- Product create/edit form with client validation mirroring the server's — **mirroring, not replacing**. The server remains the authority; client validation is only there to save a round trip.
- Barcode management: add/remove multiple codes per product, with a scan-to-add field so codes are captured from the physical label rather than typed (typed barcodes are a reliable source of typos)
- Category and tax class management
- Stock adjustment form with a **required** reason
- `costPrice` and margin fields rendered only with `CanViewMargins` — and absent from the payload anyway (Phase 2.2), so this is defence in depth, not the control

**Exit criteria**
- [ ] Full product lifecycle through the UI
- [ ] Multiple barcodes per product, scan-to-add works
- [ ] Stock adjustment requires a reason in the UI as well as the API
- [ ] Margin fields hidden for non-Owner
- [ ] Loading, empty and error states exist for every list (an empty catalog is the first thing a new tenant sees)

## 4.4 Tests

- **Vitest** — the fetch wrapper's refresh logic, guards, money formatting, form validation
- **Playwright** — login → create product with barcode → search and find it → adjust stock → see the movement in the ledger
- Playwright runs against the real API + Testcontainers Postgres, not mocks. A mocked E2E test proves the frontend agrees with the mock.

**Exit criteria**
- [ ] `pnpm test` and `pnpm test:e2e` green
- [ ] E2E runs against a real backend
- [ ] CI runs both

---

## Verification

```powershell
docker compose up -d
dotnet run --project src/Pos.Api
pnpm --dir src/Pos.Web dev
```

Then in the browser: log in as Owner → create a tax class, category and product with two barcodes → search → edit the price → adjust stock with a reason → confirm the ledger entry. Log in as a Cashier and confirm catalog editing and cost prices are unavailable.
