# =============================================================================
# docker/api.Dockerfile
#
# Multi-stage Linux Alpine image for the DnnMigration ASP.NET Core 8
# Backend-for-Frontend API. Reproduced from the migration plan's preserved
# container example (AAP 0.9.3); the sanctioned substitution is the project
# name placeholder, resolved to DnnMigration, which is what makes the published
# assembly DnnMigration.Api.dll - the exact file the entry point below starts.
# backend/src/DnnMigration.Api/DnnMigration.Api.csproj declares no AssemblyName
# and neither does backend/Directory.Build.props, so the SDK derives that name
# from the project file and nothing else can shift it.
#
# BUILD CONTEXT IS THE REPOSITORY ROOT, not this directory:
#
#     docker build -f docker/api.Dockerfile -t dnnmigration-api .
#
# The trailing dot is the context. docker-compose.yml says the same thing with
# `context: ..` plus `dockerfile: docker/api.Dockerfile`. Every COPY source path
# below is therefore repository-root-relative and prefixed `backend/`; a path
# that climbed out of the context would be rejected outright by the builder.
# The repository-root .dockerignore governs this build and already keeps the
# legacy reference trees, build output and every environment file out of it.
#
# ONE DOCUMENTED DEVIATION from the preserved example: the runtime stage adds
# ICU. The reason is measured, not assumed, and is set out in full above the
# instruction itself.
# =============================================================================

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# The tag is exact and unmoving. A floating or digest-substituted tag would put
# the compiler version outside review, and the plan caps this migration at the
# 8.x line. backend/global.json pins the SDK to 8.0.423 with feature-level roll
# forward, which this image satisfies.
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# One fewer network call during the build, and nothing sent from a build agent.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# Manifests first, on their own layer. Restore then re-runs only when a project
# file or a build property actually changes, rather than on every source edit.
#
# Directory.Build.props is not optional here: it is the single source of the
# target framework, language version, nullable setting, warning policy and
# documentation switch for all six projects, so a restore without it resolves
# against the wrong framework. global.json travels for the same reason - it is
# what makes the SDK selection explicit rather than whatever the image happens
# to carry.
COPY backend/global.json ./
COPY backend/Directory.Build.props ./
COPY backend/DnnMigration.sln ./
COPY backend/src/DnnMigration.Domain/DnnMigration.Domain.csproj src/DnnMigration.Domain/
COPY backend/src/DnnMigration.Application/DnnMigration.Application.csproj src/DnnMigration.Application/
COPY backend/src/DnnMigration.Infrastructure/DnnMigration.Infrastructure.csproj src/DnnMigration.Infrastructure/
COPY backend/src/DnnMigration.Api/DnnMigration.Api.csproj src/DnnMigration.Api/
COPY backend/tests/DnnMigration.UnitTests/DnnMigration.UnitTests.csproj tests/DnnMigration.UnitTests/
COPY backend/tests/DnnMigration.IntegrationTests/DnnMigration.IntegrationTests.csproj tests/DnnMigration.IntegrationTests/

# Solution-wide, which is why all six manifests are copied above. The image
# carries nuget.org as its only source, so the feed set is already as narrow as
# the repository-root NuGet.Config enforces for a checkout.
RUN dotnet restore

# Then the sources. Everything the publish needs is already restored, so the
# publish suppresses restore rather than repeating it.
COPY backend/ ./

# Framework-dependent on purpose. The runtime stage below already carries the
# shared framework, so a self-contained or trimmed publish would both duplicate
# it and break the reflection that EF Core and JSON serialisation depend on.
# The warning policy is deliberately not repeated here either: promoting
# warnings to errors is the build gate's job, and duplicating it would turn one
# reviewable failure into two.
RUN dotnet publish src/DnnMigration.Api/DnnMigration.Api.csproj --configuration Release \
        --no-restore \
        --output /app/publish

# ---------------------------------------------------------------------------
# Runtime stage
# ---------------------------------------------------------------------------
# The ASP.NET Core runtime image, not the SDK and not the plain .NET runtime:
# this application needs the ASP.NET Core shared framework, which only this one
# carries, and shipping the SDK would put a compiler in production.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# Unprivileged account for the process. BusyBox adduser is the only form Alpine
# provides; -D creates the account with no login credential and -u fixes the id
# at 1000 so an operator can reason about file ownership on a mounted path.
# Creating the account is only half of the hardening requirement - the switch
# further down is the other half.
RUN adduser -D -u 1000 appuser

