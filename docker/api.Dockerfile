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
# EXPOSE 8080, the wget --spider HEALTHCHECK (retargeted to /health/ready per
# SEC-F10, see the directive below), and
# ENTRYPOINT ["dotnet", "DnnMigration.Api.dll"].
#
# THREE FORMER ADDITIONS STAY WITHDRAWN, AND ONE HAS RETURNED FOR A REASON THE
# WITHDRAWAL DID NOT ANSWER.
#   * Still withdrawn - `--locked-mode` on the restore. It restated a policy that has
#     one home: backend/Directory.Build.props sets RestorePackagesWithLockFile and
#     RestoreLockedMode for all six projects and `COPY backend/ ./` brings every
#     packages.lock.json into the build, so the restore below is hash-verified and
#     rejects graph drift without the flag.
#   * Still withdrawn - six per-project packages.lock.json COPY instructions and the
#     manifest-first layer split. They affected build-cache efficiency only, never
#     behaviour, and the single tree copy brings the same files.
#   * Still withdrawn - `RUN chown -R appuser:appuser /app`. A publish copied as root
#     arrives world-readable and the process only READS its assemblies; the
#     data-protection key ring lives under the account's home directory rather than
#     /app. Its own comment conceded the process "would still START without the
#     chown".
#   * Still withdrawn - a COPY of its own for the package-source policy. That file is
#     no longer at the repository root: it lives at backend/NuGet.Config, which the
#     plan's four-file root list requires, and the single tree copy below already
#     brings it to /src/NuGet.Config.
#   * RETURNED - `--configfile` on the restore, and only that flag. Its withdrawal
#     rested on "the feed is unaffected: every identity resolves from nuget.org, the
#     image's own default source", which is true and beside the point. The default
#     source is a property of the BASE IMAGE, not of this repository, so the
#     production build was the one restore in the project that the repository's
#     cleared source list and package-source mapping did not govern - while that file
#     and README.md both said it did. The instruction and its full reasoning are at
#     the restore below.
#
# PIN POLICY. Both FROM lines name a DIGEST as well as a tag. That is the fourth
# addition to this file, it is documented at each instruction, and it is what makes a
# rebuild of one commit produce one image rather than whatever the moving tag points
# at that week.
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
# PINNED BY DIGEST, WITH THE READABLE TAG RETAINED. `8.0-alpine` on its own is a
# MOVING tag that Microsoft re-points at each 8.0.x patch release, so the same
# source commit built months apart compiled against a different SDK and shipped
# different bytes. When a FROM names both a tag and a digest, the digest is what
# the builder resolves and the tag is documentation - which is exactly the
# arrangement wanted here: the line still says which image family this is, and
# the build is reproducible.
#
# THE PINNED DIGEST, AND WHAT WAS VERIFIED IN IT (measured, not assumed):
#   sha256:8a80a27... = .NET SDK 8.0.424 on Alpine.
# backend/global.json pins the SDK band to 8.0.423 with feature-level roll
# forward, and 8.0.424 satisfies it - checked by running `dotnet --version` in
# this exact digest before pinning it, because a digest carrying a LOWER feature
# band would fail every restore in this image with an SDK-resolution error.
#
# THE REFRESH CADENCE IS A REVIEWED STEP, NOT A DRIFT. A pinned digest does not
# receive patch updates, so it must be bumped deliberately: resolve the current
# digest for the tag (`docker buildx imagetools inspect
# mcr.microsoft.com/dotnet/sdk:8.0-alpine`), confirm the SDK inside it still
# satisfies backend/global.json, replace the digest here, and re-run the container
# and end-to-end gates. That is the same cadence the runtime stage and
# docker/frontend.Dockerfile follow, and it is where a base-image advisory is
# acted on.
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine@sha256:8a80a27ddac789b4cb6d09d244f9c8d840da599c5ad22f7233c04be470e55261 AS build
WORKDIR /src

# One fewer network call during the build, and nothing sent from a build agent.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# THE REPOSITORY'S OWN PACKAGE-SOURCE POLICY ARRIVES WITH THE TREE, AND IT IS THE
# RESTORE BELOW THAT IS MADE TO OBEY IT. backend/NuGet.Config clears every inherited
# source, declares the single public source the dependency inventory was pinned
# against, and maps each package identity to it with no bare `*` pattern - so an
# identity nobody reviewed cannot be restored at all.
#
# NO COPY OF ITS OWN, BECAUSE THE FILE IS NOT AT THE REPOSITORY ROOT. It sits inside
# `backend/`, which the single tree copy below brings to /src/NuGet.Config - the
# repository root carries only the four files the plan enumerates. What WAS the defect
# is that the policy did not govern this restore: NuGet composes its settings by
# walking up from each project directory, and an earlier revision restored with no
# configuration option at all, so the image used whatever the base image's own
# settings declared while this file and README.md both stated otherwise. The strong
# form below closes that, and needs no extra instruction to do it.

