using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

/// <summary>
/// Tests for lease-aware cleanup behavior expected of <c>DeleteTranscodeFileTask</c> once
/// it is made HA-aware in Phase 5.2.
/// <para>
/// The current <c>DeleteTranscodeFileTask</c> implementation uses file-age only and does not
/// check <see cref="ITranscodeSessionStore"/>, which creates a data-loss risk on shared NFS
/// storage. These tests document the correct contract by exercising the store directly.
/// </para>
/// </summary>
public class DeleteTranscodeFileTaskTests
{
    private static TranscodeSession CreateSession(string id, string pod, DateTime leaseExpiry)
        => new TranscodeSession
        {
            PlaySessionId = id,
            OwnerPod = pod,
            LeaseExpiresUtc = leaseExpiry,
            ManifestPath = $"/transcode/{id}/manifest.m3u8",
            SegmentPathPrefix = $"/transcode/{id}/segment",
            MediaSourceId = $"media-source-{id}",
            LastCompletedSegmentIndex = 2,
            LastDurablePlaybackOffset = 12_000_000L,
        };

    /// <summary>
    /// A directory that belongs to a session with a live lease must NOT be deleted.
    /// The store returns non-null, signalling to the cleanup task that the session is active.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task LiveLease_StoreReturnsSession_DirectoryShouldNotBeDeleted()
    {
        var store = new CleanupTestSessionStore();
        var session = CreateSession("cleanup-session-1", "pod-a", DateTime.UtcNow.AddMinutes(5));
        await store.SetAsync(session);

        // The cleanup task should query the store before deleting.
        var liveSession = await store.TryGetAsync("cleanup-session-1");

        // Non-null result → lease is active → directory must be retained.
        Assert.NotNull(liveSession);
        Assert.Equal("pod-a", liveSession.OwnerPod);
        Assert.True(liveSession.LeaseExpiresUtc > DateTime.UtcNow);
    }

    /// <summary>
    /// A directory whose session lease has expired beyond the recovery window MAY be deleted.
    /// The store returns <c>null</c>, signalling to the cleanup task that deletion is safe.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task ExpiredBeyondRecoveryWindow_StoreReturnsNull_DirectoryMayBeDeleted()
    {
        var store = new CleanupTestSessionStore();

        // Lease expired two hours ago – beyond any reasonable recovery window.
        var session = CreateSession("cleanup-session-2", "pod-a", DateTime.UtcNow.AddHours(-2));
        await store.SetAsync(session);

        var liveSession = await store.TryGetAsync("cleanup-session-2");

        // Null result → lease is expired → cleanup task may delete the directory.
        Assert.Null(liveSession);
    }

    /// <summary>
    /// When no session record exists in the store for a given directory, the cleanup task
    /// should treat the directory as deletable (store returns <c>null</c>).
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task NoSessionRecord_StoreReturnsNull_DirectoryMayBeDeleted()
    {
        var store = new CleanupTestSessionStore();

        var liveSession = await store.TryGetAsync("unknown-session");

        Assert.Null(liveSession);
    }

    /// <summary>
    /// Minimal in-memory <see cref="ITranscodeSessionStore"/> used within this test class
    /// to avoid a cross-project reference to Jellyfin.MediaEncoding.Tests.
    /// </summary>
    private sealed class CleanupTestSessionStore : ITranscodeSessionStore
    {
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, TranscodeSession> _sessions =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Lock _lock = new();

        public Task<TranscodeSession?> TryGetAsync(string playSessionId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(playSessionId, out var s) && s.LeaseExpiresUtc > DateTime.UtcNow)
                {
                    return Task.FromResult<TranscodeSession?>(Clone(s));
                }

                return Task.FromResult<TranscodeSession?>(null);
            }
        }

        public Task<bool> TryTakeoverAsync(string playSessionId, string claimingPod, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(playSessionId, out var s))
                {
                    return Task.FromResult(false);
                }

                if (s.LeaseExpiresUtc > DateTime.UtcNow)
                {
                    return Task.FromResult(false);
                }

                s.OwnerPod = claimingPod;
                s.LeaseExpiresUtc = DateTime.UtcNow.Add(LeaseDuration);
                return Task.FromResult(true);
            }
        }

        public Task SetAsync(TranscodeSession session, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _sessions[session.PlaySessionId] = session;
            }

            return Task.CompletedTask;
        }

        public Task RenewLeaseAsync(string playSessionId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(playSessionId, out var s))
                {
                    s.LeaseExpiresUtc = DateTime.UtcNow.Add(LeaseDuration);
                }
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string playSessionId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _sessions.Remove(playSessionId);
            }

            return Task.CompletedTask;
        }

        private static TranscodeSession Clone(TranscodeSession source)
            => new TranscodeSession
            {
                PlaySessionId = source.PlaySessionId,
                OwnerPod = source.OwnerPod,
                LeaseExpiresUtc = source.LeaseExpiresUtc,
                ManifestPath = source.ManifestPath,
                SegmentPathPrefix = source.SegmentPathPrefix,
                MediaSourceId = source.MediaSourceId,
                LastCompletedSegmentIndex = source.LastCompletedSegmentIndex,
                LastDurablePlaybackOffset = source.LastDurablePlaybackOffset,
            };
    }
}
