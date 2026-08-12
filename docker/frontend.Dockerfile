# =============================================================================
# docker/frontend.Dockerfile
#
# Multi-stage Linux Alpine image for the Angular 19 single-page application:
# compiled by Node 20 in the build stage, then served as static files by nginx. It
# reproduces the container example preserved in AAP 0.9.3 item by item; the only
# substitution against that example is the name placeholder.
#
# [project-name] -> dnn-migration, the Angular workspace identifier that
# frontend/package.json and frontend/angular.json both declare. It belongs ONLY
# here; the PascalCase DnnMigration is the .NET assembly name and belongs ONLY in
# docker/api.Dockerfile's ENTRYPOINT. Swapping them breaks the image in a way no
# build step can catch, because the path below would simply not exist.
#
# THE BUILD CONTEXT IS THE REPOSITORY ROOT, not docker/, so every COPY source is
# repository-root-relative - hence the frontend/ prefix and docker/nginx.conf:
#
#     docker build -f docker/frontend.Dockerfile -t dnnmigration-frontend .
#
# The trailing dot is the context. docker/docker-compose.yml expresses the same
# thing as `context: ..`, so the repository-root .dockerignore governs this build.
# No path here escapes the context.
# =============================================================================

# ---------- build stage ----------
# PINNED BY DIGEST, WITH THE READABLE TAG RETAINED. `node:20-alpine` is a MOVING
# tag: the same source commit built months apart would compile with a different
# Node and npm, and could ship a different bundle. When a FROM names both a tag and
# a digest the builder resolves the DIGEST and the tag is documentation, which is
# the arrangement wanted - the line still says which image family this is, and the
# build is reproducible.
#
# WHAT WAS VERIFIED INSIDE THIS EXACT DIGEST BEFORE PINNING IT (measured, not
# assumed): Node v20.20.2 and npm 10.8.2 on Alpine 3.23.4. frontend/package.json
# constrains engines to Node >=20.20.2 <21 and frontend/.nvmrc pins 20.20.2, so
# this digest satisfies both; a digest carrying an older Node would fail `npm ci`
# on the engine check rather than at some later, less obvious point.
#
# THE REFRESH CADENCE IS A REVIEWED STEP: resolve the tag's current digest
# (`docker buildx imagetools inspect node:20-alpine`), confirm the Node inside it
# still satisfies the engine range, replace the digest, re-run the frontend build
# and test gates and the container gates. That is where a base-image advisory is
# acted on, and it is the same cadence the runtime stage and docker/api.Dockerfile
# follow.
FROM node:20-alpine@sha256:fb4cd12c85ee03686f6af5362a0b0d56d50c58a04632e6c0fb8363f609372293 AS build
WORKDIR /app

# Manifest and lockfile alone, ahead of the sources, so that editing a component
# does not invalidate this layer and re-run the whole install.
#
# `npm ci` is not interchangeable with a plain install: ci installs strictly from
# package-lock.json and FAILS if the lockfile and the manifest disagree, which is
# the deterministic restoration this migration requires, whereas a plain install
# would rewrite the lockfile and let the image drift from the audited graph.
# frontend/package-lock.json is committed for this reason.
#
# NO DEV-DEPENDENCY PRUNING, deliberately: the Angular CLI, the application
# builder and the AOT compiler are all devDependencies, so pruning them makes the
# next step fail with "ng: not found". They cost the shipped image nothing anyway,
# because the runtime stage copies only the built output.
#
# NO AUDIT STEP, deliberately. The decision, its dated evidence and its review
# trigger are in MIGRATION_NOTES.md Section 10 and README.md Section 7; in short,
# a threshold here would fail on advisories against features this application does
# not use, with no remedy inside the mandated Angular major version.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./

# THE BARE `--` IS LOAD-BEARING: it forwards the flag through npm to `ng build`.
# Drop it and npm consumes --configuration itself, the build silently runs in the
# DEVELOPMENT configuration, and this image ships an unoptimised, unhashed bundle
# from a build that reported success. The npm script is deliberately bare
# ("build": "ng build") so this command is the single place the configuration is
# chosen.
RUN npm run build -- --configuration production

# ---------- runtime stage ----------
# PINNED BY DIGEST, tag retained, for the reason given on the build stage. What was
# verified inside this exact digest before pinning it: nginx 1.31.3 on Alpine
# 3.24.1 with BusyBox v1.37.0 - and BusyBox is what supplies the `wget` the
# HEALTHCHECK below depends on, which is precisely the kind of package-set fact a
# moving tag can change under a probe that has no alternative. The rendered-template
# entrypoint this image's TLS route relies on
# (/docker-entrypoint.d/20-envsubst-on-templates.sh) is likewise part of the pinned
# digest rather than of a tag that might drop it.
FROM nginx:alpine@sha256:4a73073bd557c65b759505da037898b61f1be6cbcc3c2c3aeac22d2a470c1752 AS runtime

# docker/nginx.conf IS MANDATORY, and this COPY is the reason: remove or rename it
# and the image build fails at this instruction.
#
# It replaces the MAIN configuration rather than landing in conf.d/ because it is
# a complete configuration with its own top-level events{} and http{} blocks;
# nesting those inside the stock http{} block is a syntax error and nginx would
# refuse to start. The stock conf.d/ server block stays on disk but inert, because
# this configuration does not include it.
COPY docker/nginx.conf /etc/nginx/nginx.conf