# The whole backend tree, in one instruction. That brings global.json,
# Directory.Build.props, NuGet.Config, the solution, all six project files, all
# six packages.lock.json files and every source file, which is exactly what the
# restore and the publish below need and nothing more - the repository-root
# .dockerignore already excludes bin, obj and every environment file.
COPY backend/ ./

# Solution-wide restore, governed by backend/NuGet.Config, which arrived at
# /src/NuGet.Config with the tree copy above.
#
# `--configfile` is the strong form deliberately: it makes NuGet read THAT FILE AND
# NOTHING ELSE, so neither the base image's user-level settings nor any file a build
# agent mounts can add a source to the ones this repository declares. Without the
# flag that file would merely participate in the hierarchy, which is weaker
# than what the file itself claims.
#
# NO OTHER FLAG IS ADDED, AND THAT IS ALSO DELIBERATE. Locked mode is NOT restated
# here: backend/Directory.Build.props sets RestorePackagesWithLockFile and
# RestoreLockedMode for all six projects and the six packages.lock.json files
# arrived with the instruction above, so this restore is already hash-verified
# against the reviewed graph and already fails on any direct or transitive drift.
# Passing `--locked-mode` as well would duplicate a policy that has one home.
RUN dotnet restore --configfile ./NuGet.Config

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
#
# PINNED BY DIGEST, tag retained for readability, for the reason set out on the
# build stage. What was verified inside this exact digest before pinning it:
#   sha256:b288317... = Microsoft.AspNetCore.App 8.0.30 and Microsoft.NETCore.App
#   8.0.30 on Alpine 3.24.1, with icu-libs and icu-data-full 78.1-r0 available.
# The application is published framework-dependent against net8.0, so it runs on
# any 8.0.x shared framework; the AAP's 8.0.29 pin governs the NuGet PACKAGE graph,
# which this digest does not touch. Running on the newer patch runtime is ordinary
# roll-forward and is the safer side of the choice.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine@sha256:b288317d8ed45bb763fa95dbc807cf9d36e3bf9373ec2fac6b6548675f1f4b23 AS runtime
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
#
# THE PACKAGE VERSIONS ARE CONTROLLED BY THE PINNED BASE DIGEST, NOT BY AN apk
# CONSTRAINT, AND THAT IS A DELIBERATE CHOICE RATHER THAN AN OVERSIGHT. An Alpine
# branch repository publishes only the CURRENT version of each package, so
# `apk add icu-libs=78.1-r0` builds today and fails outright the moment the v3.24
# mirror advances to -r1 - it would convert a security update in the distribution
# into a broken build, which is the opposite of a controlled dependency. The
# controlled unit is therefore the base image digest: sha256:b288317... is Alpine
# 3.24.1, whose repository serves icu-libs and icu-data-full 78.1-r0 (verified by
# `apk policy` inside that exact digest). Rebuilding this Dockerfile unchanged
# resolves the same Alpine branch every time, and the packages move only when the
# digest is deliberately bumped - which is the same reviewed step, with the same
# gate re-run, that a runtime patch already requires. Record the apk inventory at
# that point if a bill of materials is kept.
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
# container's nginx once docker/nginx.tls.conf.template and a certificate are
# mounted, which is what docker/docker-compose.tls.yml does, or by an outer proxy -
# and no certificate is mounted into this image.
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
# SEC-F10. THE PROBE ADDRESSES /health/ready, NOT /health.
#
# The API publishes three anonymous views of the same check set: /health/live runs
# no dependency probe at all, /health/ready runs the ready-tagged ones - which is
# where the database check lives - and /health selects the process-only view. An
# orchestrator probing /health therefore reported this container healthy while it
# could not reach SQL Server, and the compose file's `service_healthy` gate let the
# front end start in front of an API that could not serve a single membership-backed
# request. Readiness is what "may this container receive traffic" means, so that is
# what is probed. Liveness remains available for a restart policy that must not
# recycle a process merely because a dependency is down.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health/ready || exit 1

# Exec form, so the runtime is process 1 and receives the stop signal directly.
# The shell form would wrap it in /bin/sh, swallow that signal and turn an
# orderly shutdown into a timeout and a kill. No CMD accompanies this: with the
# exec form any CMD would arrive as extra arguments to the runtime.
ENTRYPOINT ["dotnet", "DnnMigration.Api.dll"]
