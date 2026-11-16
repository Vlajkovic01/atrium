# syntax=docker/dockerfile:1

# Builds the single Atrium image: the server with the web app inside it.
# The build context is the repository root:
#   docker build -f deploy/Dockerfile .

# --- Web app -----------------------------------------------------------
# Runs on the build machine's own architecture: the output is plain files.
FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS web
WORKDIR /src
RUN npm install --global pnpm@12.18.0

# Dependency manifests first, so the install layer is reused until they change.
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY packages/shared/package.json packages/shared/
COPY web/package.json web/
RUN pnpm install --frozen-lockfile

COPY packages/shared packages/shared
COPY web web
RUN pnpm --filter @atrium/web run build

# --- Server ------------------------------------------------------------
# Also runs on the build machine's architecture and cross-compiles for the
# target, which is much faster than emulating the target to compile.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS server
ARG TARGETARCH
WORKDIR /src

COPY server/global.json server/Directory.Build.props server/Directory.Packages.props server/
COPY server/src server/src
RUN dotnet publish server/src/Atrium.Api \
      --configuration Release \
      --arch "$TARGETARCH" \
      --self-contained false \
      --output /app

# --- Final image -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0

LABEL org.opencontainers.image.title="Atrium" \
      org.opencontainers.image.description="Self-hosted personal cloud and media server" \
      org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.source="https://github.com/Vlajkovic01/atrium"

# The three places Atrium keeps data (docs/DESIGN.md 1.3). They are created
# here and owned by the unprivileged user, so a named volume mounted over
# them starts out writable by the server.
RUN mkdir -p /config /data /cache \
 && chown "$APP_UID" /config /data /cache

COPY --from=server /app /app
COPY --from=web /src/web/dist /app/wwwroot

ENV ATRIUM_CONFIG_DIR=/config \
    ATRIUM_CACHE_DIR=/cache \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# The server never needs root.
USER $APP_UID
WORKDIR /app
EXPOSE 8080
VOLUME ["/config", "/data", "/cache"]

# The server checks itself; the image carries no curl or wget.
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
  CMD ["dotnet", "Atrium.Api.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "Atrium.Api.dll"]
