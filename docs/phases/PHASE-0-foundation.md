# Phase 0 — Foundation & Environment

**Goal:** a repo where `dotnet build`, `dotnet test` and `pnpm build` all run clean against a real Postgres, with the rules that keep the codebase honest already enforced.

**Status:** 🔨 In progress — 0.1 and 0.6 done; next is 0.2.

---

## 0.1 Prerequisites — ✅ done (2026-07-30)

Installed toolchain:

| Component | Version |
|---|---|
| .NET SDK | 10.0.302 (ASP.NET Core runtime 10.0.10) |
| `dotnet-ef` | 10.0.10 |
| WSL | 2.7.11.0, kernel 6.18.33.2-2 |
| Docker Desktop | 4.84.0 — engine 29.6.2 (API 1.55), Compose v5.3.1 |
| Node / pnpm / git | v24.18.0 / 9.15.9 / 2.55 (already present) |

**Exit criteria**
- [x] `dotnet --list-sdks` shows 10.0.302
- [x] `docker ps` succeeds, and `docker run --rm hello-world` actually runs a container
- [x] `dotnet tool install --global dotnet-ef` succeeded

### Gotchas on this machine — read before touching Windows features

- **`Enable-WindowsOptionalFeature` / `Get-WindowsOptionalFeature` are broken here.** Both fail with `Class not registered` on build 26200.8894 — the DISM PowerShell module is unregistered. `Get-WindowsOptionalFeature` therefore reports features as absent when they are present. **Don't trust it**; use `dism /online /get-featureinfo` or the tool's own version check (`wsl --version`) instead.
- `wsl --install` bypasses that module and works.
- Pass **`--no-launch`** to `wsl --install` in any automation: without it, the distro setup prompts interactively for a UNIX username and hangs. No Linux distro is installed as a result, which is fine — Docker provisions its own `docker-desktop` WSL2 instance.
- `dotnet dev-certs https --trust` is **still outstanding**. It raises a Windows certificate dialog that cannot be automated. Needed before first running the API over HTTPS (0.4).
- Installers update the machine `PATH`, but already-running shells keep the old one. Refresh with
  `$env:Path = [Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [Environment]::GetEnvironmentVariable("Path","User")`

## 0.2 Solution scaffold

```powershell
dotnet new sln -n Pos
dotnet new classlib -o src/Pos.Core
dotnet new classlib -o src/Pos.Data
dotnet new webapi  -o src/Pos.Api
dotnet new xunit   -o tests/Pos.Core.Tests
dotnet new xunit   -o tests/Pos.Data.Tests
dotnet new xunit   -o tests/Pos.Api.Tests
# add all to sln; wire project references per the dependency rule
```