# ICU. This is the one documented deviation from the preserved example, and it
# is here because the delivered container is otherwise inoperable.
#
# mcr.microsoft.com/dotnet/aspnet:8.0-alpine sets
# DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true, because Alpine's musl userland
# ships no ICU. Microsoft.Data.SqlClient 5.x refuses to open a connection in
# that mode: SqlConnection.TryOpen throws
# `System.NotSupportedException: Globalization Invariant Mode is not supported`
# before a socket is opened. Measured, all three states, against a live SQL
# Server:
#
#   invariant mode left true          -> /health answers 503 for the container's
#                                        entire life, so the compose health
#                                        condition never opens and the frontend
#                                        service never starts at all
#   invariant mode false, ICU absent  -> Environment.FailFast at startup,
#                                        "Couldn't find a valid ICU package",
#                                        container exits 139
#   invariant mode false, ICU present -> /health answers 200 Healthy and docker
#                                        reports the container healthy
#
# Only the third state is a deliverable, so it is the one shipped. This is the
# same two-line remedy already analysed in MIGRATION_NOTES.md under
# "The delivered API container cannot open a database connection as built"; it
# is applied here so the composed topology works as delivered instead of
# requiring an undocumented manual step at deploy time. It must precede the
# account switch below, because the package manager needs root. The two
# instructions are a pair: with invariant mode off and no ICU present the
# runtime fails at startup, so never keep one without the other.
RUN apk add --no-cache icu-libs icu-data-full
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Copy, then hand ownership over, then drop privilege - in that order. Switching
# first would leave the published output owned by root and unreadable to the
# account that has to run it.
COPY --from=build /app/publish ./
RUN chown -R appuser:appuser /app

# The hardening requirement itself. Creating an account changes nothing on its
# own: without this line the process runs as root, which is the failure mode
# that looks correct in a diff. Verified by running the image and reading back
# id -u (1000) and id -un (appuser).
USER appuser

# Bind on every interface, so the reverse proxy container can reach this one by
# its compose service name; a loopback-only binding would refuse those
# connections. Plain HTTP, because TLS is terminated in front of this container
# and no certificate is mounted into it. Port 8080 is above the privileged
# range, which is exactly what lets the unprivileged account bind it - the port
# and the account choice are one decision, not two.
#
# No credential appears here or anywhere else in this image. The database
# connection string and the token signing key are supplied as environment
# variables at run time, by compose or by the run command; baking either into a
# layer would repeat the committed-key mistake this migration exists to end.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

# The probe is wget, and must stay wget. The runtime image ships exactly one
# HTTP client - BusyBox wget at /usr/bin/wget - and `command -v` for the
# familiar c-u-r-l transfer tool returns nothing in it, verified by running the
# image. A probe written with that tool exits 127 on every attempt, so the
# container is reported unhealthy for its whole life and docker-compose.yml's
# `condition: service_healthy` holds the frontend service back for ever, even
# though both images built perfectly. The host-side verification commands do
# use that tool, and correctly so - they run outside the container, where it
# exists. Do not harmonise the two.
#
# --spider discards the body and only the status code decides; --tries=1 stops
# wget's own retry from hiding a failure; the trailing exit normalises any
# failure to the 1 that docker reads as unhealthy. The start period is generous
# because the first probe otherwise races the database connection this endpoint
# reports on.
#
# /health is anonymous by contract, asserted in
# backend/src/DnnMigration.Api/Extensions/ApplicationBuilderExtensions.cs. Put
# it behind authentication and this probe receives 401 for ever, with the same
# consequence as an absent probe.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

# Exec form, so the runtime is process 1 and receives the stop signal directly.
# The shell form would wrap it in /bin/sh, swallow that signal and turn an
# orderly shutdown into a timeout and a kill. No CMD accompanies this: with the
# exec form any CMD would arrive as extra arguments to the runtime.
ENTRYPOINT ["dotnet", "DnnMigration.Api.dll"]
