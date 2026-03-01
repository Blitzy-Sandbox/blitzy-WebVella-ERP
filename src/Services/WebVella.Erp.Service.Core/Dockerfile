# ==============================================================================
# Dockerfile — WebVella ERP Core Platform Microservice
# ==============================================================================
# Multi-stage build for the Core Platform service targeting .NET 10.0.
#
# Build context: repository root (NOT the service directory).
#
# Usage (standalone):
#   docker build -f src/Services/WebVella.Erp.Service.Core/Dockerfile \
#                -t webvella-erp-core:latest .
#
# Usage (docker-compose):
#   core-service:
#     build:
#       context: .
#       dockerfile: src/Services/WebVella.Erp.Service.Core/Dockerfile
#     ports:
#       - "5010:8080"
#       - "5011:8081"
#     depends_on:
#       - postgres-core
#       - redis
#       - rabbitmq
# ==============================================================================

# ------------------------------------------------------------------------------
# Stage 1: Build
# ------------------------------------------------------------------------------
# Use the .NET 10.0 SDK image for compiling, restoring packages, and publishing.
# The SDK image includes the full toolchain (MSBuild, NuGet, Roslyn, gRPC tools).
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

# SharedKernel dependency (referenced by Core service via ProjectReference)
COPY src/SharedKernel/WebVella.Erp.SharedKernel/WebVella.Erp.SharedKernel.csproj \
     src/SharedKernel/WebVella.Erp.SharedKernel/

# Core Platform service project
COPY src/Services/WebVella.Erp.Service.Core/WebVella.Erp.Service.Core.csproj \
     src/Services/WebVella.Erp.Service.Core/

# -- Layer 3: NuGet package restore (cached until csproj/props change) --------
RUN dotnet restore src/Services/WebVella.Erp.Service.Core/WebVella.Erp.Service.Core.csproj

# -- Layer 4: Full source copy (changes frequently) --------------------------
# Copy the entire repository source tree. This includes:
# - SharedKernel source code (src/SharedKernel/)
# - Core service source code (src/Services/WebVella.Erp.Service.Core/)
# - Proto files (proto/) referenced by Grpc.Tools for code generation
# A .dockerignore file at the repository root should exclude bin/, obj/,
# node_modules/, .git/, and other non-essential directories.
COPY . .

# -- Layer 5: Publish --------------------------------------------------------
# Change to the Core service project directory and publish a self-contained
# Release build to /app/publish. The --no-restore flag skips restore since
# it was already performed in Layer 3.
WORKDIR /src/src/Services/WebVella.Erp.Service.Core
RUN dotnet publish -c ${BUILD_CONFIGURATION} -o /app/publish --no-restore

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

# -- Environment Configuration ------------------------------------------------
# ASPNETCORE_URLS: Bind to all interfaces on port 8080 (HTTP).
#   gRPC traffic is served on port 8081 via Kestrel endpoint configuration
#   in appsettings.json (Kestrel:Endpoints:Grpc).
# ASPNETCORE_ENVIRONMENT: Default to Production for container deployments.
#   Override via docker-compose or Kubernetes env vars for development/staging.
# DOTNET_RUNNING_IN_CONTAINER: Signals the .NET runtime that it is executing
#   inside a container, enabling optimized defaults for memory and GC.
# DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: Set to false to ensure full ICU
#   globalization support. Required for locale-aware operations (en-US locale)
#   and Bulgarian full-text search (Fts/BulStem).
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# -- Network Configuration ----------------------------------------------------
# Expose HTTP (REST API) and gRPC ports.
# Port 8080: HTTP/1.1 REST API endpoints + health checks
# Port 8081: HTTP/2 gRPC endpoints (EntityGrpcService, RecordGrpcService,
#            SecurityGrpcService) for inter-service communication
EXPOSE 8080
EXPOSE 8081

# -- Security: Run as non-root user -------------------------------------------
# The official .NET 10.0 aspnet image defines an unprivileged 'app' user
# with UID stored in the APP_UID environment variable. Running as non-root
# reduces the attack surface and follows container security best practices.
USER $APP_UID

# -- Health Check --------------------------------------------------------------
# Docker HEALTHCHECK probes the /health endpoint exposed by ASP.NET Core
# health checks (configured in Program.cs via AddHealthChecks + MapHealthChecks).
# Checks PostgreSQL and Redis connectivity.
#   --interval=30s   : Check every 30 seconds
#   --timeout=5s     : Fail if no response within 5 seconds
#   --start-period=10s : Grace period for service startup
#   --retries=3      : Mark unhealthy after 3 consecutive failures
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# -- Application Entry Point --------------------------------------------------
# Launch the Core Platform service. The assembly name matches the .csproj
# AssemblyName property (defaults to the project directory name).
ENTRYPOINT ["dotnet", "WebVella.Erp.Service.Core.dll"]
