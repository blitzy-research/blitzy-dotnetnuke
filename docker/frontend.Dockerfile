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

# The plain-HTTP listener exposes only this fixed liveness location and redirects
# every application path to TLS. Certificate and private-key files are mounted as
# compose secrets at /run/secrets; neither is copied into an image layer.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://127.0.0.1:80/nginx-health || exit 1

CMD ["nginx", "-g", "daemon off;"]
