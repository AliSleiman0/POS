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
- [ ] `dotnet build` succeeds with zero warnings
- [ ] `dotnet test` runs (0 tests is fine)
- [ ] Package versions centrally managed

## 0.3 Dependency rules enforced

Project references, and nothing else:

```
Pos.Data → Pos.Core
Pos.Api  → Pos.Data, Pos.Core
Pos.Core → (nothing)
```

Write `tests/Pos.Core.Tests/ArchitectureTests.cs`:

```csharp
[Fact]
public void Core_has_no_infrastructure_dependencies()
{
    var forbidden = new[] { "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore",
                            "Npgsql", "Pos.Data", "Pos.Api" };
    var referenced = typeof(Money).Assembly
        .GetReferencedAssemblies().Select(a => a.Name!);
    Assert.Empty(referenced.Where(r => forbidden.Any(f => r.StartsWith(f))));
}
```

**Exit criteria**
- [ ] `Pos.Core.csproj` has no `PackageReference` outside the BCL
- [ ] The architecture test exists and passes
- [ ] Deliberately adding an EF reference to `Core` makes the test fail (verify this — an assertion that can't fail is not a test)

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
- [ ] `docker compose up -d` → healthy Postgres 17, pgAdmin on `:5050`
- [ ] A minimal `AppDbContext` connects and `dotnet ef migrations add Initial` generates
- [ ] `git grep` finds no connection string or password in tracked files

## 0.5 Web scaffold

```powershell
pnpm create vite src/Pos.Web --template react-ts
cd src/Pos.Web
pnpm add -D tailwindcss @tailwindcss/vite prettier eslint
pnpm dlx shadcn@latest init
```

Add TanStack Query, React Router, Vitest, Playwright. Configure the Vite dev-server proxy to the API to avoid CORS in development.

**Exit criteria**
- [ ] `pnpm build` clean, `pnpm dev` serves
- [ ] Tailwind classes apply; one shadcn component renders
- [ ] `pnpm lint` and `pnpm test` run
- [ ] `strict: true` in `tsconfig.json`

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
