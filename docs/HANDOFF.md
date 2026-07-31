# Session Handoff

**Written:** 2026-07-31 · **Branch:** `phase-0/foundation` (4 commits, **not pushed**) · **Working tree:** clean

> This file is session state, not durable truth. It goes stale — overwrite it at the end of each session. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`docs/ROADMAP.md`](ROADMAP.md) — what's done, what's next
2. [`docs/phases/PHASE-0-foundation.md`](phases/PHASE-0-foundation.md) — the current phase, including a "gotchas on this machine" section that will save you an hour
3. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants. Do not violate these without a design discussion.

## Where things stand

**Phase 0 is 6/7 done.** Only **0.7 (CI)** is outstanding.

| Milestone | State |
|---|---|
| 0.1 Prerequisites | ✅ .NET 10.0.302, dotnet-ef 10.0.10, WSL 2.7.11, Docker 4.84.0 / engine 29.6.2 |
| 0.2 Solution scaffold | ✅ `Pos.slnx`, 3 src + 3 test projects, build clean with 0 warnings |
| 0.3 Architecture tests | ✅ 3 tests, failure mode verified by deliberately breaking it |
| 0.4 Local Postgres | ✅ Postgres 17.10 + pgAdmin, `Initial` migration applied, health probes verified |
| 0.5 Web scaffold | ✅ Vite 8 / React 19 / TS 6 / Tailwind v4 / shadcn, 11 Vitest tests, dev proxy verified |
| 0.6 Docs | ✅ |
| **0.7 CI** | ⬜ **Next task** |

## Start here

```powershell
docker compose up -d
dotnet build          # must be 0 warnings — warnings are errors
dotnet test           # 3 tests
pnpm --dir src/Pos.Web test    # 11 tests
```

If any of that is not green, fix it before writing anything new.

## Next task: 0.7 CI

`.github/workflows/ci.yml`, two jobs on push and PR:

- **backend** — setup .NET 10 → restore → build → test. Integration tests will use Testcontainers from Phase 1, so no service container is needed; Docker on the runner is enough.
- **frontend** — setup Node 24 + pnpm → install → lint → build → test.

Remote **does** exist: `origin` → `https://github.com/AliSleiman0/POS.git`, currently only `main`. The branch has never been pushed, so CI has never run.

Exit criteria include *"a deliberately broken build fails CI"* — actually verify that. This session found a real bug in the 0.3 architecture test precisely because that kind of negative check was performed, and found a crash-looping container precisely because one wasn't.

Note `.editorconfig` and `.gitattributes`/`.gitignore` already landed early (they were 0.7 items), so 0.7 is now only the workflow file.

## Decisions taken this session that are NOT in the original plan

All are recorded in `DECISIONS.md` or the phase doc, but flagged here because a reader of the plan alone would be surprised:

| Change | Why |
|---|---|
| `Pos.slnx`, not `Pos.sln` | .NET 10 template default. Needs SDK 10 / VS 17.13+ tooling. |
| **oxlint**, not ESLint | Vite template default and much faster. |
| `Microsoft.OpenApi` pinned to **2.11.0** | `NuGetAudit` failed the build: transitive 2.0.0 has high-severity GHSA-v5pm-xwqc-g5wc. **Remove the pin when ASP.NET Core ships a patched dependency.** |
| shadcn as a **devDependency** | It installed itself as a runtime dep. Cannot be removed outright — `init` adds `@import "shadcn/tailwind.css"`. |
| `strict: true` added by hand | The Vite/TS 6 template does not enable it and it defaults to `false`. |
| Style rules scoped off for `**/Migrations/*.cs` and `src/components/ui/**` | Both are generated/vendored code that regeneration overwrites. **Style only** — correctness analysers still apply. |

## Outstanding / deferred — none block 0.7

- **`dotnet dev-certs https --trust`** has not been run. Opens a Windows dialog that cannot be scripted. Needed before first running the API over HTTPS.
- **Playwright browsers not installed.** Run `pnpm exec playwright install --with-deps` in Phase 4 — a ~500 MB download nothing needs before then.
- **No visual verification of the web UI.** The Chrome extension was not connected. Tailwind is confirmed only via utilities in the built CSS and the Button being in the component tree — jsdom does not paint. **Eyeball it before Phase 4 builds real UI on top.**
- **Branch unpushed** — 4 commits exist only on this machine.
- `Pos.Data.Tests` and `Pos.Api.Tests` contain no tests yet. `dotnet test` still exits 0.

## Things that will bite you

Full detail in the phase doc; the short version:

1. **`Get-WindowsOptionalFeature` is broken on this machine** (`Class not registered`, build 26200.8894). It reports features as *absent when present*. Don't diagnose Windows features with it.
2. **`docker compose ps` says "Started" for a crash-looping container.** pgAdmin restarted 12 times unnoticed this session. Verify with `docker inspect -f '{{.State.Status}}, restarts={{.RestartCount}}'` or by curling the port.
3. **Wait-loops must match failure text, not just success text.** A loop grepping only for `Now listening` hung five minutes on a build failure. Silence looks identical to "still starting".
4. **Kill stray `Pos.Api` processes before rebuilding** — they hold a lock on `Pos.Data.dll` and the build fails with `MSB3027`. `pkill -f` did not catch it; use `Stop-Process`.
5. **User secrets load only in the Development environment.** `dotnet run --no-launch-profile` defaults to Production and the connection string is not found.
6. **Adding shadcn components may reintroduce the two-React-copies error** (`Cannot read properties of null (reading 'useRef')`). Fix is `test.server.deps.inline` in `vite.config.ts`, already configured for `@base-ui`.
7. **PATH refresh after installs:** `$env:Path = [Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [Environment]::GetEnvironmentVariable("Path","User")`

## After 0.7: Phase 1 is the load-bearing one

[`PHASE-1-tenancy-auth.md`](phases/PHASE-1-tenancy-auth.md). Read it before writing any entity.

**Do not start Phase 2 until 1.7's isolation tests pass.** Every entity added after Phase 1 inherits tenant scoping automatically; every entity added before it must be audited by hand. A cross-tenant leak means one shop reads another shop's takings.

The two decisions in Phase 1/3 that are **not retrofittable** — worth re-reading `DECISIONS.md` on both:

- **`TaxMode` per tenant** (inclusive vs exclusive). Reinterprets every stored price; must be set at onboarding and refused thereafter.
- **Idempotency keys from Phase 3**, not alongside the offline work. They stop a double-tap double-charging a customer today, and they are the entire mechanism Phase 9's outbox rests on.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10 (no billing code exists), but must be settled before quoting a price to a real customer: "lifetime hosting for a one-time fee" is a liability that grows with every tenant.
