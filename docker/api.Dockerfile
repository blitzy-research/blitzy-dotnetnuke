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
# THE EXACT PERMITTED DELTA FROM THE PRESERVED EXAMPLE, AND THE AUTHORITY FOR IT.
# AAP 0.9.3 says the supplied container examples are reproduced verbatim except
# for the name placeholders, while AAP 0.9.6 separately requires this image to
# satisfy non-functional requirements the example does not express, and the
# example's own runtime base cannot start the application unchanged. Both
# statements cannot be literally true at once, so the conflict is resolved
# EXPLICITLY here rather than left for a reader to discover: every instruction
# the example specifies is present and unchanged - the two Alpine bases, the UID
# 1000 non-root account, ASPNETCORE_URLS=http://+:8080, EXPOSE 8080, the wget
# HEALTHCHECK against /health, and ENTRYPOINT ["dotnet", "DnnMigration.Api.dll"]
# - and the delta is confined to this closed, itemised list:
#
#   1. RUN apk add --no-cache icu-libs icu-data-full, paired with
#      DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false. Measured requirement, not a
#      preference: Microsoft.Data.SqlClient fails to open a connection under
#      invariant globalization, so without this pair the container builds, starts
#      and then fails its own health probe. The reason is set out in full above
#      the instruction. The two lines are a pair and neither may be kept alone.
#   2. Build-stage layer ordering that restores project files before copying
#      sources, so a source-only change does not re-run the restore. Behaviour is
#      identical; only cache efficiency differs.
#   3. Ownership handover before the account switch (chown of /app), without
#      which the published output stays root-owned and unreadable to the UID 1000
#      account the example itself mandates.
#
# NOTHING ELSE DIFFERS. In particular the deployment CONFIGURATION the example
# does not cover - the database connection string, the token signing key, the
# permitted browser origin, the trusted-proxy list and the HTTPS-enforcement
# switch - is supplied as environment variables by docker-compose.yml or by the
# orchestrator, never baked into a layer here. That division is what keeps this
# file free of secrets while still letting a deployment be configured, and it is
# recorded in MIGRATION_NOTES.md under the container-example heading.
# =============================================================================

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# The tag tracks the current .NET 8 Alpine SDK image: `8.0-alpine` is a MOVING
# tag that Microsoft re-points at each 8.0.x patch release, so a rebuild months
# apart can compile against a different SDK build. That is accepted rather than
# overlooked - the plan caps this migration at the 8.x line and
# backend/global.json pins the SDK band to 8.0.423 with feature-level roll
# forward, which every image on this tag satisfies. Pin a digest here if a
# byte-reproducible build is ever required; nothing else in this file assumes
# immutability.
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# One fewer network call during the build, and nothing sent from a build agent.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# Manifests first, on their own layer. Restore then re-runs only when a project
# file or a build property actually changes, rather than on every source edit.
#
# Directory.Build.props is not optional here: it is the single source of the
# target framework, language version, nullable setting, warning policy and
# locked-restore policy for all six projects, so a restore without it resolves
# against the wrong framework and ignores the reviewed dependency graph.
# NuGet.Config clears inherited feeds and source-maps every permitted identity;
# global.json fixes the SDK selection rather than accepting whatever the image
# happens to carry.
COPY NuGet.Config ./NuGet.Config
COPY backend/global.json ./
COPY backend/Directory.Build.props ./
COPY backend/DnnMigration.sln ./
COPY backend/src/DnnMigration.Domain/DnnMigration.Domain.csproj src/DnnMigration.Domain/
COPY backend/src/DnnMigration.Domain/packages.lock.json src/DnnMigration.Domain/
COPY backend/src/DnnMigration.Application/DnnMigration.Application.csproj src/DnnMigration.Application/
COPY backend/src/DnnMigration.Application/packages.lock.json src/DnnMigration.Application/
COPY backend/src/DnnMigration.Infrastructure/DnnMigration.Infrastructure.csproj src/DnnMigration.Infrastructure/
COPY backend/src/DnnMigration.Infrastructure/packages.lock.json src/DnnMigration.Infrastructure/
COPY backend/src/DnnMigration.Api/DnnMigration.Api.csproj src/DnnMigration.Api/
COPY backend/src/DnnMigration.Api/packages.lock.json src/DnnMigration.Api/
COPY backend/tests/DnnMigration.UnitTests/DnnMigration.UnitTests.csproj tests/DnnMigration.UnitTests/
COPY backend/tests/DnnMigration.UnitTests/packages.lock.json tests/DnnMigration.UnitTests/
COPY backend/tests/DnnMigration.IntegrationTests/DnnMigration.IntegrationTests.csproj tests/DnnMigration.IntegrationTests/
COPY backend/tests/DnnMigration.IntegrationTests/packages.lock.json tests/DnnMigration.IntegrationTests/

# Solution-wide, which is why all six manifests and lock files are copied above.
# Both controls are explicit: --configfile prevents machine/image settings from
# adding a source, and --locked-mode rejects any direct or transitive graph drift.
RUN dotnet restore --configfile NuGet.Config --locked-mode

# Then the sources. Everything the publish needs is already restored, so the
# publish suppresses restore rather than repeating it.
COPY backend/ ./

