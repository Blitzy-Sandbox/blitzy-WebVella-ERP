# ==============================================================================
# Dockerfile — WebVella ERP Reporting Microservice
# ==============================================================================
# Multi-stage build for the Reporting and aggregation service targeting .NET 10.0.
# Provides report generation, data aggregation, and analytics capabilities
# via REST endpoints and event-driven interfaces.
#
# Build context: repository root (NOT the service directory).
#
# Usage (standalone):
#   docker build -f src/Services/WebVella.Erp.Service.Reporting/Dockerfile \
#                -t webvella-erp-reporting:latest .
#
# Usage (docker-compose):
#   reporting-service:
#     build:
#       context: .
#       dockerfile: src/Services/WebVella.Erp.Service.Reporting/Dockerfile
#     ports:
#       - "5050:8080"
#     depends_on:
#       - postgres-reporting
#       - redis
#       - rabbitmq
# ==============================================================================

# ------------------------------------------------------------------------------
# Stage 1: Build
# ------------------------------------------------------------------------------
# Use the .NET 10.0 SDK image for compiling, restoring packages, and publishing.
# The SDK image includes the full toolchain (MSBuild, NuGet, Roslyn).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG BUILD_CONFIGURATION=Release

WORKDIR /src

# -- Layer 1: Build infrastructure files (rarely change) ----------------------
# Copy MSBuild infrastructure first for optimal Docker layer caching.
# These files control package versions and shared build properties across all
# projects. Changes to these files invalidate the restore cache.
COPY Directory.Build.props Directory.Packages.props ./

# Copy global.json to pin the .NET SDK version inside the container,
# ensuring deterministic builds matching the developer environment.
COPY global.json ./

# -- Layer 2: Project files for NuGet restore (change infrequently) -----------
# Copy only .csproj files to leverage Docker layer caching for `dotnet restore`.
# The restore layer is invalidated only when project references or package
# versions change — not when source code changes.

# SharedKernel dependency (referenced by Reporting service via ProjectReference)
COPY src/SharedKernel/WebVella.Erp.SharedKernel/WebVella.Erp.SharedKernel.csproj \
     src/SharedKernel/WebVella.Erp.SharedKernel/

# Reporting service project
COPY src/Services/WebVella.Erp.Service.Reporting/WebVella.Erp.Service.Reporting.csproj \
     src/Services/WebVella.Erp.Service.Reporting/

# -- Layer 3: NuGet package restore (cached until csproj/props change) --------
RUN dotnet restore src/Services/WebVella.Erp.Service.Reporting/WebVella.Erp.Service.Reporting.csproj

# -- Layer 4: Full source copy (changes frequently) --------------------------
# Copy only the SharedKernel and Reporting service source code required for
# build. Selective COPY avoids pulling in the entire repository, reducing
# build context size and improving layer caching.
COPY src/SharedKernel/WebVella.Erp.SharedKernel/ \
     src/SharedKernel/WebVella.Erp.SharedKernel/

COPY src/Services/WebVella.Erp.Service.Reporting/ \
     src/Services/WebVella.Erp.Service.Reporting/

# -- Layer 5: Publish --------------------------------------------------------
# Publish a framework-dependent Release build to /app/publish.
# --no-restore skips restore since it was already performed in Layer 3.
# --no-self-contained produces a framework-dependent deployment (FDD) that
# relies on the aspnet:10.0 runtime image's shared framework.
RUN dotnet publish src/Services/WebVella.Erp.Service.Reporting/WebVella.Erp.Service.Reporting.csproj \
    -c ${BUILD_CONFIGURATION} \
    -o /app/publish \
    --no-restore \
    --no-self-contained

# ------------------------------------------------------------------------------
# Stage 2: Runtime
# ------------------------------------------------------------------------------
# Use the minimal ASP.NET Core 10.0 runtime image. This image includes only
# the .NET runtime and ASP.NET Core shared framework — no SDK, compilers,
# or build tools — resulting in a smaller, more secure container.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install curl for the Docker HEALTHCHECK probe.
# The aspnet base image (Debian Bookworm) does not include curl by default.
# We clean up apt lists to minimize image size.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy the published application from the build stage.
# Files are owned by root but world-readable, which is appropriate for
# the non-root APP_UID user that will run the application.
COPY --from=build /app/publish .

# -- OCI Image Labels ---------------------------------------------------------
# Standard OCI annotation labels for image metadata, searchability, and
# provenance tracking in container registries and orchestrators.
LABEL org.opencontainers.image.title="WebVella ERP Reporting Service" \
      org.opencontainers.image.description="Reporting and aggregation microservice for WebVella ERP. Provides report generation, data aggregation, and analytics via REST and event-driven interfaces." \
      org.opencontainers.image.version="1.7.7" \
      org.opencontainers.image.vendor="WebVella" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.source="https://github.com/WebVella/WebVella-ERP" \
      org.opencontainers.image.documentation="https://webvella.com"

# -- Environment Configuration ------------------------------------------------
# ASPNETCORE_URLS: Bind to all interfaces on port 8080 (HTTP).
#   The Reporting service exposes only REST endpoints (no gRPC).
# ASPNETCORE_ENVIRONMENT: Default to Production for container deployments.
#   Override via docker-compose or Kubernetes env vars for development/staging.
# DOTNET_RUNNING_IN_CONTAINER: Signals the .NET runtime that it is executing
#   inside a container, enabling optimized defaults for memory and GC.
# DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: Set to false to ensure full ICU
#   globalization support. Required for locale-aware operations (en-US locale).
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# -- Network Configuration ----------------------------------------------------
# Expose only the HTTP REST API port.
# Port 8080: HTTP/1.1 REST API endpoints + health checks
# Note: The Reporting service does not expose a gRPC port (no gRPC services
# defined per the architecture plan). Inter-service data is consumed via
# event subscriptions and REST API calls to other services.
EXPOSE 8080

# -- Security: Run as non-root user -------------------------------------------
# The official .NET 10.0 aspnet image defines an unprivileged 'app' user
# with UID stored in the APP_UID environment variable. Running as non-root
# reduces the attack surface and follows container security best practices.
USER $APP_UID

# -- Health Check --------------------------------------------------------------
# Docker HEALTHCHECK probes the /health endpoint exposed by ASP.NET Core
# health checks (configured in Program.cs via AddHealthChecks + MapHealthChecks).
# Checks PostgreSQL and Redis connectivity.
#   --interval=30s    : Check every 30 seconds
#   --timeout=5s      : Fail if no response within 5 seconds
#   --start-period=10s: Grace period for service startup
#   --retries=3       : Mark unhealthy after 3 consecutive failures
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# -- Application Entry Point --------------------------------------------------
# Launch the Reporting service. The assembly name matches the .csproj
# AssemblyName property: WebVella.Erp.Service.Reporting.
ENTRYPOINT ["dotnet", "WebVella.Erp.Service.Reporting.dll"]
