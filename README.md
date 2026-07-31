# POS

Vendor-hosted multi-tenant point-of-sale system. ASP.NET Core API + React PWA, with an Avalonia desktop client later against the same API. Retail first, cash-only checkout in the MVP.

| Read this | For |
|---|---|
| [`DECISIONS.md`](DECISIONS.md) | **Why** the architecture is what it is |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | **Where we are** and what's next |
| [`CLAUDE.md`](CLAUDE.md) | Working conventions and the invariants that must hold |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Layering, multi-tenancy, auth flow |
| [`docs/DATA-MODEL.md`](docs/DATA-MODEL.md) | Entities, money rules, invariants |
| [`docs/API.md`](docs/API.md) | Endpoint contracts |

## Prerequisites

- .NET SDK 10
- Docker Desktop (WSL2 backend on Windows)
- Node 22+ and pnpm 9+ (for `Pos.Web`, from milestone 0.5)
- `dotnet tool install --global dotnet-ef`

## Getting started

```powershell
# 1. Start Postgres + pgAdmin
docker compose up -d

# 2. Point the API at it. Required — the API fails fast at startup without this.
#    User secrets only load in the Development environment.
dotnet user-secrets set "ConnectionStrings:Postgres" `
  "Host=localhost;Port=5432;Database=pos_dev;Username=pos;Password=dev_only_not_a_secret" `
  --project src/Pos.Api

# 3. Apply migrations. NEVER runs automatically at startup — see CLAUDE.md.
dotnet ef database update --project src/Pos.Data --startup-project src/Pos.Api

# 4. Build, test, run
dotnet build          # warnings are errors
dotnet test
dotnet run --project src/Pos.Api
```

Then:

- API: `https://localhost:7xxx/openapi/v1.json` (port from `src/Pos.Api/Properties/launchSettings.json`)
- Health: `/health/live` (process) and `/health/ready` (process + database)
- pgAdmin: <http://localhost:5050> — `dev@localhost` / `dev_only_not_a_secret`

First run over HTTPS also needs the dev certificate trusted. This opens a Windows dialog, so it cannot be scripted:

```powershell
dotnet dev-certs https --trust
```

## Layout

```
src/Pos.Core   — domain logic. Pure C#, no infrastructure. Enforced by a test.
src/Pos.Data   — EF Core: DbContext, configurations, interceptors, migrations
src/Pos.Api    — ASP.NET Core Web API
src/Pos.Web    — Vite + React + TS PWA (milestone 0.5)
tests/         — Pos.Core.Tests, Pos.Data.Tests, Pos.Api.Tests
```

## Credentials in this repo

`docker-compose.yml` contains obvious throwaway passwords. They are safe to commit because they only ever bind to localhost on a development machine, and nothing reuses them: local secrets go in `dotnet user-secrets`, production secrets in env/vault. No real credential, connection string or signing key is ever committed.
