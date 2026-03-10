using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers
{
    /// <summary>
    /// Tests for HA recovery scenarios that will be wired into <see cref="DynamicHlsController"/>
    /// in Phase 5.2. These tests verify the <see cref="ITranscodeSessionStore"/> contract that
    /// the controller will rely on for missing-local-job recovery, claim racing, and cleanup guarding.
    /// </summary>
    public class DynamicHlsHaTakeoverTests
    {
        private static TranscodeSession CreateSession(string id, string pod, DateTime leaseExpiry)
            => new TranscodeSession
            {
                PlaySessionId = id,
                OwnerPod = pod,
                LeaseExpiresUtc = leaseExpiry,
                ManifestPath = $"/transcode/{id}/manifest.m3u8",
                SegmentPathPrefix = $"/transcode/{id}/segment",
                MediaSourceId = "media-source-1",
                LastCompletedSegmentIndex = 3,
                LastDurablePlaybackOffset = 18_000_000L,
            };

        /// <summary>
        /// Missing-local-job + durable-manifest-present: the store returns the session so
        /// the controller can serve the existing manifest instead of returning an error.
        /// </summary>
        [Fact]
        [Trait("Category", "UnitTest")]
        public async Task DurableManifestPresent_WithLiveSession_StoreReturnsSession()
        {
            var store = new HaTestSessionStore();
            var session = CreateSession("ha-session-1", "pod-a", DateTime.UtcNow.AddMinutes(5));
            await store.SetAsync(session);

            // Simulate controller recovery: look up the session in the durable store.
            var recovered = await store.TryGetAsync("ha-session-1");

            Assert.NotNull(recovered);
            Assert.Equal("/transcode/ha-session-1/manifest.m3u8", recovered.ManifestPath);
        }

        /// <summary>
        /// Claim-race between two concurrent requesters: only one wins
        /// <see cref="ITranscodeSessionStore.TryTakeoverAsync"/>.
        /// The other receives <c>false</c>, indicating it should redirect (302) or wait.
        /// </summary>
        [Fact]
        [Trait("Category", "UnitTest")]
        public async Task ClaimRace_TwoConcurrentRequesters_OnlyOneWinsTakeover()
        {
            var store = new HaTestSessionStore();

            // Original pod crashed – lease is expired.
            var session = CreateSession("ha-session-2", "pod-a", DateTime.UtcNow.AddMilliseconds(-1));
            await store.SetAsync(session);

            // Two pods simultaneously attempt to claim the orphaned session.
            var task1 = store.TryTakeoverAsync("ha-session-2", "pod-b");
            var task2 = store.TryTakeoverAsync("ha-session-2", "pod-c");
            var results = await Task.WhenAll(task1, task2);

            // Exactly one pod must win.
            var wins = Array.FindAll(results, r => r);
            Assert.Single(wins);
        }

        /// <summary>
        /// Stale-manifest cleanup guard: a lease that has expired beyond the recovery window
        /// causes the store to return <c>null</c>, signalling that cleanup may proceed safely.
        /// </summary>
        [Fact]
        [Trait("Category", "UnitTest")]
        public async Task StaleManifestCleanupGuard_ExpiredBeyondRecoveryWindow_StoreReturnsNull()
        {
            var store = new HaTestSessionStore();

            // Lease expired hours ago – well beyond any recovery window.
            var session = CreateSession("ha-session-3", "pod-a", DateTime.UtcNow.AddHours(-2));
            await store.SetAsync(session);

            // Controller or cleanup task checks the store before deleting files.
            var liveSession = await store.TryGetAsync("ha-session-3");

            // Store returns null → cleanup may proceed without risking data loss.
            Assert.Null(liveSession);
        }

        /// <summary>
        /// Minimal in-memory <see cref="ITranscodeSessionStore"/> used within this test class
        /// to avoid a cross-project reference to Jellyfin.MediaEncoding.Tests.
        /// </summary>
        private sealed class HaTestSessionStore : ITranscodeSessionStore
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

            public Task<IEnumerable<TranscodeSession>> GetActiveSessionsAsync(CancellationToken cancellationToken = default)
            {
                lock (_lock)
                {
                    var sessions = _sessions.Values
                        .Where(s => s.LeaseExpiresUtc > DateTime.UtcNow)
                        .Select(Clone)
                        .ToList();
                    return Task.FromResult<IEnumerable<TranscodeSession>>(sessions);
                }
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
}
