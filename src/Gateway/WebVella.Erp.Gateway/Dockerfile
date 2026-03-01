##############################################################################
# WebVella ERP — API Gateway / BFF Service — Dockerfile
#
# Multi-stage Docker build for the API Gateway / Backend-for-Frontend service.
# This service is the single entry point for clients, routing requests to
# backend microservices (Core, CRM, Project, Mail, Reporting, Admin) and
# serving preserved Razor Pages UI.
#
# Target runtime: .NET 10 (net10.0 LTS)
# Exposed port:   8080 (HTTP)
# Assembly:        WebVella.Erp.Gateway.dll
#
# Build context:   Solution root (.) — as configured in docker-compose.yml
# Dockerfile path: src/Gateway/WebVella.Erp.Gateway/Dockerfile
#
# Usage:
#   docker compose up gateway          # Via docker-compose.yml
#   docker build -f src/Gateway/WebVella.Erp.Gateway/Dockerfile -t erp-gateway .
##############################################################################

# =============================================================================
# LABELS — Service metadata
# =============================================================================
# Applied early so they are present in all stages and the final image.

# =============================================================================
# STAGE 1: BUILD — Restore, compile, and publish the Gateway project
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

LABEL maintainer="WebVella"
LABEL service="gateway"

WORKDIR /src

# ---------------------------------------------------------------------------
# 1a. Copy build infrastructure files for NuGet restore layer caching.
#     These change infrequently, so Docker can cache the restore layer.
# ---------------------------------------------------------------------------
COPY Directory.Build.props Directory.Build.props
COPY Directory.Packages.props Directory.Packages.props

# ---------------------------------------------------------------------------
# 1b. Copy project files (.csproj) for restore — SharedKernel dependency first,
#     then the Gateway project itself. Ordering allows Docker layer caching to
#     skip restore when only source files change.
# ---------------------------------------------------------------------------
COPY src/SharedKernel/WebVella.Erp.SharedKernel/WebVella.Erp.SharedKernel.csproj \
     src/SharedKernel/WebVella.Erp.SharedKernel/

COPY src/Gateway/WebVella.Erp.Gateway/WebVella.Erp.Gateway.csproj \
     src/Gateway/WebVella.Erp.Gateway/

# ---------------------------------------------------------------------------
# 1c. Copy proto files required for gRPC client code generation at build time.
#     The Gateway csproj includes <Protobuf> items referencing ../../../proto/*.
# ---------------------------------------------------------------------------
COPY proto/ proto/

# ---------------------------------------------------------------------------
# 1d. Restore NuGet packages (cached unless .csproj or .props files change).
# ---------------------------------------------------------------------------
RUN dotnet restore src/Gateway/WebVella.Erp.Gateway/WebVella.Erp.Gateway.csproj

# ---------------------------------------------------------------------------
# 1e. Copy ALL source files for SharedKernel and Gateway projects.
#     Done after restore so that source-only changes don't invalidate the
#     NuGet restore cache layer.
# ---------------------------------------------------------------------------
COPY src/SharedKernel/WebVella.Erp.SharedKernel/ \
     src/SharedKernel/WebVella.Erp.SharedKernel/

COPY src/Gateway/WebVella.Erp.Gateway/ \
     src/Gateway/WebVella.Erp.Gateway/

# ---------------------------------------------------------------------------
# 1f. Publish the Gateway project in Release configuration.
#     --no-restore skips redundant restore since we already restored above.
#     Output goes to /app/publish for the runtime stage to COPY.
# ---------------------------------------------------------------------------
RUN dotnet publish src/Gateway/WebVella.Erp.Gateway/WebVella.Erp.Gateway.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# =============================================================================
# STAGE 2: RUNTIME — Minimal ASP.NET Core image for production deployment
# =============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

LABEL maintainer="WebVella"
LABEL service="gateway"

WORKDIR /app

# ---------------------------------------------------------------------------
# 2a. Install curl for the HEALTHCHECK probe.
#     The aspnet base image is Debian-slim and does not include curl by default.
#     We clean up the apt cache immediately to keep the layer small.
# ---------------------------------------------------------------------------
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# ---------------------------------------------------------------------------
# 2b. Copy the published application output from the build stage.
# ---------------------------------------------------------------------------
COPY --from=build /app/publish .

# ---------------------------------------------------------------------------
# 2c. Environment variables — ASP.NET Core runtime configuration.
# ---------------------------------------------------------------------------
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# ---------------------------------------------------------------------------
# 2d. Expose the HTTP port used by the Gateway.
#     Port 8080 is mapped to host port 5000 in docker-compose.yml.
# ---------------------------------------------------------------------------
EXPOSE 8080

# ---------------------------------------------------------------------------
# 2e. Run as non-root user for security best practices.
#     The aspnet:10.0 image defines $APP_UID (defaults to 1654).
# ---------------------------------------------------------------------------
USER $APP_UID

# ---------------------------------------------------------------------------
# 2f. Health check — verifies the Gateway is responding on /health endpoint.
#     The application must map a health check endpoint at /health (configured
#     in Program.cs via app.MapHealthChecks("/health")).
# ---------------------------------------------------------------------------
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# ---------------------------------------------------------------------------
# 2g. Entry point — launch the Gateway assembly via dotnet runtime.
# ---------------------------------------------------------------------------
ENTRYPOINT ["dotnet", "WebVella.Erp.Gateway.dll"]