# THE THREE SHARED SNIPPETS, AND THEY ARE AS MANDATORY AS THE FILE ABOVE. nginx
# fails to START when an include names a file that is absent, and
# docker/nginx.conf includes all three unconditionally - so dropping this
# instruction breaks the image's own health probe immediately rather than quietly
# removing a header or a proxy protection.
#
# They land in /etc/nginx/snippets/, a directory NOTHING in this image
# auto-includes. That matters: /etc/nginx/conf.d/ is included by the stock
# configuration and /etc/nginx/tls/ is included by ours, and a fragment containing
# `location` blocks placed in either would be parsed in the wrong context and
# refuse to load. A directory reached only by an explicit include is the only
# correct home for a fragment.
#
# WHY THEY ARE SHARED FILES RATHER THAN INLINE TEXT. The TLS server block a public
# deployment mounts (docker/nginx.tls.conf.template) has to declare the
# application's locations itself, because an nginx server inherits from http but
# not from a sibling server. It used to declare COPIES, and the copies had drifted:
# the public listener lacked per-request upstream resolution, the raw request
# target, the import-sized body allowance, the RFC 7807 gateway answer and three
# response security headers. Both servers now include these files, so the two
# listeners cannot serve the application differently.
COPY docker/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY docker/api-proxy.conf        /etc/nginx/snippets/api-proxy.conf
COPY docker/spa-static.conf       /etc/nginx/snippets/spa-static.conf

# THE TLS INCLUDE DIRECTORY, CREATED EMPTY, AND ITS EXISTENCE IS LOAD-BEARING FOR
# TWO SEPARATE REASONS.
#
#   1. It is where a deployment's rendered TLS server block lands. This image's own
#      entrypoint (/docker-entrypoint.d/20-envsubst-on-templates.sh) renders every
#      /etc/nginx/templates/*.template into $NGINX_ENVSUBST_OUTPUT_DIR, and
#      docker/docker-compose.tls.yml points that at /etc/nginx/tls so
#      docker/nginx.conf's `include /etc/nginx/tls/*.conf` picks it up. That script
#      refuses to render when the output directory is not writable - it logs
#      `$output_dir is not writable` and RETURNS, leaving nginx to start with no TLS
#      server at all - and a directory that does not exist is not writable. Measured
#      in this image rather than assumed: the check is `[ ! -w "$output_dir" ]`,
#      evaluated before the render loop. Creating the directory here is therefore the
#      difference between a TLS deployment that serves HTTPS and one that silently
#      serves only plain HTTP.
#   2. It keeps the wildcard include in docker/nginx.conf meaningful in the base
#      image, where the directory is deliberately EMPTY: a glob that matches nothing
#      is not an error in nginx, which is what lets one image serve plain HTTP with no
#      certificate present and HTTPS the moment a deployment supplies one.
#
# No certificate, key or passphrase is committed to this repository and none is baked
# into this image; the certificate pair is mounted at run time into a certificates/
# sub-directory that the *.conf glob cannot match.
RUN mkdir -p /etc/nginx/tls

# THE /browser SUFFIX IS REQUIRED - DO NOT REMOVE IT.
# @angular-devkit/build-angular:application writes the browser bundle into a
# browser/ SUB-DIRECTORY of outputPath, so "dist/dnn-migration" yields
# dist/dnn-migration/browser/index.html. Copying the parent instead leaves nginx
# with no index.html at its root - a directory listing or a 403 - while
# `docker build` still reports success, and nothing in the build can catch it. The
# destination matches the `root` docker/nginx.conf declares.
COPY --from=build /app/dist/dnn-migration/browser /usr/share/nginx/html

# Matches `listen 80` in docker/nginx.conf and the 4200:80 publication in
# docker/docker-compose.yml. EXPOSE is metadata only - it neither opens nor
# publishes a port - so declaring 80 alone takes nothing away from the TLS
# topology, which publishes its own port list at run time.
EXPOSE 80

# The probe must stay wget, and there are now two independent reasons it cannot
# break. BusyBox provides wget unconditionally in this image family, so the probe
# does not depend on an optional package that a package-set change could drop -
# a probe naming a tool that went missing would exit 127 and report the container
# unhealthy for its entire life. And the runtime stage is pinned by digest, so the
# package inventory is fixed for this commit rather than resolved afresh on each
# rebuild: BusyBox v1.37.0 supplies wget inside sha256:4a73073..., measured in that
# digest. wget is also the form the container example specifies.
#
# It reads /nginx-health, the dependency-free liveness location that
# docker/nginx.conf declares for exactly this purpose: an exact-match location
# with access logging off that returns a fixed 200 without touching the
# filesystem or any upstream. docker/docker-compose.yml declares the identical
# probe for the service, so the image and the orchestrator agree. It is
# deliberately NOT the API's /health - that endpoint belongs to the api container
# on port 8080 and nginx does not serve it - and deliberately not an /api/ path,
# which would proxy upstream and make this container's health a function of the
# API's.
#
# Plain HTTP is correct here whether or not TLS is active, because this image
# decides no transport policy and holds no certificate. TLS reaches this
# container only when a deployment MOUNTS a server block and a certificate
# directory, as docker/docker-compose.tls.yml does; nothing of the sort is baked
# into a layer. docker/nginx.conf names its port-80 server as default_server
# precisely so that a mounted TLS block cannot capture this probe and redirect it
# to a name whose certificate 127.0.0.1 could never verify.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://127.0.0.1:80/nginx-health || exit 1

# NO privilege-dropping directive here, deliberately: the nginx master process
# needs root to bind port 80 and lowers its own workers to the unprivileged nginx
# account by itself, so overriding the account breaks that bind and the container
# never starts. The non-root requirement is scoped to the API image, whose own
# example names the account creation explicitly; this image's example does not.
#
# Restating the base image's default command, in exec form, so the process this
# container runs is visible here rather than only in the base image.
CMD ["nginx", "-g", "daemon off;"]
