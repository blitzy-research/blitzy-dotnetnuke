# =============================================================================
# DnnMigration API - multi-stage Linux Alpine image (AAP 0.9.3)
#
# Placeholder resolution: [ProjectName] -> DnnMigration, so the published
# assembly is DnnMigration.Api.dll (verified against the built solution).
# Build context is the REPOSITORY ROOT:
#   docker build -f docker/api.Dockerfile -t dnnmigration-api .
# =============================================================================

# ---------- build stage ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Restore first so the layer is cached independently of source changes.
COPY backend/global.json ./
COPY backend/Directory.Build.props ./
COPY backend/DnnMigration.sln ./
COPY backend/src/DnnMigration.Domain/DnnMigration.Domain.csproj src/DnnMigration.Domain/
COPY backend/src/DnnMigration.Application/DnnMigration.Application.csproj src/DnnMigration.Application/
COPY backend/src/DnnMigration.Infrastructure/DnnMigration.Infrastructure.csproj src/DnnMigration.Infrastructure/
COPY backend/src/DnnMigration.Api/DnnMigration.Api.csproj src/DnnMigration.Api/
COPY backend/tests/DnnMigration.UnitTests/DnnMigration.UnitTests.csproj tests/DnnMigration.UnitTests/
COPY backend/tests/DnnMigration.IntegrationTests/DnnMigration.IntegrationTests.csproj tests/DnnMigration.IntegrationTests/
RUN dotnet restore src/DnnMigration.Api/DnnMigration.Api.csproj

# Then the sources.
COPY backend/ ./
RUN dotnet publish src/DnnMigration.Api/DnnMigration.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        /p:UseAppHost=false

# ---------- runtime stage ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# Non-root runtime user (non-functional requirement: container hardening).
RUN adduser -D -u 1000 appuser

COPY --from=build /app/publish ./
RUN chown -R appuser:appuser /app
USER appuser

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true

EXPOSE 8080

# aspnet:8.0-alpine ships wget, NOT curl - do not substitute curl here.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget --no-verbose --tries=1 --spider http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "DnnMigration.Api.dll"]
