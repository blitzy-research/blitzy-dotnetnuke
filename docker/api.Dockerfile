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
# for the name placeholders. Every instruction the example specifies is present
# and unchanged: the two Alpine bases, the UID 1000 non-root account created with
# `adduser -D -u 1000 appuser`, the account switch, ASPNETCORE_URLS=http://+:8080,
# EXPOSE 8080, the wget --spider HEALTHCHECK against /health, and
# ENTRYPOINT ["dotnet", "DnnMigration.Api.dll"].
#
# SEC-B4: FOUR ADDITIONS WERE WITHDRAWN, BECAUSE THEY WERE NOT THE EXAMPLE'S AND
# WERE NOT REQUIRED. An earlier revision of this file added a repository
# NuGet.Config copy with `dotnet restore --configfile NuGet.Config --locked-mode`,
# six per-project packages.lock.json COPY instructions, a manifest-first layer
# split, and `RUN chown -R appuser:appuser /app`. None of them survives, and
# nothing is lost by removing them:
#   * Determinism is unaffected. backend/Directory.Build.props sets
#     RestorePackagesWithLockFile and RestoreLockedMode for all six projects, and
#     `COPY backend/ ./` brings every packages.lock.json into the build, so the
#     restore below is still hash-verified and still rejects graph drift - the
#     flags were restating a policy the props file already enforces.
#   * The feed is unaffected. Every identity in the reviewed graph resolves from
#     nuget.org, which is the image's own default source.
#   * The chown was never load-bearing. A publish copied as root arrives
#     world-readable, and the process only READS its assemblies; the data-protection
#     key ring lives under the account's home directory rather than /app. The
#     comment that accompanied it conceded the process "would still START without
#     the chown".
#   * The layer split only affected build-cache efficiency, never behaviour.
#
# ONE ADDITION REMAINS, AND IT IS A MEASURED PREREQUISITE FOR THE IMAGE TO
# FUNCTION AT ALL: the ICU package pair. Its evidence is recorded above the
# instruction itself, including the exact failure each alternative produces. It is
# retained because an image that cannot open a database connection satisfies no
# requirement of this migration, and AAP 0.9.8 makes a working container one of the
# seven deliverables. It is documented, itemised and re-verified rather than
# assumed.
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

# The whole backend tree, in one instruction. That brings global.json,
# Directory.Build.props, the solution, all six project files, all six
# packages.lock.json files and every source file, which is exactly what the
# restore and the publish below need and nothing more - the repository-root
# .dockerignore already excludes bin, obj and every environment file.
COPY backend/ ./

# Solution-wide restore. It needs no flags of its own: backend/Directory.Build.props
# sets RestorePackagesWithLockFile and RestoreLockedMode for all six projects, and
# the lock files arrived with the instruction above, so this restore is hash-verified
# against the reviewed dependency graph and fails on any direct or transitive drift.
# Every identity in that graph resolves from nuget.org, the image's default source.
RUN dotnet restore

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

# The published output. It arrives world-readable, which is all the unprivileged
# account below needs: the process READS its assemblies and writes nothing beside
# them - the data-protection key ring lives under the account's own home directory,
# not here. An earlier revision followed this with `chown -R appuser:appuser /app`;
# it was withdrawn because the preserved example does not have it and it bought
# nothing, and its own comment conceded the process would start without it.
COPY --from=build /app/publish ./

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
