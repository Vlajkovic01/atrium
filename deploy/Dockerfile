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
# The mobile app is a member of the workspace, so its manifest must be
# here for the lockfile to match; the filter keeps its packages (React
# Native and the rest) out of this build.
COPY mobile/package.json mobile/
RUN pnpm install --frozen-lockfile --filter "@atrium/web..."

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

# libvips for photos, with the HEIF plugin so iPhone photos (HEIC) decode
# (D-032): the distribution's packages, whose source Ubuntu offers.
#
# FFmpeg and ffprobe from Jellyfin's build (D-064): GPL-3.0, never nonfree
# (CI checks), with every hardware encoder and its drivers (VA-API, Quick
# Sync, NVENC). Its repository's key is checked against the fingerprint
# below before anything is installed from it, and the version is pinned.
# Jellyfin offers the source; see docs/LICENSES.md.
ARG JELLYFIN_FFMPEG=8.1.3-2-noble
ARG JELLYFIN_KEY=4918AABC486CA052358D778D49023CD01DE21A7B
RUN apt-get update \
 && apt-get install --yes --no-install-recommends ca-certificates curl gnupg libvips42t64 libheif-plugin-libde265 \
 && . /etc/os-release && test "$VERSION_CODENAME" = noble \
 && curl --fail --silent --show-error --location https://repo.jellyfin.org/jellyfin_team.gpg.key \
      | gpg --dearmor --output /usr/share/keyrings/jellyfin.gpg \
 && gpg --show-keys --with-colons /usr/share/keyrings/jellyfin.gpg | grep -q "^fpr:::::::::$JELLYFIN_KEY:" \
 && echo "deb [signed-by=/usr/share/keyrings/jellyfin.gpg] https://repo.jellyfin.org/ubuntu noble main" > /etc/apt/sources.list.d/jellyfin.list \
 && apt-get update \
 && apt-get install --yes --no-install-recommends "jellyfin-ffmpeg8=$JELLYFIN_FFMPEG" \
 && apt-get purge --yes --auto-remove curl gnupg \
 && rm -rf /var/lib/apt/lists/*

# The three places Atrium keeps data (docs/DESIGN.md 1.3). They are created
# here and owned by the unprivileged user, so a named volume mounted over
# them starts out writable by the server.
RUN mkdir -p /config /data /cache \
 && chown "$APP_UID" /config /data /cache

COPY --from=server /app /app
COPY --from=web /src/web/dist /app/wwwroot

ENV ATRIUM_CONFIG_DIR=/config \
    ATRIUM_CACHE_DIR=/cache \
    ATRIUM_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg \
    ATRIUM_FFPROBE=/usr/lib/jellyfin-ffmpeg/ffprobe \
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
