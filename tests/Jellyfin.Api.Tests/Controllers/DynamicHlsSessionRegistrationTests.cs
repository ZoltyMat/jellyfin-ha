using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers
{
    /// <summary>
    /// Tests for HLS session registration, lease renewal, and cleanup behaviour wired into
    /// <see cref="Jellyfin.Api.Controllers.DynamicHlsController"/> in Phase 5.2.2a.
    /// These tests verify the <see cref="ITranscodeSessionStore"/> contract used by the controller.
    /// </summary>
    public class DynamicHlsSessionRegistrationTests
    {
        private static TranscodeSession CreateSession(string id, string pod)
            => new TranscodeSession
            {
                PlaySessionId = id,
                OwnerPod = pod,
                LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(30),
                ManifestPath = string.Empty,
                SegmentPathPrefix = string.Empty,
                MediaSourceId = "media-source-1",
                LastCompletedSegmentIndex = 0,
                LastDurablePlaybackOffset = 0L,
            };

        /// <summary>
        /// After registering a session via <see cref="ITranscodeSessionStore.SetAsync"/>,
        /// <see cref="ITranscodeSessionStore.TryGetAsync"/> must return a non-null result with
        /// matching <see cref="TranscodeSession.PlaySessionId"/> and <see cref="TranscodeSession.OwnerPod"/>.
        /// </summary>
        [Fact]
        [Trait("Category", "UnitTest")]
        public async Task SessionRegistration_AfterStreamStart_StoreContainsSession()
        {
            var store = new InMemoryTranscodeSessionStore();
            var session = CreateSession("session-reg-1", "pod-a");

            await store.SetAsync(session);

            var retrieved = await store.TryGetAsync("session-reg-1");

            Assert.NotNull(retrieved);
            Assert.Equal("session-reg-1", retrieved.PlaySessionId);
            Assert.Equal("pod-a", retrieved.OwnerPod);
        }

        /// <summary>
        /// After calling <see cref="ITranscodeSessionStore.DeleteAsync"/>,
        /// <see cref="ITranscodeSessionStore.TryGetAsync"/> must return <c>null</c>.
        /// </summary>
        [Fact]
        [Trait("Category", "UnitTest")]
        public async Task SessionCleanup_AfterStreamEnd_StoreReturnsNull()
        {
            var store = new InMemoryTranscodeSessionStore();
            var session = CreateSession("session-cleanup-1", "pod-b");

            await store.SetAsync(session);
            await store.DeleteAsync("session-cleanup-1");

            var retrieved = await store.TryGetAsync("session-cleanup-1");

            Assert.Null(retrieved);
        }

        /// <summary>
        /// After a session's initial lease window would have expired, calling
        /// <see cref="ITranscodeSessionStore.RenewLeaseAsync"/> must extend the lease so that
        /// <see cref="ITranscodeSessionStore.TryGetAsync"/> still returns the session as active.
        /// </summary>
        [Fact]
        [Trait("Category", "UnitTest")]
        public async Task LeaseRenewal_ExtendsBeyondInitialExpiry()
        {
            var store = new InMemoryTranscodeSessionStore();

            // Create the session with a lease that has already expired.
            var session = new TranscodeSession
            {
                PlaySessionId = "session-renewal-1",
                OwnerPod = "pod-c",
                LeaseExpiresUtc = DateTime.UtcNow.AddMilliseconds(-1),
                ManifestPath = string.Empty,
                SegmentPathPrefix = string.Empty,
                MediaSourceId = "media-source-1",
                LastCompletedSegmentIndex = 0,
                LastDurablePlaybackOffset = 0L,
            };
            await store.SetAsync(session);

            // Verify the session is not accessible because the lease has expired.
            Assert.Null(await store.TryGetAsync("session-renewal-1"));

            // Renew the lease.
            await store.RenewLeaseAsync("session-renewal-1");

            // After renewal the session must be accessible again.
            var renewed = await store.TryGetAsync("session-renewal-1");
            Assert.NotNull(renewed);
            Assert.Equal("session-renewal-1", renewed.PlaySessionId);
            Assert.True(renewed.LeaseExpiresUtc > DateTime.UtcNow);
        }

        /// <summary>
        /// Minimal thread-safe in-memory implementation of <see cref="ITranscodeSessionStore"/>
        /// used within this test class to avoid a cross-project reference.
        /// </summary>
        private sealed class InMemoryTranscodeSessionStore : ITranscodeSessionStore
        {
            private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

            private readonly Dictionary<string, TranscodeSession> _sessions =
                new(StringComparer.OrdinalIgnoreCase);

            private readonly Lock _lock = new();

            public Task<TranscodeSession?> TryGetAsync(string playSessionId, CancellationToken cancellationToken = default)
            {
                lock (_lock)
                {
                    if (_sessions.TryGetValue(playSessionId, out var session) && session.LeaseExpiresUtc > DateTime.UtcNow)
                    {
                        return Task.FromResult<TranscodeSession?>(Clone(session));
                    }

                    return Task.FromResult<TranscodeSession?>(null);
                }
            }

            public Task<bool> TryTakeoverAsync(string playSessionId, string claimingPod, CancellationToken cancellationToken = default)
            {
                lock (_lock)
                {
                    if (!_sessions.TryGetValue(playSessionId, out var session))
                    {
                        return Task.FromResult(false);
                    }

                    if (session.LeaseExpiresUtc > DateTime.UtcNow)
                    {
                        return Task.FromResult(false);
                    }

                    session.OwnerPod = claimingPod;
                    session.LeaseExpiresUtc = DateTime.UtcNow.Add(DefaultLeaseDuration);
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
                    if (_sessions.TryGetValue(playSessionId, out var session))
                    {
                        session.LeaseExpiresUtc = DateTime.UtcNow.Add(DefaultLeaseDuration);
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

            public Task SetLiveStreamAsync(LiveStreamSession session, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public Task<LiveStreamSession?> TryGetLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default)
                => Task.FromResult<LiveStreamSession?>(null);

            public Task DeleteLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

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
