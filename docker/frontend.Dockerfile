# =============================================================================
# docker/frontend.Dockerfile
#
# Multi-stage Linux Alpine image for the Angular 19 single-page application:
# compiled by Node 20 in the build stage, then served as static files by nginx.
# It reproduces the container example preserved in AAP 0.9.3 item by item, and
# the only substitution made against that example is the name placeholder.
#
# PLACEHOLDER RESOLUTION: [project-name] -> dnn-migration (kebab-case). That is
# the Angular workspace identifier - frontend/package.json declares
# "name": "dnn-migration" and frontend/angular.json builds the project of the
# same name - and it belongs ONLY in this file. The PascalCase form is the .NET
# assembly name and belongs ONLY in docker/api.Dockerfile's ENTRYPOINT. The two
# are never interchanged: a swap here breaks the image in a way no compiler and
# no build step can catch, because the path below would simply not exist.
#
# THE BUILD CONTEXT IS THE REPOSITORY ROOT, not docker/. Every COPY source is
# therefore repository-root-relative, which is why the application sources carry
# a frontend/ prefix and the proxy configuration is copied as docker/nginx.conf:
#
#     docker build -f docker/frontend.Dockerfile -t dnnmigration-frontend .
#
# The trailing dot is the context. docker/docker-compose.yml says the same thing
# as `context: ..` paired with `dockerfile: docker/frontend.Dockerfile`, and the
# repository-root .dockerignore is consequently the ignore list that governs this
# build. No path here escapes the context.
# =============================================================================

# ---------- build stage ----------
FROM node:20-alpine AS build
WORKDIR /app

# The manifest and the lockfile are copied on their own, ahead of the sources, so
# that editing a component does not invalidate this layer and re-run the whole
# dependency install.
#
# `npm ci` is deliberate and is not interchangeable with the non-locked
# alternative: ci installs strictly from package-lock.json and FAILS outright if
# the lockfile and the manifest disagree, which is exactly the deterministic
# restoration this migration requires, whereas a non-locked install would
# silently rewrite the lockfile and let the image drift from the audited
# dependency graph. frontend/package-lock.json is committed for this reason.
#
# No dev-dependency pruning flag is passed, and no production-mode environment
# variable is declared in this stage either: the Angular CLI, the application
# builder and the AOT compiler are all declared as devDependencies, so pruning
# them makes the next step fail with "ng: not found". They cost the shipped image
# nothing regardless, because the runtime stage below copies only the built output
# and none of this stage's node_modules. The configuration is selected by the flag
# on the build command, which is how this framework expects it to be chosen.
#
# No dependency-audit step is run here on purpose. A pristine workspace on the
# pinned Angular line reports a large advisory count before any application code
# exists, almost entirely from build-toolchain transitives that never reach the
# browser bundle, and the one runtime advisory covers every release of the
# mandated major version. It is unreachable in this application: no
# server-rendering package is installed and no client-hydration provider is
# registered anywhere in the workspace. An audit threshold here would block the
# delivery for a vector that cannot execute.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./

# The bare `--` is load-bearing: it forwards the flag through npm to the
# underlying `ng build`. Drop it and npm consumes --configuration itself, the
# build silently runs in the DEVELOPMENT configuration, and this image ships an
# unoptimised, unhashed bundle from a build that reported success. The script it
# invokes is deliberately bare ("build": "ng build") so this command, and Gate
# 3's, are the single place the configuration is chosen.
RUN npm run build -- --configuration production

# ---------- runtime stage ----------
FROM nginx:alpine AS runtime

# docker/nginx.conf IS MANDATORY, and this COPY is the reason: remove or rename
# that file and the image build fails right here, at this instruction.
#
# It is copied over the MAIN configuration rather than into conf.d/ because it is
# a complete configuration carrying its own top-level events{} and http{} blocks.
# Placing it in conf.d/ would nest those inside the stock http{} block, which is
# a syntax error, and nginx would refuse to start. The stock conf.d/ server block
# is left in place but inert, since this configuration does not include it.
COPY docker/nginx.conf /etc/nginx/nginx.conf

# THE /browser SUFFIX IS REQUIRED - DO NOT REMOVE IT.
#
# frontend/angular.json builds with @angular-devkit/build-angular:application,
# and that builder writes the browser bundle into a browser/ SUB-DIRECTORY of its
# outputPath. outputPath "dist/dnn-migration" therefore yields
# dist/dnn-migration/browser/index.html, not dist/dnn-migration/index.html.
#
# Copying dist/dnn-migration instead would copy a directory that merely CONTAINS
# browser/, leaving nginx with no index.html at its root and serving a directory
# listing or a 403 while `docker build` still reports complete success. Nothing in
# the build can catch that, so the check that catches it is asserting index.html
# lands directly in the destination below. The destination itself matches the
# `root` that docker/nginx.conf declares.
COPY --from=build /app/dist/dnn-migration/browser /usr/share/nginx/html

# Matches the `listen 80` in docker/nginx.conf and the 4200:80 publication in
# docker/docker-compose.yml, which is what answers Gate 7's request to
# localhost:4200. EXPOSE is image metadata only - it neither opens nor publishes
# a port - so declaring 80 alone takes nothing away from the TLS topology in
# docker/docker-compose.tls.yml, which publishes its own port list and mounts a
# server block plus a certificate directory at run time.
EXPOSE 80

# The probe must stay wget. wget is always available here because BusyBox
# provides it, so the probe cannot break as the base image's package set drifts.
# `alpine` is a moving tag: the exact package inventory of the runtime image is
# not fixed across rebuilds, and a probe that named a tool supplied only by an
# optional package would exit 127 the moment a rebuild dropped it and would then
# report the container unhealthy for its entire life. Measured on the image this
# file builds today, BusyBox v1.37.0 supplies wget - that is the guarantee this
# probe relies on, and it is the form the container example specifies.
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

# There is deliberately no privilege-dropping directive in this stage. The nginx
# master process needs root to bind port 80, and it lowers its own workers to the
# unprivileged nginx account by itself; overriding the image's account here breaks
# that bind and the container never starts. The non-root requirement in this
# migration is scoped to the API image, whose example names the account creation
# explicitly - the example for this image does not. The asymmetry is intentional.
#
# Restating the base image's own default command, in exec form, so the process
# this container runs is visible in the file rather than only in the base image.
CMD ["nginx", "-g", "daemon off;"]
