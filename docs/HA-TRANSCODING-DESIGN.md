# HA Transcoding Design — Phase 5.1.1 Audit

> **Status**: Design audit only. No functional code changes in this document.
> **Purpose**: Map the exact transcode lifecycle before Phase 5.2 code changes begin.
> **Last updated**: 2026-03-07

## Table of Contents

1. [Sequence Diagram: Full Transcode Lifecycle](#sequence-diagram-full-transcode-lifecycle)
2. [Key In-Memory State Fields](#key-in-memory-state-fields)
3. [Why `playSessionId` Is Insufficient](#why-playsessionid-is-insufficient)
4. [Why `DeleteTranscodeFileTask` Is Unsafe for Shared Storage](#why-deletetranscodfiletask-is-unsafe-for-shared-storage)
5. [How `SessionManager._activeLiveStreamSessions` Works](#how-sessionmanager_activelivestreamsessions-works)
6. [NFSv3 Lock Recovery on Pod Death](#nfsv3-lock-recovery-on-pod-death)
7. [Minimum Recovery State](#minimum-recovery-state)
8. [HA Failure Scenario Walk-Through](#ha-failure-scenario-walk-through)
9. [Open Questions Before Phase 5.2](#open-questions-before-phase-52)
10. [Cross-References](#cross-references)

---

## Sequence Diagram: Full Transcode Lifecycle

The following describes the path from a client HLS manifest request through
FFmpeg startup to segment delivery and session cleanup.

```
Client                     DynamicHlsController         StreamingHelpers          TranscodeManager
  |                                |                           |                         |
  | GET /Videos/{id}/live.m3u8     |                           |                         |
  |------------------------------->|                           |                         |
  |                                | GetStreamingState()       |                         |
  |                                |-------------------------->|                         |
  |                                |   StreamState             |                         |
  |                                |<--------------------------|                         |
  |                                |                           |                         |
  |                                | File.Exists(playlistPath)?|                         |
  |                                |---------- NO ---------->  |                         |
  |                                |                           |                         |
  |                                | LockAsync(playlistPath)   |                         |
  |                                |--------------------------------------------->|     |
  |                                |   (async keyed lock held) |                   |     |
  |                                |                           |                   |     |
  |                                | StartFfMpeg(state, ...)   |                         |
  |                                |------------------------------------------>|         |
  |                                |                           |          OnTranscodeBeginning()
  |                                |                           |          _activeTranscodingJobs.Add(job)
  |                                |                           |          Process.Start(ffmpeg)
  |                                |   TranscodingJob          |                         |
  |                                |<------------------------------------------|         |
  |                                |                           |                         |
  |                                | WaitForMinimumSegmentCount() (if minSegments > 0)  |
  |                                |------------------------------------------  ...  ---|
  |                                |                           |                         |
  | 200 OK (m3u8 playlist text)    |                           |                         |
  |<-------------------------------|                           |                         |
  |                                |                           |                         |
  | GET /Videos/{id}/hls/segment0.ts                          |                         |
  |------------------------------->|                           |                         |
  |                                | GetStreamingState()       |                         |
  |                                |-------------------------->|                         |
  |                                |                           |                         |
  |                                | File.Exists(playlistPath)?|                         |
  |                                |---------- YES ----------> |                         |
  |                                |                           |                         |
  |                                | OnTranscodeBeginRequest(playlistPath, type)          |
  |                                |------------------------------------------>|         |
  |                                |   job (from _activeTranscodingJobs by path)         |
  |                                |<------------------------------------------|         |
  |                                |                           |                         |
  |                                | PingTranscodingJob(playSessionId)                   |
  |                                | (resets kill timer, marks active)                   |
  |                                |                           |                         |
  | 200 OK (segment data)          |                           |                         |
  |<-------------------------------|                           |                         |
  |                                |                           |                         |
  | (client stops requesting)      |                           |                         |
  |                                |                           |                         |
  |          [kill timer fires after inactivity timeout]       |                         |
  |                                |                           |                         |
  |                                | OnTranscodeKillTimerStopped()                       |
  |                                |------------------------------------------>|         |
  |                                |            KillTranscodingJob(job, ...)             |
  |                                |            Process.Kill(ffmpeg)                     |
  |                                |            DeletePartialStreamFiles(path)            |
  |                                |            _activeTranscodingJobs.Remove(job)       |
```

### `GetStreamingState()` — What It Does

`StreamingHelpers.GetStreamingState()` (in `Jellyfin.Api/Helpers/StreamingHelpers.cs`)
constructs a `StreamState` object from the inbound `StreamingRequestDto`. It:

- Resolves the `MediaSourceInfo` for the request
- Computes `OutputFilePath` from `IApplicationPaths.TranscodePath` + a hash-derived subdirectory
- Applies encoding parameters from the request and the device profile
- Does **not** consult any durable store — state is recomputed from scratch on every request

### `StartFfMpeg()` — What It Does

`TranscodeManager.StartFfMpeg()` (line ~371, `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs`):

1. Calls `OnTranscodeBeginning()` → creates a `TranscodingJob`, adds it to `_activeTranscodingJobs`
2. Calls `AcquireResources()` (waits `MediaSource.BufferMs` if set)
3. Starts FFmpeg process with the generated command line
4. Calls `StartThrottler()` and `StartSegmentCleaner()` if applicable
5. Returns the `TranscodingJob` to the caller

### `OnTranscodeBeginRequest()` — What It Does

Called when the playlist already exists on disk. Looks up a job in `_activeTranscodingJobs`
by filesystem path and `TranscodingJobType`. Returns `null` if no matching in-memory job
exists (which is exactly the pod-takeover failure scenario).

---

## Key In-Memory State Fields

### `TranscodeManager._activeTranscodingJobs`

**Location**: `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs`, line 48

```csharp
private readonly List<TranscodingJob> _activeTranscodingJobs = new();
```

- Protected by `lock(_activeTranscodingJobs)` (monitor lock)
- **Process-local**: not shared between pods, not persisted to any durable store
- Contains one `TranscodingJob` per active FFmpeg process
- Looked up by `PlaySessionId` (string) or by path + type pair

Key `TranscodingJob` fields relevant to recovery:

| Field | Type | Notes |
|---|---|---|
| `PlaySessionId` | `string?` | Caller-supplied; can be null |
| `Path` | `string` | Absolute path to the m3u8 playlist file |
| `Type` | `TranscodingJobType` | `HLS`, `Progressive`, etc. |
| `DeviceId` | `string` | Client device identifier |
| `Process` | `Process?` | The live FFmpeg process handle |
| `IsLiveOutput` | `bool` | Set to `true` for live HLS streams |
| `Id` | `string` | `Guid.NewGuid().ToString("N")` — per-job, not durable |

### `SessionManager._activeLiveStreamSessions`

**Location**: `Emby.Server.Implementations/Session/SessionManager.cs`, line ~67

```csharp
private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _activeLiveStreamSessions
```

- Maps `liveStreamId → (sessionId → playSessionId)`
- Updated by `UpdateLiveStreamActiveSessionMappings()` (line ~849)
- Queried in media-open paths to prevent double-opening a live stream
- **Process-local**: cleared on pod shutdown (`_activeLiveStreamSessions.Clear()` on line ~2151)
- A takeover pod **cannot** inherit these mappings without explicit rehydration from a durable store

---

## Why `playSessionId` Is Insufficient

`playSessionId` is an **optional, caller-supplied** query parameter:

```csharp
// DynamicHlsController.cs, GetLiveHlsStream():
[FromQuery] string? playSessionId,
```

It is passed directly to `StreamingRequestDto.PlaySessionId` and from there into
`TranscodingJob.PlaySessionId`. This creates three failure modes for HA:

### Failure Mode 1: Two clients collide on the same ID

If two clients supply the same `playSessionId` string, `GetTranscodingJob(playSessionId)`
returns the first matching job regardless of which device owns it. The second client's
segment requests will ping the first client's kill timer, potentially extending an
unrelated session indefinitely.

### Failure Mode 2: `null` PlaySessionId is common

When the Jellyfin web client does not supply a `playSessionId`, the field is `null`.
`GetTranscodingJob(string playSessionId)` does an `OrdinalIgnoreCase` compare:

```csharp
return _activeTranscodingJobs.FirstOrDefault(j =>
    string.Equals(j.PlaySessionId, playSessionId, StringComparison.OrdinalIgnoreCase));
```

If `playSessionId` is null, `string.Equals(null, null)` returns `true`, so the lookup
returns the **first job in the list with a null PlaySessionId**, regardless of path,
device, or item. On a shared filesystem with two pods, this creates an ambiguity
between jobs running on different pods.

### Failure Mode 3: Insufficient as a durable recovery key

`playSessionId` is not generated by the server — it is client-supplied. There is no
guarantee it is present, globally unique, or stable across client reconnects. A durable
recovery store (Issue 5.2.1) must use a server-generated, correlation-stable key that
includes at minimum: server-assigned UUID, item ID, media source ID, and owner pod name.

---

## Why `DeleteTranscodeFileTask` Is Unsafe for Shared Storage

**Location**: `Emby.Server.Implementations/ScheduledTasks/Tasks/DeleteTranscodeFileTask.cs`

```csharp
public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
{
    var minDateModified = DateTime.UtcNow.AddDays(-1);
    // ...
    DeleteTempFilesFromDirectory(_configurationManager.GetTranscodePath(), minDateModified, ...);
    return Task.CompletedTask;
}

private void DeleteTempFilesFromDirectory(string directory, DateTime minDateModified, ...)
{
    var filesToDelete = _fileSystem.GetFiles(directory, true)
        .Where(f => _fileSystem.GetLastWriteTimeUtc(f) < minDateModified)    // ← age only
        .ToList();
    // deletes without any lease check
}
```

**Triggers**: startup + every 24h.

**Problem for shared NFS storage**: The task deletes *any* file not written to in the
last 24 hours. When a pod dies and a takeover pod attempts recovery, it needs to:

1. Read the existing `.m3u8` manifest to find segment path prefix
2. Determine the last fully-written `.ts` segment
3. Restart FFmpeg from one segment before that point

If those files have an `mtime` older than 24 hours (e.g., the original pod started an
overnight transcode), the cleanup task running on any pod that boots after 24h will
delete them before the recovery pod can read them. There is **no lease or ownership check**.

**Required fix (Phase 5.2.2b)**: Before deleting a file, check whether a valid recovery
lease exists in the durable store (`ITranscodeSessionStore`). Skip deletion for any path
covered by an active or recently-expired lease.

---

## How `SessionManager._activeLiveStreamSessions` Works

When a Jellyfin client opens a live stream, `OpenMediaSource()` calls
`UpdateLiveStreamActiveSessionMappings(liveStreamId, sessionId, playSessionId)`:

```csharp
// SessionManager.cs, line ~849
private void UpdateLiveStreamActiveSessionMappings(string liveStreamId, string sessionId, string playSessionId)
{
    var activeSessionMappings = _activeLiveStreamSessions.GetOrAdd(
        liveStreamId, _ => new ConcurrentDictionary<string, string>());
    activeSessionMappings[sessionId] = playSessionId;
}
```

This prevents two sessions from opening the same live stream without coordination. It is
consulted when another `OpenMediaSource` call arrives for the same `liveStreamId`.

**Why this breaks in HA**:

- The mapping lives only in the pod that originally opened the stream
- When the owning pod dies, active session mappings are gone
- A takeover pod has no record that liveStreamId `X` is in use
- `CloseLiveStream()` on pod B will never be called for a stream opened on pod A
- The live stream source (e.g., a TV tuner) may stay locked open indefinitely

**Recovery approach (Phase 5.2.1/5.3.1)**: The durable `ITranscodeSessionStore` must
persist `(liveStreamId → sessionId, playSessionId, ownerPod, openedAt)` and allow
takeover pods to query and claim abandoned streams.

---

## NFSv3 Lock Recovery on Pod Death

**NFS version confirmed**: `nfsvers=3` — from `kubernetes/apps/media/nfs-pv.yaml` mount
options used for all existing media NFS PersistentVolumes.

### NFSv3 Lock (`lockd`) Behavior on Pod Death

NFSv3 uses the Network Lock Manager (`lockd`) for advisory file locks. When a client
(pod) terminates:

1. The NFS client kernel module sends an `NSM` (Network Status Monitor) notification
   to the NFS server
2. The NFS server's `lockd` releases all locks held by that client after a grace period
   (typically the `sm-notify` retry window, default ~15s)
3. **Not guaranteed**: If the pod is killed abruptly (OOM/SIGKILL) and cannot send NSM
   notification, the NFS server detects the client has disappeared via TCP keep-alive
   timeout (typically 20–120s depending on server configuration)

### Implications for Segment Files

FFmpeg writes `.ts` files sequentially. A typical write pattern:

1. Open `segment_N.ts` for write
2. Write video/audio data (2–4 MB for a 2–4s segment)
3. Close and rename/flush

If the pod dies **mid-write** of `segment_N.ts`:

- The file may be 0 bytes, partially filled, or have a corrupted end
- NFSv3 does **not** guarantee close-to-open consistency for concurrent readers
  — another pod may see a stale cached version or a partial file
- The NFS server releases the lock within seconds to minutes, but the file
  content is not rolled back

**Recovery rule (must implement in Phase 5.2)**:

> When resuming from a manifest on shared storage, identify the last `.ts` segment
> that appears in the `.m3u8` `#EXTINF` entries AND is non-zero in size AND has a
> stable mtime (not being written). Restart FFmpeg from **one segment before** that
> point to ensure the last segment is re-written cleanly.

This is analogous to the WAL recovery principle: never trust the last write from a
crashed writer.

### NFS Lock Hold-Up on Active Pod

When a Jellyfin pod has an open file handle on the NFS mount and the NAS becomes
unreachable, NFSv3 with `hard` mount option (confirmed in existing PVs) will block
I/O indefinitely — the pod will not crash, but it will stall. This is the correct
behavior for transcode recovery: FFmpeg stalls rather than emitting corrupt segments.
Test this in Issue 5.1.2 NAS outage test.

---

## Minimum Recovery State

For a takeover pod to resume an orphaned transcode session, the following minimum
state must be durably stored (Phase 5.2.1):

| Field | Source | Why Needed |
|---|---|---|
| `sessionId` | server-generated UUID | Stable correlation key; not client-supplied |
| `playSessionId` | client-supplied (may be null) | Needed to match kill-timer pings |
| `ownerPod` | k8s `POD_NAME` env var | Identify which pod is current owner |
| `manifestPath` | `OutputFilePath` with `.m3u8` extension | Entry point for takeover pod |
| `segmentPathPrefix` | derived from `manifestPath` directory | Find `.ts` files |
| `mediaSourceId` | `StreamState.MediaSource.Id` | Re-open the same stream |
| `itemId` | `StreamState.Request.ItemId` | Re-construct `StreamingRequestDto` |
| `encodingParams` | serialized subset of `StreamState` | Restart FFmpeg with identical params |
| `lastHeartbeatUtc` | updated by owner pod on segment write | Orphan detection: > 120s = orphaned |
| `lastCompletedSegmentIndex` | updated on each segment flush | Recovery knows where to seek |
| `deviceId` | `StreamState.Request.DeviceId` | Kill-job scope on cleanup |

---

## HA Failure Scenario Walk-Through

### Scenario: Pod A dies mid-transcode, Pod B receives next segment request

```
Pod A (owner)                   Redis (durable store)       Pod B (takeover)
     |                                 |                         |
     | write sessionKey → Redis        |                         |
     |-------------------------------->|                         |
     |                                 |                         |
     | heartbeat every 30s             |                         |
     |-------------------------------->|                         |
     |                                 |                         |
    DIES (OOMKill / node drain)        |                         |
                                       |          GET segment_N+1.ts
                                       |<------------------------|
                                       |   session key exists    |
                                       |   lastHeartbeat > 120s ago
                                       |   ownerPod != me        |
                                       |                         |
                              [today, WITHOUT Phase 5.2]:        |
                                       |                         |
                                       |   _activeTranscodingJobs is empty on Pod B
                                       |   OnTranscodeBeginRequest() → null
                                       |   No ffmpeg started
                                       |   Client receives stale m3u8, then 404s on segment
                                       |   Playback stalls indefinitely
                                       |                         |
                              [with Phase 5.2]:                  |
                                       |                         |
                                       |   CAS: set ownerPod = pod-B |
                                       |<------------------------|
                                       |                         |
                                       |   recover from segment_N-1 |
                                       |   StartFfMpeg(resumeFrom=N-1)
                                       |<------------------------|
                                       |                         |
                                       |   client resumes from segment N-1 (~4s rewind)
```

### Current State (Without Phase 5.2)

1. Client sends `GET .../segment_100.ts` to pod B (Traefik sticky session cookie
   `jellyfin-server-id` routes to pod B because pod A is gone)
2. Pod B calls `GetStreamingState()` → computes same `OutputFilePath` (deterministic hash)
3. Pod B calls `File.Exists(playlistPath)` → **true** (file exists on NFS from pod A)
4. Pod B calls `OnTranscodeBeginRequest(playlistPath, HLS)` → **null** (no job in pod B's `_activeTranscodingJobs`)
5. `job is null` → `OnTranscodeEndRequest` not called, no ping, no FFmpeg restart
6. Pod B reads and returns the existing `.m3u8` from disk
7. Client requests segment 100 → pod B tries to serve `segment_100.ts`
   - If the file exists and is complete: **success** (but no new segments will be produced)
   - If the file does not exist yet (pod A was mid-write): **404**, client stalls

Without Phase 5.2, the transcode stream terminates on pod death. No recovery happens
automatically. The client must re-initiate playback from the beginning or from a
seek point.

---

## Open Questions Before Phase 5.2

| # | Question | Who Answers | When |
|---|---|---|---|
| Q1 | What is the actual `leasetime` configured on the Ugreen DXP4800 NFS server? (default 90s, but UGOS Pro may differ) | Issue 5.1.2 benchmark pod | 5.1.2 |
| Q2 | Does the NFS mount use `nfsvers=3` exclusively, or does UGOS Pro negotiate v4 when requested? | `nfsstat -m` in test pod | 5.1.2 |
| Q3 | What is the minimum HLS segment duration in practice? (affects recovery seek distance) | FFmpeg log inspection | 5.1.1 follow-on |
| Q4 | Does the Jellyfin web client re-supply a stable `playSessionId` on reconnect, or generate a new one? | Client code inspection | 5.2.2a |
| Q5 | Does `StackExchange.Redis` in the fork use connection multiplexing that survives pod address changes? | 5.2.1a implementation | 5.2.1a |

---

## Cross-References

- [jellyfin-ha-plan.md](../home_k3s_cluster/docs/jellyfin-ha/jellyfin-ha-plan.md) — overall HA plan and phase structure
- [jellyfin-ha-phase5-transcoding.md](../home_k3s_cluster/docs/jellyfin-ha/jellyfin-ha-phase5-transcoding.md) — Phase 5 issue list, rollback matrix, Go/No-Go preconditions
- [jellyfin-ha-failover-test.md](../home_k3s_cluster/docs/jellyfin-ha/jellyfin-ha-failover-test.md) — SLO baselines, failover test procedures
- [ci-cd.md](../home_k3s_cluster/docs/ci-cd.md) — Phase 5 CI/CD paths
- `MediaBrowser.MediaEncoding/Transcoding/TranscodeManager.cs` — `_activeTranscodingJobs`, `StartFfMpeg()`, `KillTranscodingJob()`
- `Jellyfin.Api/Controllers/DynamicHlsController.cs` — `GetLiveHlsStream()`, segment lookup
- `Emby.Server.Implementations/ScheduledTasks/Tasks/DeleteTranscodeFileTask.cs` — age-only cleanup
- `Emby.Server.Implementations/Session/SessionManager.cs` — `_activeLiveStreamSessions`
- `kubernetes/apps/media/nfs-pv.yaml` — `nfsvers=3` confirmed

---

## Bitrate/Segment Tradeoffs

### Why shorter segments trade throughput for faster failover

HLS streaming works by dividing a media stream into a series of short, independently decodable
segments. The segment length is a fundamental trade-off: longer segments reduce per-segment HTTP
overhead and allow FFmpeg to apply more aggressive compression across each chunk, improving overall
bitrate efficiency. Shorter segments, however, mean that when a pod fails mid-transcode, a takeover
pod only needs to rewind to the previous segment boundary — not the start of a much longer one.
With the default 6-second segment length, a client could stall for up to 6 seconds before the
takeover pod produces a new segment for it to consume. With the HA recovery default of 2 seconds
(`RecoverySegmentLengthSeconds = 2`), that stall window is reduced to at most 2 seconds of rewind,
dramatically improving the perceived continuity of playback during a pod failover.

### The rolling segment buffer and disk usage

In HA mode, `RecoverySegmentBufferCount` (default `5`) controls how many segments are retained in
the HLS playlist at any one time. This creates a rolling on-disk buffer of `5 × 2 s = 10 seconds`
of media that a takeover pod can serve immediately while it restarts FFmpeg from the last known
position. Keeping fewer segments wastes less NFS storage but shrinks the window in which a newly
promoted pod can respond to in-flight client requests without waiting for new segments to be
produced. Keeping more segments lengthens the recovery window but increases NFS write pressure and
disk usage proportionally. The valid range (2–10) was chosen so that the minimum buffer is always
at least 4 seconds (2 × 2 s) and the maximum stays under 20 seconds (10 × 2 s), balancing storage
cost against recovery robustness.

### Tuning guidance and rollback

The two knobs, `RecoverySegmentLengthSeconds` and `RecoverySegmentBufferCount`, can be adjusted in
the Jellyfin server's encoding options without restarting the service; the new values take effect on
the next transcode session that enters HA mode. To reduce disk I/O at the cost of a slightly longer
stall window, increase `RecoverySegmentLengthSeconds` toward its maximum of 6 (matching the
throughput-optimized default). To shrink the NFS footprint at the cost of a narrower recovery
window, lower `RecoverySegmentBufferCount` toward its minimum of 2. To roll back to the
pre-HA-mode behavior entirely, set `RecoverySegmentLengthSeconds = 6` and ensure that no active
session is registered in the `ITranscodeSessionStore` (which disables HA mode detection in
`DynamicHlsController`). All changes are backwards-compatible: in single-pod deployments where the
store is a no-op, these settings have no effect on the FFmpeg command generated.
