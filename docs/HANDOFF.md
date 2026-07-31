# Session Handoff

**Written:** 2026-07-31 · **Branch:** `main` · Phase 0.7 merged via [PR #2](https://github.com/AliSleiman0/POS/pull/2) · CI green

> This file is session state, not durable truth. It goes stale — overwrite it at the end of each session. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`docs/ROADMAP.md`](ROADMAP.md) — what's done, what's next
2. [`docs/phases/PHASE-1-tenancy-auth.md`](phases/PHASE-1-tenancy-auth.md) — the phase you are about to start
3. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants. Do not violate these without a design discussion.

## Where things stand

**Phase 0 is complete (0.1–0.7).** 0.7 landed this session.

`.github/workflows/ci.yml` runs two jobs on push to any branch and on PR:

- **backend** — .NET 10 → restore → build `Release` → test. NuGet cache keyed on `Directory.Packages.props`. No Postgres service container: Phase 1's integration tests use Testcontainers and the runner provides Docker.
- **frontend** — pnpm + Node 24 → `install --frozen-lockfile` → lint → format check → build → test.

Both exit criteria were met **with evidence**, not assumed:

| Run | Result |
|---|---|
| `30630171848`, `30630509285` | ✅ green |
| `30630365853` | ❌ failed on purpose — backend `CS0219` **as `error`** (proves warnings-are-errors on the runner), frontend `TS2322` (proves `tsc -b` runs in `pnpm build`) |

## First thing to do

**Turn Smart App Control off** (decided 2026-07-31, see below) and confirm `dotnet test` runs locally again. Until that is done, local test results are unavailable and Phase 1's isolation work is a push-and-wait loop.

```powershell
dotnet build && dotnet test    # expect 3 passed, not FileLoadException 0x800711C7
```

## ⚠️ Smart App Control breaks local `dotnet test`

`dotnet test` now fails on this machine with:

```
System.IO.FileLoadException : Could not load file or assembly '...\Pos.Core.dll'.
An Application Control policy has blocked this file. (0x800711C7)
```

**Smart App Control is enforced** — `HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy` → `VerifiedAndReputablePolicyState = 1`. It blocks newly produced unsigned binaries, so any rebuild can break the test run. Confirm with event 3077 in `Microsoft-Windows-CodeIntegrity/Operational`.

This is **local only** — CI is unaffected and green. But Phase 1 is test-heavy (tenant isolation is proven by tests, not by inspection), so running tests locally matters from here on.

**Decision (2026-07-31): turn SAC off.** A dev machine that compiles unsigned binaries every few minutes cannot work under it. Note this is **irreversible — re-enabling requires reinstalling Windows.**

Windows Security → App & browser control → Smart App Control → **Off**. It is a GUI toggle requiring admin; the registry value is protected and reverts, so do not script it. Verify afterwards:

```powershell
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy").VerifiedAndReputablePolicyState  # expect 0
```

If it is still `1` after the toggle, a reboot is needed.

## Next task: Phase 1 — multi-tenancy & auth spine

[`PHASE-1-tenancy-auth.md`](phases/PHASE-1-tenancy-auth.md). Read it before writing any entity. Start at **1.1 tenant primitives**; branch `phase-1/tenancy-auth`.

**Do not start Phase 2 until 1.7's isolation tests pass.** Every entity added after Phase 1 inherits tenant scoping automatically; every entity added before it must be audited by hand. A cross-tenant leak means one shop reads another shop's takings.

Two decisions are **not retrofittable** — re-read `DECISIONS.md` on both before implementing:

- **`TaxMode` per tenant** (inclusive vs exclusive). Reinterprets every stored price; set at onboarding and refused thereafter.
- **Idempotency keys land in Phase 3**, not alongside the offline work. They stop a double-tap double-charging a customer today, and they are the entire mechanism Phase 9's outbox rests on.

Also for Phase 3.6: `EnableRetryOnFailure` is configured, and an execution strategy cannot wrap a user-initiated transaction without going through `CreateExecutionStrategy()`.

