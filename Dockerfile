# syntax=docker/dockerfile:1

# ── Build stage ──────────────────────────────────────────────────────────────
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Restore dependencies first (layer-cache friendly)
COPY ["Jellyfin.sln", "global.json", "nuget.config", "Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["SharedVersion.cs", "BannedSymbols.txt", "stylecop.json", "./"]

# Copy all project files so dotnet restore can resolve the full dependency graph
COPY Emby.Naming/                                   Emby.Naming/
COPY Emby.Photos/                                   Emby.Photos/
COPY Emby.Server.Implementations/                  Emby.Server.Implementations/
COPY Jellyfin.Api/                                  Jellyfin.Api/
COPY Jellyfin.Data/                                 Jellyfin.Data/
COPY Jellyfin.Server/                               Jellyfin.Server/
COPY Jellyfin.Server.Implementations/              Jellyfin.Server.Implementations/
COPY MediaBrowser.Common/                           MediaBrowser.Common/
COPY MediaBrowser.Controller/                       MediaBrowser.Controller/
COPY MediaBrowser.LocalMetadata/                    MediaBrowser.LocalMetadata/
COPY MediaBrowser.MediaEncoding/                    MediaBrowser.MediaEncoding/
COPY MediaBrowser.Model/                            MediaBrowser.Model/
COPY MediaBrowser.Providers/                        MediaBrowser.Providers/
COPY MediaBrowser.XbmcMetadata/                    MediaBrowser.XbmcMetadata/
COPY src/                                           src/

RUN dotnet restore Jellyfin.Server/Jellyfin.Server.csproj \
      --runtime linux-x64

# Publish the server (and all transitive dependencies, including the
# PostgreSQL provider assembly added by this fork).
# Note: TreatWarningsAsErrors is disabled for the Docker build — StyleCop
# analyzer violations in upstream src/ projects would otherwise block the
# image build. StyleCop is enforced in the CI pipeline, not the Dockerfile.
RUN dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
      --configuration Release \
      --runtime linux-x64 \
      --self-contained false \
      --no-restore \
      -p:TreatWarningsAsErrors=false \
      --output /app

# ── Web client stage ──────────────────────────────────────────────────────────
# Install jellyfin-web via the official Jellyfin apt repo.
# Web assets land at /usr/share/jellyfin/web/ — stable, prebuilt, no npm required.
# Use 10.9.11 (latest released web client; API-compatible with the 10.12.0 server fork).
FROM --platform=linux/amd64 debian:bookworm-slim AS webclient

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
 && curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key \
    | gpg --dearmor -o /usr/share/keyrings/jellyfin.gpg \
 && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/jellyfin.gpg] https://repo.jellyfin.org/debian bookworm main" \
    > /etc/apt/sources.list.d/jellyfin.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends jellyfin-web=10.9.11+1 \
 && rm -rf /var/lib/apt/lists/* \
 && ls /usr/share/jellyfin/web/ | head -5

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0

# Install FFmpeg and native dependencies required by SkiaSharp and fontconfig.
# libicu, libssl, and liblttng-ust are already present in the dotnet/aspnet base image.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ffmpeg \
      fontconfig \
      libfontconfig1 \
      libfreetype6 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /jellyfin

COPY --from=build /app .
COPY --from=webclient /usr/share/jellyfin/web ./jellyfin-web/

# Jellyfin default ports
EXPOSE 8096
EXPOSE 8920

# Data / config volumes
VOLUME ["/config", "/cache", "/media"]

ENV JELLYFIN_DATA_DIR=/config \
    JELLYFIN_CACHE_DIR=/cache \
    JELLYFIN_LOG_DIR=/config/log \
    JELLYFIN_CONFIG_DIR=/config

ENTRYPOINT ["./jellyfin", \
            "--datadir", "/config", \
            "--cachedir", "/cache", \
            "--webdir", "/jellyfin/jellyfin-web"]
