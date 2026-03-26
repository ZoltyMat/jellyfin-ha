> **Last updated: 2026-03-04**

# Jellyfin Server Architecture

High-level overview of the Jellyfin server structure, layer responsibilities, and key subsystems.

## Runtime

| Component | Value |
|---|---|
| Framework | .NET 10 / ASP.NET Core 10 |
| Target | `net10.0` |
| Entry point | `Jellyfin.Server` |
| Version | `10.12.0` (see `SharedVersion.cs`) |

---

## Layer Diagram

```
┌───────────────────────────────────────────────────────────┐
│                      HTTP Clients                         │
│            (Jellyfin Web, mobile apps, 3rd-party)         │
└────────────────────────┬──────────────────────────────────┘
                         │ REST / WebSocket
┌────────────────────────▼──────────────────────────────────┐
│                   Jellyfin.Api                            │
│   ASP.NET Core controllers, middleware, auth, Swashbuckle  │
└────────────────────────┬──────────────────────────────────┘
                         │ Interfaces (ILibraryManager, etc.)
┌────────────────────────▼──────────────────────────────────┐
│              MediaBrowser.Controller                      │
│   Core domain interfaces — no implementation here         │
└────────────────────────┬──────────────────────────────────┘
                         │ Implementations
┌────────────────────────▼──────────────────────────────────┐
│    Emby.Server.Implementations / Jellyfin.Server.Impl     │
│   Library manager, item repos, scheduled tasks, HTTP server│
└────────┬───────────────────────────────┬──────────────────┘
         │                               │
┌────────▼────────┐             ┌────────▼────────┐
│ Jellyfin.Data   │             │ MediaBrowser     │
│ EF Core DbCtx   │             │ MediaEncoding    │
│ SQLite via      │             │ FFmpeg, HLS,     │
│ Microsoft.Data  │             │ Trickplay        │
│ .Sqlite         │             └─────────────────┘
└─────────────────┘
         │
┌────────▼─────────────────────────────────────────────────┐
│              MediaBrowser.Model                           │
│   Pure DTOs, enums, no logic (shared by all layers)      │
└──────────────────────────────────────────────────────────┘
```

---

## Project Responsibilities

### `Jellyfin.Server`

Entry point. Handles:
- CLI argument parsing (`CommandLineParser`)
- Serilog configuration (console, file, Graylog sinks)
- DI container wiring (`ApplicationHost`)
- ASP.NET Core host startup

### `Jellyfin.Api`

All HTTP surface. Handles:
- ASP.NET Core controllers (`Controllers/`)
- Authentication middleware (`Auth/`)
- Swashbuckle/OpenAPI configuration
- Request/response formatting (camelCase + PascalCase JSON)
- WebSocket listeners (`WebSocketListeners/`)

Controllers inherit from `BaseJellyfinApiController` which sets default route, produces JSON, and provides typed `Ok<T>()` helpers.

### `MediaBrowser.Controller`

Core domain interfaces. Key examples:
- `ILibraryManager` — media library operations
- `IMediaEncoder` — FFmpeg wrapper
- `IProviderManager` — metadata provider coordination
- `IUserManager` — user management
- `IPlaybackManager` — playback session tracking

**No implementations live here.** This keeps the domain decoupled from infrastructure.

### `Emby.Server.Implementations`

Primary implementation assembly. Contains:
- `ApplicationHost.cs` — DI wiring and startup
- `Data/` — SQLite queries and EF Core repositories
- `Library/` — `LibraryManager`, `LibraryMonitor`
- `Images/` — image processing pipeline (SkiaSharp)
- `HttpServer/` — HTTP server wiring

### `Jellyfin.Server.Implementations`

Secondary implementation assembly split from `Emby.Server.Implementations`. Contains newer implementations using EF Core patterns.

### `Jellyfin.Data`

EF Core data models and `DbContext`. Migrations managed here.

### `MediaBrowser.Model`

Pure data-transfer objects (DTOs) and enums. No logic. Consumed by all layers and by external clients. Changes here are API-breaking.

### `MediaBrowser.Providers`

Online metadata providers:
- TMDB (movies, TV)
- MusicBrainz (audio)
- OMDB
- TV Maze, TheTVDB

Uses `IMetadataProvider<T>` interface from `MediaBrowser.Controller`.

### `MediaBrowser.MediaEncoding`

FFmpeg process management, HLS streaming, keyframe extraction, subtitle transcoding, trickplay image generation.

### `Emby.Naming`

Media file path parsing — resolves series/season/episode structure, detects extras, parses video codecs from filenames.

### `MediaBrowser.LocalMetadata` / `MediaBrowser.XbmcMetadata`

Local NFO/XML metadata providers (Kodi-compatible `.nfo` sidecar files).

### `src/Jellyfin.CodeAnalysis`

Custom Roslyn analyzer. Runs only in Debug builds. Enforces project-specific rules.

---

## Key Subsystems

### Authentication

- Session-based API keys (stored in SQLite)
- Quick Connect (pairing flow)
- Auth middleware in `Jellyfin.Api/Auth/`
- Policies defined in `Jellyfin.Api/Constants/Policies.cs`

### Library Scanning

1. `LibraryMonitor` watches filesystem for changes
2. `LibraryManager` resolves paths → `BaseItem` subclasses
3. `Emby.Naming` parses filenames → metadata hints
4. `IProviderManager` fetches remote metadata and saves locally
5. Results persisted to SQLite via EF Core

### Transcoding

1. Client requests a stream via `MediaInfoController` or `DynamicHlsController`
2. `MediaInfoHelper` determines if transcoding is needed (codec matrix)
3. `MediaEncoder` spawns an FFmpeg subprocess with computed arguments
4. HLS segments or direct stream served via `AudioController` / `VideosController`

### Metrics

prometheus-net serves metrics at `/metrics`. Key meters:
- `prometheus-net.AspNetCore` — HTTP request duration/count
- `prometheus-net.DotNetRuntime` — GC, thread pool, JIT metrics
- Custom counters can be added via `Metrics.CreateCounter(...)` in any service

### Logging

Serilog pipeline:
- Console sink (structured)
- File sink (rolling, default `%APPDATA%/jellyfin/logs/`)
- Graylog GELF sink (optional, configured via `logging.json`)

---

## Database

SQLite database at `{DataDir}/data/jellyfin.db`. Accessed via:
- EF Core (`Jellyfin.Data.JellyfinDbContext`) for new data access
- `Microsoft.Data.Sqlite` direct queries for legacy paths

**All EF Core operations must use async methods** (`ToListAsync`, `FirstOrDefaultAsync`, etc.).

---

## Test Layout

```
tests/
  Jellyfin.Api.Tests/                 Controller + middleware unit tests
  Jellyfin.Common.Tests/              MediaBrowser.Common utilities
  Jellyfin.Controller.Tests/          Interface contracts and helpers
  Jellyfin.MediaEncoding.Tests/       FFmpeg argument building
  Jellyfin.Naming.Tests/              File path parsing
  Jellyfin.Providers.Tests/           Provider logic
  Jellyfin.Server.Integration.Tests/  Full-stack HTTP tests + OpenAPI spec gen
  Jellyfin.Server.Tests/              Server startup and DI tests
```

Test stack: xUnit + AutoFixture + Moq + FsCheck. See `.github/instructions/testing.instructions.md`.
