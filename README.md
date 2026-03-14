# jellyfin-ha

**A fork of [Jellyfin](https://github.com/jellyfin/jellyfin) adding high-availability transcoding support for multi-pod Kubernetes deployments.**

[![License: GPL v2](https://img.shields.io/badge/License-GPL_v2-blue.svg)](https://www.gnu.org/licenses/old-licenses/gpl-2.0.en.html)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Upstream](https://img.shields.io/badge/upstream-jellyfin%2Fjellyfin-informational)](https://github.com/jellyfin/jellyfin)

---

## What is this?

Jellyfin's default assumption is that exactly one server instance is running at a time. Transcode state is held entirely in-memory — when the process dies, so do all active HLS streams. For homelab deployments that want Kubernetes-managed redundancy (rolling restarts, node drain, pod rescheduling), that's a problem.

This fork adds a thin HA layer on top of unmodified Jellyfin core:

- **`ITranscodeSessionStore`** — a new interface for durable, distributed transcode session tracking
- **`RedisTranscodeSessionStore`** — a Redis-backed implementation using atomic Lua takeover scripts and TTL-based lease expiry
- **`NullTranscodeSessionStore`** — a no-op fallback so single-instance deployments work with zero configuration change
- **Lease-aware `DeleteTranscodeFileTask`** — coordinates cleanup across replicas so a restarting pod doesn't delete segments another pod is actively streaming
- **`SessionManager` HA recovery** — safe takeover of live HLS streams when a pod takes over after lease expiry
- **PostgreSQL database provider** — alternative to SQLite for shared-database HA setups (experimental, under `src/Jellyfin.Database/Jellyfin.Database.Providers.PostgreSQL`)

---

## Architecture

```
┌─────────────┐     ┌─────────────┐
│  Jellyfin   │     │  Jellyfin   │
│   Pod A     │     │   Pod B     │
│             │     │             │
│ ┌─────────┐ │     │ ┌─────────┐ │
│ │Transcode│ │     │ │Transcode│ │
│ │Manager  │ │     │ │Manager  │ │
│ └────┬────┘ │     │ └────┬────┘ │
└──────┼──────┘     └──────┼──────┘
       │                   │
       └─────────┬─────────┘
                 │
         ┌───────▼───────┐
         │  Redis        │   ← ITranscodeSessionStore
         │  (lease store)│     TTL-based ownership
         └───────────────┘

       ┌─────────────────────┐
       │  Shared NAS / NFS   │   ← HLS segments + manifests
       │  (shared storage)   │
       └─────────────────────┘
```

**How takeover works:**

1. Pod A starts an HLS transcode and writes a `TranscodeSession` to Redis with a 30-second lease.
2. Pod A renews the lease every `LeaseDurationSeconds / 2` seconds.
3. If Pod A dies, the lease expires in Redis after 30 seconds.
4. Pod B receives a client request for the same play session, calls `TryTakeoverAsync`, and atomically claims ownership via a Lua script.
5. Pod B resumes FFmpeg from the last durable segment index. The client sees a brief stutter, not an error.

---

## Quick Start

### Single instance (no Redis)

No configuration required. `NullTranscodeSessionStore` is used automatically. Behavior is identical to upstream Jellyfin.

```bash
dotnet run --project Jellyfin.Server/Jellyfin.Server.csproj -- \
  --datadir /var/lib/jellyfin \
  --webdir /usr/share/jellyfin/web
```

### HA mode with Redis

Set the `Jellyfin:TranscodeStore:RedisConnectionString` configuration key. You can pass it as an environment variable, a `DOTNET_` prefixed env var, or in a JSON config file.

**Environment variable:**

```bash
export Jellyfin__TranscodeStore__RedisConnectionString="redis:6379"
export Jellyfin__TranscodeStore__LeaseDurationSeconds="30"

dotnet run --project Jellyfin.Server/Jellyfin.Server.csproj -- \
  --datadir /var/lib/jellyfin \
  --webdir /usr/share/jellyfin/web
```

**`appsettings.json` section:**

```json
{
  "Jellyfin": {
    "TranscodeStore": {
      "RedisConnectionString": "redis:6379,abortConnect=false",
      "LeaseDurationSeconds": 30
    }
  }
}
```

When `RedisConnectionString` is set, `RedisTranscodeSessionStore` is registered in DI. If the Redis connection fails at startup, the server throws and refuses to start — this is intentional so you don't silently fall back to broken HA behavior.

---

## Configuration Reference

| Key | Default | Description |
|-----|---------|-------------|
| `Jellyfin:TranscodeStore:RedisConnectionString` | _(empty)_ | StackExchange.Redis connection string. Empty = single-instance mode. |
| `Jellyfin:TranscodeStore:LeaseDurationSeconds` | `30` | How long a pod's transcode lease is valid before another pod may take over. |

### Redis connection string examples

```
# Standalone Redis
redis:6379

# With password
redis:6379,password=secret

# With TLS
redis.example.com:6380,ssl=true,abortConnect=false

# Redis Sentinel
sentinel-host:26379,serviceName=mymaster
```

Standard [StackExchange.Redis connection string format](https://stackexchange.github.io/StackExchange.Redis/Configuration) is accepted.

---

## Docker / Kubernetes

The `Dockerfile.runtime` in this repo produces a runtime-only image. The `.NET publish` step is intended to run on the CI host (not inside Docker) for I/O performance reasons.

```bash
# Build locally
dotnet publish Jellyfin.Server/Jellyfin.Server.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained false \
  --output ./publish-output

# Build image
docker build -f Dockerfile.runtime -t jellyfin-ha:local \
  --platform linux/amd64 .
```

**Kubernetes environment variables for HA:**

```yaml
env:
  - name: Jellyfin__TranscodeStore__RedisConnectionString
    valueFrom:
      secretKeyRef:
        name: jellyfin-redis
        key: connection-string
  - name: Jellyfin__TranscodeStore__LeaseDurationSeconds
    value: "30"
  - name: JELLYFIN_HA_POD_NAME
    valueFrom:
      fieldRef:
        fieldPath: metadata.name
```

Shared storage (NFS, Longhorn RWX, or similar) must be mounted at the same path on all pods for segment file access to work across pod boundaries.

---

## PostgreSQL (experimental)

This fork includes a PostgreSQL database provider under `src/Jellyfin.Database/Jellyfin.Database.Providers.PostgreSQL`. It is experimental — the SQLite provider remains the default and the recommended choice for most deployments.

To use PostgreSQL, set the migration provider at startup and run migrations:

```bash
dotnet ef migrations add InitialCreate \
  --project "src/Jellyfin.Database/Jellyfin.Database.Providers.PostgreSQL" \
  -- --migration-provider Jellyfin-PostgreSQL
```

See `src/Jellyfin.Database/readme.md` for full migration instructions.

---

## Building and Testing

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build

```bash
dotnet build Jellyfin.Server/Jellyfin.Server.csproj
```

### Run all tests

```bash
dotnet test Jellyfin.sln \
  --configuration Release \
  --filter "Category!=RequiresDocker&FullyQualifiedName!~Integration"
```

### Run HA-specific tests

The transcode session store and HA recovery tests live in:

- `tests/Jellyfin.Server.Implementations.Tests/MediaEncoding/RedisTranscodeSessionStoreTests.cs`
- `tests/Jellyfin.MediaEncoding.Tests/Fakes/InMemoryTranscodeSessionStore.cs`

```bash
dotnet test tests/Jellyfin.Server.Implementations.Tests \
  --configuration Release \
  --filter "FullyQualifiedName~TranscodeSession"
```

### Run with code coverage

```bash
dotnet test Jellyfin.sln \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --settings tests/coverletArgs.runsettings
```

---

## Project Structure

```
MediaBrowser.Controller/MediaEncoding/
  ITranscodeSessionStore.cs        ← Interface (DI contract)
  TranscodeSession.cs              ← Session record model
  TranscodeStoreOptions.cs         ← Configuration options
  NullTranscodeSessionStore.cs     ← No-op, single-instance fallback

Emby.Server.Implementations/MediaEncoding/
  RedisTranscodeSessionStore.cs    ← Redis-backed HA implementation

src/Jellyfin.Database/
  Jellyfin.Database.Providers.PostgreSQL/  ← Experimental PostgreSQL provider

tests/
  Jellyfin.Server.Implementations.Tests/MediaEncoding/
    RedisTranscodeSessionStoreTests.cs
  Jellyfin.MediaEncoding.Tests/Fakes/
    InMemoryTranscodeSessionStore.cs
```

---

## Contributing

This is a personal experiment, not an officially maintained fork. Issues and PRs are welcome but response time may vary.

If you're interested in getting proper HA transcoding into upstream Jellyfin, that conversation belongs in the [upstream repo](https://github.com/jellyfin/jellyfin). The changes here are deliberately narrow and designed to be upstream-friendly if there's maintainer interest.

**Code conventions** follow the upstream Jellyfin rules:
- `async`/`await` everywhere — no `.Result` or `.Wait()`
- All public members need XML doc comments
- Use `Directory.Packages.props` for NuGet versions — never add `Version=` to a `<PackageReference>`
- `.NET 10` required
- Warnings are treated as errors

---

## Relationship to upstream

This fork tracks [jellyfin/jellyfin](https://github.com/jellyfin/jellyfin) `master`. The HA additions are intentionally isolated to:

1. New interfaces and models in `MediaBrowser.Controller`
2. New implementations in `Emby.Server.Implementations`
3. DI wiring in `Jellyfin.Server/CoreAppHost.cs`
4. New test projects

No core Jellyfin logic was modified — only extended via existing DI extension points.

---

## License

GPL-2.0, same as upstream Jellyfin. See [LICENSE](LICENSE).

---

*Upstream README preserved below for reference.*

---

Instructions to run this project from the command line are included here, but you will also need to install an IDE if you want to debug the server while it is running. Any IDE that supports .NET 6 development will work, but two options are recent versions of [Visual Studio](https://visualstudio.microsoft.com/downloads/) (at least 2022) and [Visual Studio Code](https://code.visualstudio.com/Download).

[ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) will also need to be installed.

### Cloning the Repository

After dependencies have been installed you will need to clone a local copy of this repository. If you just want to run the server from source you can clone this repository directly, but if you are intending to contribute code changes to the project, you should [set up your own fork](https://jellyfin.org/docs/general/contributing/development.html#set-up-your-copy-of-the-repo) of the repository. The following example shows how you can clone the repository directly over HTTPS.

```bash
git clone https://github.com/jellyfin/jellyfin.git
```

### Installing the Web Client

The server is configured to host the static files required for the [web client](https://github.com/jellyfin/jellyfin-web) in addition to serving the backend by default. Before you can run the server, you will need to get a copy of the web client since they are not included in this repository directly.

Note that it is recommended for development to [host the web client separately](#hosting-the-web-client-separately) from the web server with some additional configuration, in which case you can skip this step.

There are two options to get the files for the web client.

1. Build them from source following the instructions on the [jellyfin-web repository](https://github.com/jellyfin/jellyfin-web)
2. Get the pre-built files from an existing installation of the server. For example, with a Windows server installation the client files are located at `C:\Program Files\Jellyfin\Server\jellyfin-web`

### Running The Server

The following instructions will help you get the project up and running via the command line, or your preferred IDE.

#### Running With Visual Studio

To run the project with Visual Studio you can open the Solution (`.sln`) file and then press `F5` to run the server.

#### Running With Visual Studio Code

To run the project with Visual Studio Code you will first need to open the repository directory with Visual Studio Code using the `Open Folder...` option.

Second, you need to [install the recommended extensions for the workspace](https://code.visualstudio.com/docs/editor/extension-gallery#_recommended-extensions). Note that extension recommendations are classified as either "Workspace Recommendations" or "Other Recommendations", but only the "Workspace Recommendations" are required.

After the required extensions are installed, you can run the server by pressing `F5`.

#### Running From the Command Line

To run the server from the command line you can use the `dotnet run` command. The example below shows how to do this if you have cloned the repository into a directory named `jellyfin` (the default directory name) and should work on all operating systems.

```bash
cd jellyfin                          # Move into the repository directory
dotnet run --project Jellyfin.Server --webdir /absolute/path/to/jellyfin-web/dist # Run the server startup project
```

A second option is to build the project and then run the resulting executable file directly. When running the executable directly you can easily add command line options. Add the `--help` flag to list details on all the supported command line options.

1. Build the project

```bash
dotnet build                       # Build the project
cd Jellyfin.Server/bin/Debug/net10.0 # Change into the build output directory
```

2. Execute the build output. On Linux, Mac, etc. use `./jellyfin` and on Windows use `jellyfin.exe`.

#### Accessing the Hosted Web Client

If the Server is configured to host the Web Client, and the Server is running, the Web Client can be accessed at `http://localhost:8096` by default.

API documentation can be viewed at `http://localhost:8096/api-docs/swagger/index.html`


### Running from GitHub Codespaces

As Jellyfin will run on a container on a GitHub hosted server, JF needs to handle some things differently.

**NOTE:** Depending on the selected configuration (if you just click 'create codespace' it will create a default configuration one) it might take 20-30 seconds to load all extensions and prepare the environment while VS Code is already open. Just give it some time and wait until you see `Downloading .NET version(s) 7.0.15~x64 ...... Done!` in the output tab.

**NOTE:** If you want to access the JF instance from outside, like with a WebClient on another PC, remember to set the "ports" in the lower VS Code window to public.

**NOTE:** When first opening the server instance with any WebUI, you will be sent to the login instead of the setup page. Refresh the login page once and you should be redirected to the Setup.

There are two configurations for you to choose from.
#### Default - Development Jellyfin Server
This creates a container that has everything to run and debug the Jellyfin Media server but does not setup anything else. Each time you create a new container you have to run through the whole setup again. There is also no ffmpeg, webclient or media preloaded. Use the `.NET Launch (nowebclient)` launch config to start the server.

> Keep in mind that as this has no web client you have to connect to it via an external client. This can be just another codespace container running the WebUI. vuejs does not work from the get-go as it does not support the setup steps.

#### Development Jellyfin Server ffmpeg
this extends the default server with a default installation of ffmpeg6 though the means described here: https://jellyfin.org/docs/general/installation/linux#repository-manual
If you want to install a specific ffmpeg version, follow the comments embedded in the `.devcontainer/Dev - Server Ffmpeg/install.ffmpeg.sh` file.

Use the `ghcs .NET Launch (nowebclient, ffmpeg)` launch config to run with the jellyfin-ffmpeg enabled.


### Running The Tests

This repository also includes unit tests that are used to validate functionality as part of a CI pipeline on Azure. There are several ways to run these tests.

1. Run tests from the command line using `dotnet test`
2. Run tests in Visual Studio using the [Test Explorer](https://docs.microsoft.com/en-us/visualstudio/test/run-unit-tests-with-test-explorer)
3. Run individual tests in Visual Studio Code using the associated [CodeLens annotation](https://github.com/OmniSharp/omnisharp-vscode/wiki/How-to-run-and-debug-unit-tests)

### Advanced Configuration

The following sections describe some more advanced scenarios for running the server from source that build upon the standard instructions above.

#### Hosting The Web Client Separately

It is not necessary to host the frontend web client as part of the backend server. Hosting these two components separately may be useful for frontend developers who would prefer to host the client in a separate webpack development server for a tighter development loop. See the [jellyfin-web](https://github.com/jellyfin/jellyfin-web#getting-started) repo for instructions on how to do this.

To instruct the server not to host the web content, there is a `nowebclient` configuration flag that must be set. This can be specified using the command line
switch `--nowebclient` or the environment variable `JELLYFIN_NOWEBCONTENT=true`.

Since this is a common scenario, there is also a separate launch profile defined for Visual Studio called `Jellyfin.Server (nowebcontent)` that can be selected from the 'Start Debugging' dropdown in the main toolbar.

**NOTE:** The setup wizard cannot be run if the web client is hosted separately.

---
<p align="center">
This project is supported by:
<br/>
<br/>
<a href="https://www.digitalocean.com"><img src="https://opensource.nyc3.cdn.digitaloceanspaces.com/attribution/assets/SVG/DO_Logo_horizontal_blue.svg" height="50px" alt="DigitalOcean"></a>
    &nbsp;
<a href="https://www.jetbrains.com"><img src="https://gist.githubusercontent.com/anthonylavado/e8b2403deee9581e0b4cb8cd675af7db/raw/199ae22980ef5da64882ec2de3e8e5c03fe535b8/jetbrains.svg" height="50px" alt="JetBrains logo"></a>
</p>
