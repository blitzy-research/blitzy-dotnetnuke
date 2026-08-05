# =============================================================================
# DnnMigration frontend - Angular 19 build + nginx runtime (AAP 0.9.3)
#
# Placeholder resolution: [project-name] -> dnn-migration, so the production
# build output copied below is dist/dnn-migration/browser (verified against a
# real `ng build --configuration production`).
# Build context is the REPOSITORY ROOT:
#   docker build -f docker/frontend.Dockerfile -t dnnmigration-frontend .
# =============================================================================

# ---------- build stage ----------
FROM node:20-alpine AS build
WORKDIR /app

# `npm ci` requires a committed package-lock.json.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build -- --configuration production

# ---------- runtime stage ----------
FROM nginx:alpine AS runtime

COPY docker/nginx.conf /etc/nginx/nginx.conf
COPY --from=build /app/dist/dnn-migration/browser /usr/share/nginx/html

EXPOSE 80 443

# The probe reads the fixed liveness location docker/nginx.conf declares, which is
# dependency-free and has its own access logging switched off. It must stay wget:
# nginx:alpine ships BusyBox wget and no curl, so a curl probe would exit 127 and
# report this container unhealthy for its whole life.
#
# SEC-B4: THE BASE IMAGE REDIRECTS NOTHING AND READS NO SECRET. An earlier version
# of this note said the plain-HTTP listener "redirects every application path to
# TLS" and that a certificate pair arrived as compose secrets at /run/secrets.
# Neither was true of the delivered artefacts, and both are now false by design:
# docker/nginx.conf decides no transport policy at all - it cannot, because the only
# facts available to it there are supplied by the caller - and a certificate reaches
# this image only when a deployment mounts docker/nginx.tls.conf plus a certificate
# DIRECTORY, which docker/docker-compose.tls.yml does. Nothing is copied into a
# layer, and this probe therefore speaks plain HTTP to a location that answers
# whether or not TLS is activated.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://127.0.0.1:80/nginx-health || exit 1

CMD ["nginx", "-g", "daemon off;"]
