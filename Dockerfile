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
RUN dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
      --configuration Release \
      --runtime linux-x64 \
      --self-contained false \
      --no-restore \
      --output /app

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
            "--cachedir", "/cache"]