`Directory.Build.props` at the repo root, so settings apply to every project and cannot be forgotten on a new one:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props` with `ManagePackageVersionsCentrally` — one place to bump a version, no chance of two projects resolving different EF Core versions.

**Why warnings-as-errors from commit one:** a nullable-reference warning in money-handling code is a null-total waiting for a customer. Turning this on later means clearing hundreds of warnings, which nobody does.

**Exit criteria**
- [x] `dotnet build` succeeds with zero warnings
- [x] `dotnet test` runs (exit code 0; `Pos.Data.Tests` and `Pos.Api.Tests` are empty for now, which does not fail the run)
- [x] Package versions centrally managed (`Directory.Packages.props`, `ManagePackageVersionsCentrally` + transitive pinning)

### Deviations from the original plan

- **The solution file is `Pos.slnx`, not `Pos.sln`.** .NET 10's `dotnet new sln` produces the new XML format by default. It diffs far better than the classic format and auto-grouped the projects into `src/` and `tests/` folders. Requires SDK 10 / VS 17.13+ / recent Rider — fine here, but worth knowing if an older tool ever needs to open it.
- **`Microsoft.OpenApi` is pinned to 2.11.0.** `NuGetAudit` (enabled in `Directory.Build.props`) failed the very first build: `Microsoft.AspNetCore.OpenApi` 10.0.10 resolves `Microsoft.OpenApi` 2.0.0, which carries a high-severity advisory (GHSA-v5pm-xwqc-g5wc). Pinned transitively; remove the pin once the ASP.NET Core package ships a patched dependency.
- **`.editorconfig` and `.gitattributes`/`.gitignore` landed early** (they were 0.7 items). `.gitattributes` was needed because staging the docs on Windows triggered LF→CRLF warnings on all 16 files; `.editorconfig` was needed because CA1707 rejects underscores in member names and snake_case test names are deliberate — the rule is disabled for `tests/**` only, so production code still obeys it.

## 0.3 Dependency rules enforced

Project references, and nothing else:

```
Pos.Data → Pos.Core
Pos.Api  → Pos.Data, Pos.Core
Pos.Core → (nothing)
```

`tests/Pos.Core.Tests/ArchitectureTests.cs` holds **two** complementary checks, because either alone has a blind spot:

1. **`Core_csproj_declares_no_infrastructure_references`** — parses `Pos.Core.csproj` for `PackageReference`/`ProjectReference`. Catches a forbidden dependency that is *declared but not yet used*.
2. **`Core_assembly_uses_no_infrastructure_types`** — reflects over `GetReferencedAssemblies()`. Catches one that is *used*, including arriving transitively.

**Why both:** the C# compiler omits references whose types are never touched, so a `PackageReference` to EF Core that Core hasn't called into is invisible to reflection. The csproj check closes that gap.

**Exit criteria**
- [x] `Pos.Core.csproj` has no `PackageReference` outside the BCL — it has no `ItemGroup` at all, only a comment stating the invariant
- [x] The architecture tests exist and pass (3 tests green)
- [x] **Negative verification performed.** Adding `Microsoft.EntityFrameworkCore` to `Pos.Core` makes test 1 fail with a clear message; removing it returns to green.

> **This step earned its keep.** On the first attempt the negative verification *passed* — meaning the guardrail was broken. Cause: `Path.GetFileNameWithoutExtension("Microsoft.EntityFrameworkCore")` returns `"Microsoft"`, because it strips everything after the last dot. Package ids were being mangled into strings that match no forbidden prefix. The path-stripping is only correct for `ProjectReference` paths, so the two reference kinds are now handled separately. A test that cannot fail is worse than no test, because it reads as protection.
>
> Note that the first attempt at the negative check — adding a `ProjectReference` to `Pos.Data` — cannot work: `Data` already references `Core`, so restore dies on a circular dependency before any test runs. Use a forbidden *package* for this verification.

## 0.4 Local Postgres

`docker-compose.yml`:

```yaml
services:
  db:
    image: postgres:17
    environment:
      POSTGRES_DB: pos_dev
      POSTGRES_USER: pos
      POSTGRES_PASSWORD: dev_only_not_a_secret
    ports: ["5432:5432"]
    volumes: [pgdata:/var/lib/postgresql/data]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U pos -d pos_dev"]
      interval: 5s
  pgadmin:
    image: dpage/pgadmin4
    environment:
      PGADMIN_DEFAULT_EMAIL: dev@localhost
      PGADMIN_DEFAULT_PASSWORD: dev_only_not_a_secret
    ports: ["5050:80"]
volumes: { pgdata: }
```

Connection string via `dotnet user-secrets` in `Pos.Api`, never in `appsettings.json`. Add `Npgsql.EntityFrameworkCore.PostgreSQL` to `Pos.Data`.

**Exit criteria**
- [x] `docker compose up -d` → Postgres **17.10** reports healthy, pgAdmin on `:5050`
- [x] `AppDbContext` connects; `migrations add Initial` generated and `database update` applied (`__EFMigrationsHistory` present)
- [x] `git grep` finds no connection string or password in tracked files — the connection string lives only in `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`
- [x] `/health/ready` **verified to fail** (503 `Unhealthy`) with Postgres stopped, and to recover without an API restart
- [x] `/health/live` **verified to stay 200** with Postgres stopped — see below

### Notes

**The `Initial` migration is intentionally empty.** There are no entities yet; they arrive in Phase 1. It is kept rather than deleted because it applies cleanly, creates `__EFMigrationsHistory`, and commits a model-snapshot baseline that records the model-wide conventions — so Phase 1's migration is a clean incremental diff rather than a first-and-huge one.

**`live` deliberately does not probe the database.** If Postgres blips, the orchestrator should stop routing traffic (`ready` fails), not kill and restart the container (`live` fails). A liveness probe that checks a dependency turns a brief database hiccup into a restart storm. Both behaviours were verified by actually stopping the container, not assumed.

**Conventions are set once, model-wide,** in `AppDbContext.ConfigureConventions`: `decimal` → `numeric(19,4)`, `DateTimeOffset` → `timestamptz`, snake_case naming via `UseSnakeCaseNamingConvention()`. A new property therefore cannot be mapped at the provider's default `numeric(18,2)` by omission, which would silently truncate the 3rd and 4th decimals of a unit price.

**Gotcha — user secrets only load in the Development environment.** `dotnet run --no-launch-profile` skips `launchSettings.json`, so `ASPNETCORE_ENVIRONMENT` defaults to Production and the connection string is not found. Set `ASPNETCORE_ENVIRONMENT=Development` explicitly when bypassing the launch profile. The startup guard in `Program.cs` fails fast with the exact command to run, rather than surfacing as a 500 on the first request.

**Gotcha — generated migrations vs. style rules.** EF's scaffolder emits block-scoped namespaces, which `.editorconfig`'s `file_scoped` preference escalated to an error under `TreatWarningsAsErrors`; the first `database update` failed to build. `.editorconfig` now exempts `**/Migrations/*.cs` from **style** rules only. Correctness analysers still apply, and every generated migration must still be read before committing — EF sometimes emits a destructive change for an innocuous model edit.

**`retryOnFailure` and explicit transactions do not mix.** `EnableRetryOnFailure` is on for transient managed-Postgres faults, but an execution strategy cannot wrap a user-initiated transaction without explicit handling. Phase 3.6's sale transaction must therefore go through `CreateExecutionStrategy()`.

## 0.5 Web scaffold

```powershell
pnpm create vite src/Pos.Web --template react-ts
cd src/Pos.Web
pnpm add -D tailwindcss @tailwindcss/vite prettier eslint
pnpm dlx shadcn@latest init
```

Add TanStack Query, React Router, Vitest, Playwright. Configure the Vite dev-server proxy to the API to avoid CORS in development.

**Exit criteria**
- [x] `pnpm build` clean, `pnpm dev` serves (HTTP 200 on `/`)
- [x] Tailwind utilities present in the built CSS (`.flex`, `.rounded-xl`, `.bg-slate-50`, …); the shadcn `Button` renders — asserted by `findByRole('button', { name: 'Re-check' })`
- [x] `pnpm lint` clean, `pnpm test` green (11 tests), `pnpm format:check` clean
- [x] `strict: true` — **and it was missing**, see below
- [x] **Dev proxy verified end to end**: `curl localhost:5173/health/ready` → `Healthy`, proving browser → Vite → API → Postgres

> **Not verified: visual rendering.** The Chrome extension was not connected, so no screenshot was taken. Tailwind is confirmed only by the utilities being emitted into the built CSS and the Button being in the component tree — jsdom does not paint. Worth an eyeball before Phase 4 builds real UI on top.

### What the template gave us, and what had to be corrected

The .NET 10 / Vite 8 / TS 6 templates have moved on from the plan's assumptions:

- **`strict` is NOT enabled** by the current Vite + TS 6 template — it is absent from both `tsconfig.app.json` and `tsconfig.node.json`, and `strict` defaults to `false`. `CLAUDE.md` requires it. Added, along with `noUncheckedIndexedAccess`, which matters specifically here: `cart[i]` and `lines[i]` are undefined-able and an unchecked index in checkout code is a blank total on a receipt.
- **`baseUrl` is deprecated in TypeScript 6** and errors the build. `paths` now resolves relative to the config file, so `baseUrl` was simply dropped.
- **The template ships `oxlint`, not ESLint.** Kept — it is the template default and far faster. The plan said ESLint; this is a deviation, not an omission.
- **shadcn/ui's `init` failed twice before working.** It resolves the import alias from the **root** `tsconfig.json`, which in a project-references setup contains only `references` and no `compilerOptions` — so `@/*` was unresolvable and it aborted after writing `components.json`. Fixed by adding a `paths` entry to the root config purely for tooling (it compiles nothing).
- **shadcn installed itself as a runtime `dependency`.** Moved to `devDependencies` — but it cannot simply be removed, because `init` adds `@import "shadcn/tailwind.css"` to `index.css`, which needs the package present at build time.
- **shadcn's current style is `base-nova`**, built on `@base-ui/react` rather than Radix.

### Gotcha — two React copies under pnpm

Component tests failed with `TypeError: Cannot read properties of null (reading 'useRef')`. Cause: `@base-ui/react` resolved React's **CJS** build while the app used ESM, giving two React instances with separate hook dispatchers. `resolve.dedupe` alone did not fix it; `test.server.deps.inline: [/@base-ui/]` did. Expect this again when adding further shadcn components that pull new Base UI packages.

### Gotcha — a wait-loop that hung for five minutes

Starting the API with `until grep -qE "Now listening|Unhandled"` hung, because the build had actually failed with `MSB3027` (a stale `Pos.Api` process from the 0.4 verification held a file lock on `Pos.Data.dll`) and the pattern matched no failure text. **Any wait-loop must match the failure signatures too, or silence looks identical to "still starting".** Kill stray `Pos.Api` processes before rebuilding — `Stop-Process` on the actual process, since `pkill -f` did not catch it.

### Deferred deliberately

`pnpm exec playwright install --with-deps` has **not** been run — a ~500 MB browser download that nothing needs until Phase 4 writes the first E2E spec. `playwright.config.ts` is in place and `pnpm test:e2e` is wired.

## 0.6 Docs — ✅ done

- [x] `docs/ROADMAP.md`, `ARCHITECTURE.md`, `DATA-MODEL.md`, `API.md`
- [x] `docs/phases/PHASE-0..9`
- [x] `CLAUDE.md`
- [x] `DECISIONS.md` updated with the resolved questions

## 0.7 CI

`.github/workflows/ci.yml` — two jobs on push and PR:

- **backend**: setup .NET 10 → restore → build (warnings are errors) → test. Integration tests use Testcontainers, so no service container is declared; Docker on the runner is enough.
- **frontend**: setup Node 24 + pnpm → install → lint → build → test.

Also `.editorconfig` (C# + TS formatting), `.gitignore` (`bin/`, `obj/`, `node_modules/`, `.env*`, `*.user`), `.gitattributes` (`* text=auto eol=lf`, so a Windows checkout doesn't churn line endings for everyone else).

**Exit criteria**
- [ ] CI green on a pushed branch
- [ ] A deliberately broken build fails CI (verify, don't assume)

---

## Verification

```powershell
docker compose up -d
dotnet build            # zero warnings
dotnet test             # architecture test passes
pnpm --dir src/Pos.Web build
pnpm --dir src/Pos.Web test
```

## Notes for the next phase

Phase 1 is load-bearing: it decides whether tenant isolation is structural or a habit. Do not start Phase 2 entities before 1.7's isolation tests pass — retrofitting isolation onto existing entities is how leaks are introduced.
