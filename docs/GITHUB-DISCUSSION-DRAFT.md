# Jellyfin HA transcoding fork: Redis-backed session failover + experimental PostgreSQL provider

I've been working on a fork of Jellyfin focused on one specific problem: making HLS transcoding survive pod restarts in a multi-replica Kubernetes deployment.

## What it does

Right now, Jellyfin assumes transcode state lives in one server process. If that pod dies, active transcodes die with it. This fork adds a small HA layer so transcode ownership can survive a pod restart:

- A new `ITranscodeSessionStore` abstraction for durable transcode session tracking
- A `RedisTranscodeSessionStore` implementation with lease-based ownership
- Atomic pod takeover using a Redis Lua script when a lease expires
- Lease-aware cleanup so one pod does not delete segments another pod still needs
- A `NullTranscodeSessionStore` fallback, so single-instance deployments behave exactly like upstream with no config changes

I also added an experimental PostgreSQL provider for shared-database deployments, since SQLite is not a good fit once multiple replicas are involved.

## What the HA flow looks like

- Pod A starts an HLS transcode and registers the session in Redis
- Pod A renews the lease while it owns the session
- If Pod A dies, the lease expires
- Pod B receives the next request, atomically claims the expired lease, and resumes from the last completed segment on shared storage
- The client sees a short buffer pause instead of a hard failure

## How to run it

There are three practical modes:

### 1. Single instance

No config needed. It falls back to the no-op store automatically.

### 2. Local HA test

Run two Jellyfin instances against:

- the same Redis
- the same shared transcode directory

That is enough to test failover behavior locally.

### 3. Kubernetes / k3s

This is the intended deployment model. You need:

- 2+ Jellyfin replicas
- Redis
- shared RWX storage for transcode output
- shared media storage
- ideally PostgreSQL if you want a proper shared DB setup

The key config is:

```text
Jellyfin:TranscodeStore:RedisConnectionString
Jellyfin:TranscodeStore:LeaseDurationSeconds
```

Repo and write-up:

- Source: https://github.com/ZoltyMat/jellyfin-ha
- Full change summary vs upstream: https://github.com/ZoltyMat/jellyfin-ha/blob/main/docs/FORK-DIFF.md
- Write-up with diagrams and k8s manifests: https://blog.zolty.systems/posts/jellyfin-ha-kubernetes

## What would be required to merge upstream

I do not expect this to be merged as-is without discussion. If there is interest, I think the realistic path is to split it into small pieces:

1. Introduce `ITranscodeSessionStore`, `TranscodeSession`, and `NullTranscodeSessionStore` only
2. Add the DI wiring with no behavior change unless configured
3. Add HLS session registration and lease renewal hooks
4. Add lease-aware cleanup in `DeleteTranscodeFileTask`
5. Add takeover logic in the HLS/session path
6. Discuss whether Redis should be the first supported distributed store, or whether the interface should land before any concrete implementation
7. Treat PostgreSQL as a separate discussion entirely

I think the HA transcode work has a better chance of review if it is separated from the PostgreSQL provider and migration tooling.

## Why I'm posting it

I'm not trying to maintain a permanent hard fork. I built this to see whether Jellyfin could be made to behave well in a replicated environment without rewriting major subsystems. The answer seems to be yes, but it needs maintainers to decide whether this kind of deployment is something upstream wants to support.

If there's interest, I'm happy to break the work into smaller PRs, clean up anything that does not match project direction, and rework the design around maintainer feedback.