## Things that will bite you

New this session (1–3); the rest carried forward. Full detail in the phase doc.

1. **`[CallerFilePath]` is a lie under CI.** `Directory.Build.props` sets `ContinuousIntegrationBuild` when `CI=true`, enabling deterministic source paths — the compiler bakes in `/_/tests/...`, which exists nowhere. This made the architecture test throw on every CI run while passing locally. **Reproduce CI-only build behaviour with `$env:CI="true"` before building** — no push needed.
2. **Smart App Control** — see above.
3. **`pnpm/action-setup` ignores `defaults.run.working-directory`.** It reads `package.json` from the repo root. Any action input needing a path must name it explicitly.
4. **`docker compose ps` says "Running" for a crash-looping container.** Verify with `docker inspect -f '{{.State.Status}} restarts={{.RestartCount}}'` or by curling the port. pgAdmin on `:5050` was re-verified this session (HTTP 200, 0 restarts).
5. **Wait-loops must match failure text, not just success text.** Silence looks identical to "still starting".
6. **Kill stray `Pos.Api` processes before rebuilding** — they lock `Pos.Data.dll` (`MSB3027`). `pkill -f` misses them; use `Stop-Process`.
7. **User secrets load only in Development.** `dotnet run --no-launch-profile` defaults to Production and the connection string is not found.
8. **Adding shadcn components may reintroduce the two-React-copies error.** Fix is `test.server.deps.inline` in `vite.config.ts`, already configured for `@base-ui`.
9. **`Get-WindowsOptionalFeature` is broken here** (`Class not registered`). It reports present features as absent.
10. **PATH refresh after installs:** `$env:Path = [Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [Environment]::GetEnvironmentVariable("Path","User")`

## Machine tuning applied this session

Diagnostics found no pathology — NVMe SSD, no thermal throttling, no paging, 318 GB free. Changes made:

- **`%USERPROFILE%\.wslconfig` created** — WSL2 capped at 4 GB / 4 CPUs with `autoMemoryReclaim=gradual`. Previously uncapped, meaning Docker could claim ~8 GB of 16. Observed at 2.3 GB after the change.
- **Defender exclusions added** (by the user, elevated) for `C:\dev\pos`, the NuGet and pnpm caches, `%LOCALAPPDATA%\Docker`, and `dotnet/node/MSBuild/VBCSCompiler` processes.

Baseline timings after tuning: `dotnet build` ~16 s cold / ~5 s warm, `dotnet test` 7 s, `pnpm build` <1 s, `pnpm test` 4.6 s.

## Outstanding / deferred — none block Phase 1

- **`dotnet dev-certs https --trust`** still not run. Opens a Windows dialog that cannot be scripted. Needed before first running the API over HTTPS.
- **Playwright browsers not installed.** `pnpm exec playwright install --with-deps` in Phase 4 — a ~500 MB download nothing needs before then.
- **No visual verification of the web UI.** jsdom does not paint; Tailwind is confirmed only via built CSS. **Eyeball it before Phase 4 builds real UI on top.**
- **CI actions emit a Node 20 deprecation warning** (`checkout@v4`, `setup-node@v4`, `setup-dotnet@v4`, `cache@v4`, `pnpm/action-setup@v4`). Warnings only, runs are green; bump to `@v5` when the actions ship it.
- **No `global.json`.** CI pins `dotnet-version: 10.0.x`, so a future SDK 10 feature band could differ from local 10.0.302. Add one if that ever diverges.
- `Pos.Data.Tests` and `Pos.Api.Tests` contain no tests yet. `dotnet test` still exits 0.
- The `Initial` migration is intentionally empty — entities arrive in Phase 1.
- **`Microsoft.OpenApi` is still pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc. Remove the pin when ASP.NET Core ships a patched dependency.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10 (no billing code exists), but must be settled before quoting a price to a real customer: "lifetime hosting for a one-time fee" is a liability that grows with every tenant.
