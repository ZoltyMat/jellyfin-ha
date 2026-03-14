# Fork Diff: `ZoltyMat/jellyfin-ha` vs `jellyfin/jellyfin`

> **Generated:** 2026-03-14  
> **Base:** `upstream/master` (`jellyfin/jellyfin`)  
> **Head:** `origin/main` (`ZoltyMat/jellyfin-ha`)  
> **Summary:** 40 commits ahead · 49 files changed · +9,879 / -93 lines

---

## What changed and why

This fork adds a **high-availability transcoding layer** on top of unmodified Jellyfin core. The design principle: extend via DI, touch as little upstream code as possible. No core business logic was rewritten.

Changes fall into five buckets:

| Bucket | Files | Lines added |
|--------|-------|-------------|
| New HA interfaces and models | 5 | ~260 |
| Redis session store implementation | 1 | ~270 |
| Modified upstream files (DI wiring + HA hooks) | 4 | ~300 |
| PostgreSQL database provider (experimental) | 7 | ~3,200 |
| Tests | 9 | ~2,000 |
| Tooling (DbMigrator, CI, Docker) | 12 | ~800 |
| Docs | 3 | ~1,100 |

---

## New files (net additions, no upstream equivalent)

### HA Session Store

#### `MediaBrowser.Controller/MediaEncoding/ITranscodeSessionStore.cs` (+104)

New interface. The DI contract for durable transcode session tracking.

```csharp
public interface ITranscodeSessionStore
{
    Task<TranscodeSession?> TryGetAsync(string playSessionId, CancellationToken ct);
    Task<bool> TryTakeoverAsync(string playSessionId, string claimingPod, CancellationToken ct);
    Task SetAsync(TranscodeSession session, CancellationToken ct);
    Task RenewLeaseAsync(string playSessionId, CancellationToken ct);
    Task DeleteAsync(string playSessionId, CancellationToken ct);
    Task<IReadOnlyList<TranscodeSession>> GetAllAsync(CancellationToken ct);
}
```

#### `MediaBrowser.Controller/MediaEncoding/TranscodeSession.cs` (+49)

The session record stored in Redis. Tracks ownership (`OwnerPod`), lease expiry, manifest path, segment path prefix, and the last durable segment index for resuming FFmpeg after failover.

#### `MediaBrowser.Controller/MediaEncoding/TranscodeStoreOptions.cs` (+19)

Configuration model. Two fields: `RedisConnectionString` (null/empty = single-instance mode) and `LeaseDurationSeconds` (default 30).

#### `MediaBrowser.Controller/MediaEncoding/NullTranscodeSessionStore.cs` (+49)

No-op implementation. Registered when `RedisConnectionString` is not configured. Single-instance deployments get identical behavior to upstream.

#### `MediaBrowser.Controller/MediaEncoding/LiveStreamSession.cs` (+36)

Model for tracking live stream sessions alongside transcode sessions in the Redis store.

#### `Emby.Server.Implementations/MediaEncoding/RedisTranscodeSessionStore.cs` (+270)

Redis-backed implementation of `ITranscodeSessionStore`. Key design points:

- Sessions stored as JSON under `jellyfin:transcode:{playSessionId}`
- Live stream sessions under `jellyfin:livestream:{sessionId}`
- Lease takeover is atomic via a Lua script (Redis single-threaded script execution guarantees no race between concurrent pods)
- TTL on the Redis key mirrors `LeaseExpiresUtc` — Redis GCs orphaned sessions automatically

```lua
-- Takeover script: atomically checks lease expiry and transfers ownership
local raw = redis.call('GET', KEYS[1])
if not raw then return 0 end
local session = cjson.decode(raw)
local currentTicks = tonumber(ARGV[1])
if session['LeaseExpiresUtc'] > currentTicks then return 0 end
session['OwnerPod'] = ARGV[2]
-- ... update expiry and SET atomically
return 1
```

---

### PostgreSQL Provider (experimental)

#### `src/Jellyfin.Database/Jellyfin.Database.Providers.PostgreSQL/` (+~3,200 lines)

A complete EF Core database provider for PostgreSQL, parallel to the existing SQLite provider:

- `PostgreSqlDatabaseProvider.cs` — implements `IDatabaseProvider`, configures Npgsql, handles migrations
- `PostgreSqlDesignTimeJellyfinDbFactory.cs` — EF Core design-time factory for `dotnet ef migrations`
- `Migrations/20260305010333_InitialPostgreSql.cs` — full initial schema migration (~1,146 lines)
- `Migrations/JellyfinDbContextModelSnapshot.cs` — EF Core model snapshot

Registered at startup when the PostgreSQL provider is selected. Falls back to SQLite by default — no behavioral change for existing deployments.

#### `tools/Jellyfin.DbMigrator/` (+~660 lines)

CLI tool to migrate an existing SQLite Jellyfin database to PostgreSQL:

- `Program.cs` — reads SQLite source, writes to PostgreSQL target
- `SqliteTableReader.cs` — reads all tables and rows from SQLite
- `PostgresBulkWriter.cs` — bulk-inserts via `NpgsqlBinaryImporter` (COPY protocol)
- `MigrationReport.cs` — structured migration result logging
- `TableNameValidator.cs` — validates table names against allowlist to prevent injection

