# SmartTableSync container image: the worker host (SmartTableSync.Worker) on Linux.
# Build from the repository root, so the engine project is in the build context:
#   docker build -f SmartTableSync.Worker/Dockerfile -t smarttablesync-worker .
# Run it: docs/guides/containers.md

# --- Build ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, in its own layer, so changing code doesn't download the packages again
COPY Directory.Build.props ./
COPY SmartTableSync.Core/SmartTableSync.Core.csproj SmartTableSync.Core/
COPY SmartTableSync.Worker/SmartTableSync.Worker.csproj SmartTableSync.Worker/
RUN dotnet restore SmartTableSync.Worker/SmartTableSync.Worker.csproj

COPY SmartTableSync.Core/ SmartTableSync.Core/
COPY SmartTableSync.Worker/ SmartTableSync.Worker/
RUN dotnet publish SmartTableSync.Worker/SmartTableSync.Worker.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false

# --- Run -----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Run as the image's non-root user; the sync needs no file system or root rights
USER $APP_UID

# Keeps running and syncs every SmartTableSync__Worker__IntervalMinutes (default 5).
# Add "--once" (docker run ... smarttablesync-worker --once) to run once and exit.
ENTRYPOINT ["dotnet", "SmartTableSync.Worker.dll"]