# Framework-dependent on purpose, and the two alternatives are rejected for two
# DIFFERENT reasons. A self-contained publish would embed a second copy of the
# runtime that the aspnet runtime stage below already carries, inflating the
# image for no gain; it is a size decision, not a correctness one. TRIMMING is
# the correctness risk: it removes members no static reference reaches, and EF
# Core's model building and System.Text.Json's reflection-based serialisation
# both resolve types and members at run time, so trimming can leave the image
# building fine and failing on first request. Neither is used here.
# The warning policy is deliberately not repeated either: promoting warnings to
# errors is the build gate's job, and duplicating it would turn one reviewable
# failure into two.
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
# that mode - SqlConnection.TryOpen throws
# `System.NotSupportedException: Globalization Invariant Mode is not supported`
# before a socket is opened - so the readiness view answers 503 for the
# container's entire life and every store-backed request fails. Turning invariant
# mode off without installing ICU is worse still: the runtime fails fast at
# startup with "Couldn't find a valid ICU package" and the container never serves
# at all.
#
#   invariant mode left true          -> /health/ready answers 503 for the container's
#                                        entire life and every /api/v1 request that
#                                        touches the store fails, so the image is
#                                        undeployable - and SILENTLY so, because the
#                                        liveness path the probe below reads still
#                                        answers 200 and docker still reports the
#                                        container healthy
#   invariant mode false, ICU absent  -> Environment.FailFast at startup,
#                                        "Couldn't find a valid ICU package",
#                                        container exits 139
#   invariant mode false, ICU present -> /health/ready answers 200 Healthy, every
#                                        store-backed request works, and docker
#                                        reports the container healthy
#
# Only the third state is a deliverable, so it is the one shipped. This is the
# same two-line remedy already analysed in MIGRATION_NOTES.md under
# "The API Alpine image installs ICU and can open SQL Server connections"; it
# is applied here so the composed topology works as delivered instead of
# requiring an undocumented manual step at deploy time. It must precede the
# account switch below, because the package manager needs root. The two
# instructions are a pair: with invariant mode off and no ICU present the
# runtime fails at startup, so never keep one without the other.
RUN apk add --no-cache icu-libs icu-data-full
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Copy, then hand ownership over, then drop privilege - in that order. A publish
# copied as root arrives world-readable, so the process would still START without
# the chown; what the chown buys is predictable ownership rather than access. It
# makes /app writable by the account that runs there - which matters for anything
# the runtime creates beside the assemblies, such as a data-protection key ring -
# and it means a later instruction cannot silently depend on root's file mode.
# Switching the account first would leave the copy owned by root with no way back,
# because only root may chown.
COPY --from=build /app/publish ./
RUN chown -R appuser:appuser /app

# The hardening requirement itself. Creating an account changes nothing on its
# own: without this line the process runs as root, which is the failure mode that
# looks correct in a diff because the adduser instruction is right there above it.
USER appuser

# Bind on every interface, so the reverse proxy container can reach this one by
# its compose service name; a loopback-only binding would refuse those
# connections. Binding every interface INSIDE the container is not the same as
# publishing every interface on the HOST: docker-compose.yml publishes this port
# as 127.0.0.1:8080:8080 precisely so that the cleartext listener is reachable
# from the compose network and the operator's own machine and from nowhere else.
#
# Plain HTTP, because TLS is terminated IN FRONT OF this container - by the SPA
# container's nginx once docker/nginx.tls.conf.example and a certificate are
# mounted, or by an outer proxy - and no certificate is mounted into this image.
# That arrangement is only safe because the application refuses cleartext by
# default outside development and honours the proxy's X-Forwarded-Proto once the
# deployment names that hop as trusted; see
# Api/Extensions/ApplicationBuilderExtensions.cs. The health path is the single
# documented exemption from the redirect, which is what keeps the probe below
# working over plain HTTP on a loopback address.
#
# Port 8080 is above the privileged range, which is exactly what lets the
# unprivileged account bind it - the port and the account choice are one
# decision, not two.
#
# No credential appears here or anywhere else in this image. The database
# connection string and the token signing key are supplied as environment
# variables at run time, by compose or by the run command; baking either into a
# layer would repeat the committed-key mistake this migration exists to end.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

# The probe is wget, and must stay wget. The runtime image ships exactly one HTTP
# client - BusyBox wget at /usr/bin/wget - and no curl binary at all. A probe
# written with curl exits 127 on every attempt, so the container is reported
# unhealthy for its whole life and docker-compose.yml's
# `condition: service_healthy` holds the frontend service back for ever, even
# though both images built perfectly. Host-side verification commands DO use
# curl, and correctly so - they run outside the container, where it exists. Do
# not harmonise the two.
#
# --spider discards the body and only the status code decides; --tries=1 stops
# wget's own retry from hiding a failure; the trailing exit normalises any
# failure to the 1 that docker reads as unhealthy. The start period is generous
# because the first probe otherwise races the database connection this endpoint
# reports on.
#
# /health is anonymous by contract and is the LIVENESS view: it runs every probe
# not tagged `ready`, which today means the process-local audit-delivery probe and
# no dependency probe at all. That is the deliberate choice for a container probe
# in THIS topology - the compose file declares no database service, so the store is
# external and may legitimately be unreachable while the process starts, and a
# probe that depended on it would report the container unhealthy for a reason
# unrelated to whether it can answer and would hold the frontend behind
# `condition: service_healthy` for ever. Readiness stays available at
# /health/ready for an orchestrator that wants to gate traffic on the store.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

# Exec form, so the runtime is process 1 and receives the stop signal directly.
# The shell form would wrap it in /bin/sh, swallow that signal and turn an
# orderly shutdown into a timeout and a kill. No CMD accompanies this: with the
# exec form any CMD would arrive as extra arguments to the runtime.
ENTRYPOINT ["dotnet", "DnnMigration.Api.dll"]