---

## Modified upstream files

### `Jellyfin.Server/CoreAppHost.cs` (+31)

DI wiring. Reads `Jellyfin:TranscodeStore` config section and registers either `RedisTranscodeSessionStore` or `NullTranscodeSessionStore`:

```diff
+           serviceCollection.Configure<TranscodeStoreOptions>(
+               _startupConfig.GetSection("Jellyfin:TranscodeStore"));
+           var redisConnectionString =
+               _startupConfig["Jellyfin:TranscodeStore:RedisConnectionString"];
+           if (!string.IsNullOrEmpty(redisConnectionString))
+           {
+               serviceCollection.AddSingleton<IConnectionMultiplexer>(...);
+               serviceCollection.AddSingleton<ITranscodeSessionStore,
+                   RedisTranscodeSessionStore>();
+           }
+           else
+           {
+               serviceCollection.AddSingleton<ITranscodeSessionStore,
+                   NullTranscodeSessionStore>();
+           }
```

### `Emby.Server.Implementations/Tasks/DeleteTranscodeFileTask.cs` (+58 / -3)

Lease-aware cleanup. Before deleting transcode temp files, checks Redis for a valid (non-expired) lease. If the session is still active on another pod, cleanup is skipped for that session.

```diff
+        // HA guard: do not delete files belonging to a session with a valid lease
+        // on another pod. Only clean up sessions with no Redis entry or an expired lease.
+        var session = await _transcodeSessionStore
+            .TryGetAsync(playSessionId, cancellationToken).ConfigureAwait(false);
+        if (session is not null && session.LeaseExpiresUtc > DateTime.UtcNow)
+        {
+            continue;
+        }
```

### `Emby.Server.Implementations/Session/SessionManager.cs` (+62 / -3)

HA recovery hooks. `_activeLiveStreamSessions` is now checked against the Redis store during takeover — a pod that receives a request for a live stream it doesn't own locally can attempt `TryTakeoverAsync` before starting a new FFmpeg process.

### `Jellyfin.Api/Controllers/DynamicHlsController.cs` (+146 / -47)

HLS session registration. When a new HLS transcode starts, `SetAsync` is called to register the session in Redis. During segment requests, `RenewLeaseAsync` extends the lease. On stop/cleanup, `DeleteAsync` removes the session. The controller now injects `ITranscodeSessionStore` via constructor DI.

---

## CI and Docker

### `.github/workflows/ha-build.yml` (new, +90)

Build-and-push workflow for the fork image. Runs on push to `main`/`feat/ha-*`/`copilot/*`. Publishes with `dotnet publish` on the runner host (not inside Docker), then builds the runtime image and pushes to ECR.

### `.github/workflows/ci-tests.yml` (+87 / -5)

Extended with a `run-phase5-tests` parallel job targeting the three test assemblies most affected by HA changes: `Jellyfin.Api.Tests`, `Jellyfin.MediaEncoding.Hls.Tests`, and `Jellyfin.Server.Implementations.Tests`.

### `Dockerfile.runtime` (new, +56)

Runtime-only image. Expects a pre-built `publish-output/` directory (produced by `dotnet publish` on the CI host). Installs `jellyfin-web` from the official Jellyfin apt repo. Builds for `linux/amd64` only.

---

## Tests

| Test file | What it covers |
|-----------|----------------|
| `RedisTranscodeSessionStoreTests.cs` (+384) | Set, get, takeover, renew, delete, concurrent takeover races |
| `DeleteTranscodeFileTaskTests.cs` (+429) | Lease-aware cleanup: active lease skips deletion, expired lease allows deletion |
| `DynamicHlsHaTakeoverTests.cs` (+259) | Controller registers sessions, renews on segment requests, takeover path |
| `DynamicHlsSessionRegistrationTests.cs` (+223) | Session lifecycle: create, renew, delete through HLS controller |
| `TranscodeManagerTests.cs` (+167) | TranscodeManager calls store on begin/end transcode |
| `PostgreSqlProviderTests.cs` (+336) | PostgreSQL provider DI, migration, CRUD roundtrip |
| `PostgreSqlConcurrencyTests.cs` (+126) | Concurrent writes under PostgreSQL |
| `PostgreSqlMigrationTests.cs` (+99) | Migration from SQLite via DbMigrator tool |
| `InMemoryTranscodeSessionStore.cs` (+169) | Test fake used across all HA unit tests |

---

## What was NOT changed

- No core media scanning or library logic
- No changes to the Jellyfin data model or existing EF Core SQLite migrations
- No changes to the Jellyfin plugin system
- No changes to the authentication or user management stack
- No changes to the Jellyfin web client (separate repo)
- No changes to subtitle, image, or metadata providers

The HA layer is fully additive. Removing it would require deleting the new files and the ~30-line DI block in `CoreAppHost.cs`.

---

## Diff commands

```bash
# Add the upstream remote
git remote add upstream https://github.com/jellyfin/jellyfin.git
git fetch upstream master

# Full file-level summary
git diff upstream/master...HEAD --stat

# New files only
git diff upstream/master...HEAD --name-only --diff-filter=A

# Full patch (large — ~10k lines)
git diff upstream/master...HEAD > fork.patch
```
