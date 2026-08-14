# The API image. Build from the REPOSITORY ROOT, not from this directory:
#
#   docker build -t pos-api -f src/Pos.Api/Dockerfile .
#
# The context has to be the root because Directory.Build.props and
# Directory.Packages.props sit there and apply to every project, and because
# Pos.Api references Pos.Core and Pos.Data as siblings. A context of
# src/Pos.Api/ cannot see any of them.

# ---------------------------------------------------------------- build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# The manifests and project files first, on their own layer. Restore then
# re-runs only when a dependency actually changes; editing a .cs file reuses
# the cached package graph. Versions are managed centrally, so
# Directory.Packages.props is the file that decides what gets restored.
#
# .editorconfig comes with them: EnforceCodeStyleInBuild is on and warnings are
# errors, so it is part of the build contract. Without it the container applies
# stricter analyzer rules than the host and fails on committed migration files.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Pos.Core/Pos.Core.csproj src/Pos.Core/
COPY src/Pos.Data/Pos.Data.csproj src/Pos.Data/
COPY src/Pos.Api/Pos.Api.csproj src/Pos.Api/

# Only the API's graph. Pos.Web, the test projects and tools/Pos.Seed are not
# part of this image and must not drag their dependencies into it.
RUN dotnet restore src/Pos.Api/Pos.Api.csproj

COPY src/Pos.Core/ src/Pos.Core/
COPY src/Pos.Data/ src/Pos.Data/
COPY src/Pos.Api/ src/Pos.Api/

# --no-restore so a restore problem is reported by the step above rather than
# surfacing here. TreatWarningsAsErrors is set in Directory.Build.props, so any
# warning fails the build — same contract as CI.
RUN dotnet publish src/Pos.Api/Pos.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app

# -------------------------------------------------------------- runtime stage
#
# `-extra` is load-bearing and NOT interchangeable with plain `-chiseled`.
#
# Directory.Build.props sets InvariantGlobalization=false deliberately (Phase
# 6.1): every Tenant carries an IANA TimeZoneId, and invariant 8 — UTC in
# storage, the tenant's zone at the edge — depends on ICU being present. The
# plain chiseled image ships neither ICU nor tzdata, so
# TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin") throws and every receipt
# timestamp and business-day boundary fails. It fails ONLY inside the container,
# so CI and every developer machine stay green while production breaks.
#
# `-extra` is the variant carrying ICU and tzdata. If this is ever swapped for
# Alpine it needs an explicit `apk add --no-cache icu-libs tzdata`. Do not set
# InvariantGlobalization anywhere in this file or in a csproj.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app

# The base image already runs as UID 1654 (a non-root `app` user), already sets
# ASPNETCORE_HTTP_PORTS=8080, and has no shell or package manager at all.
# Nothing here escalates that — there is deliberately no USER root.
COPY --from=build /app .

# HTTP only. TLS terminates at the edge (Fly), which forwards plain HTTP with
# X-Forwarded-Proto — see UseForwardedHeaders in Program.cs, without which
# UseHttpsRedirection redirects every request into a loop.
EXPOSE 8080

# No secrets, no connection string, no signing key. Every one of those comes
# from the environment at run time: `fly secrets set`, or -e locally. The app
# refuses to start without a usable Jwt:SigningKey, which is the intended
# behaviour for a container that was deployed without one.
ENTRYPOINT ["dotnet", "Pos.Api.dll"]
