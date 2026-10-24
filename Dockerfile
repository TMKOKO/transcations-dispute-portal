# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore on the project file alone so the layer is cached until a package reference changes.
COPY src/DisputePortal.Api/DisputePortal.Api.csproj src/DisputePortal.Api/
RUN dotnet restore src/DisputePortal.Api/DisputePortal.Api.csproj

COPY src/DisputePortal.Api/ src/DisputePortal.Api/
RUN dotnet publish src/DisputePortal.Api/DisputePortal.Api.csproj -c Release -o /app/publish --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# curl for the container healthcheck only.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# appsettings.Development.json carries a committed dev JWT signing key. Publish copies it, so it
# would ship inside the image layer; drop it. Compose supplies dev values as environment variables.
RUN rm -f /app/appsettings.Development.json

# Non-root: the aspnet:8.0 image ships an unprivileged "app" user (APP_UID).
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "DisputePortal.Api.dll"]
