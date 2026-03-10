using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.MediaEncoding;

/// <summary>
/// Tests for transcode session store contract behavior, using <see cref="InMemoryTranscodeSessionStore"/>
/// as a reference implementation (no real Redis required).
/// </summary>
public class RedisTranscodeSessionStoreTests
{
    /// <summary>
    /// Verifies that <see cref="ITranscodeSessionStore.TryGetAsync"/> returns <c>null</c> after
    /// a session's lease has expired.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task TryGetAsync_AfterLeaseExpires_ReturnsNull()
    {
        var store = new InMemoryTranscodeSessionStore();
        var session = new TranscodeSession
        {
            PlaySessionId = "session-1",
            OwnerPod = "pod-a",
            LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(-1),
        };

        await store.SetAsync(session);

        var result = await store.TryGetAsync("session-1");

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that <see cref="ITranscodeSessionStore.TryTakeoverAsync"/> returns <c>false</c>
    /// when the session's lease is still valid.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task TryTakeoverAsync_WhileLeaseValid_ReturnsFalse()
    {
        var store = new InMemoryTranscodeSessionStore();
        var session = new TranscodeSession
        {
            PlaySessionId = "session-2",
            OwnerPod = "pod-a",
            LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(30),
        };

        await store.SetAsync(session);

        var result = await store.TryTakeoverAsync("session-2", "pod-b");

        Assert.False(result);
    }

    /// <summary>
    /// Verifies that <see cref="ITranscodeSessionStore.TryTakeoverAsync"/> returns <c>true</c>
    /// and updates the owner when the session's lease has expired.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task TryTakeoverAsync_AfterLeaseExpires_ReturnsTrue_AndUpdatesOwner()
    {
        var store = new InMemoryTranscodeSessionStore();
        var session = new TranscodeSession
        {
            PlaySessionId = "session-3",
            OwnerPod = "pod-a",
            LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(-1),
        };

        await store.SetAsync(session);

        var result = await store.TryTakeoverAsync("session-3", "pod-b");

        Assert.True(result);

        var updated = await store.TryGetAsync("session-3");
        Assert.NotNull(updated);
        Assert.Equal("pod-b", updated.OwnerPod);
        Assert.True(updated.LeaseExpiresUtc > DateTime.UtcNow);
    }

    /// <summary>
    /// Verifies that when multiple pods concurrently attempt to take over an expired session,
    /// exactly one succeeds.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task ConcurrentTryTakeover_OnlyOneWins()
    {
        var store = new InMemoryTranscodeSessionStore();
        var session = new TranscodeSession
        {
            PlaySessionId = "session-4",
            OwnerPod = "pod-a",
            LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(-1),
        };

        await store.SetAsync(session);

        const int concurrency = 10;
        var tasks = new Task<bool>[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            var podName = $"pod-{i}";
            tasks[i] = store.TryTakeoverAsync("session-4", podName);
        }

        var results = await Task.WhenAll(tasks);

        var successCount = 0;
        foreach (var r in results)
        {
            if (r)
            {
                successCount++;
            }
        }

        Assert.Equal(1, successCount);
    }

    /// <summary>
    /// Verifies that <see cref="ITranscodeSessionStore.SetLiveStreamAsync"/> stores a live stream
    /// record that can be retrieved by session id.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task SetLiveStreamAsync_CanBeRetrievedBySessionId()
    {
        var store = new InMemoryTranscodeSessionStore();
        var liveStream = new LiveStreamSession
        {
            LiveStreamId = "stream-1",
            SessionId = "session-a",
            PlaySessionId = "play-session-a",
            OwnerPod = "pod-a",
            OpenedAtUtc = DateTime.UtcNow,
        };

        await store.SetLiveStreamAsync(liveStream);

        var result = await store.TryGetLiveStreamAsync("stream-1", "session-a");
        Assert.NotNull(result);
        Assert.Equal("session-a", result.SessionId);
        Assert.Equal("pod-a", result.OwnerPod);
    }

    /// <summary>
    /// Verifies that <see cref="ITranscodeSessionStore.SetLiveStreamAsync"/> stores a live stream
    /// record that can be retrieved by play session id.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task SetLiveStreamAsync_CanBeRetrievedByPlaySessionId()
    {
        var store = new InMemoryTranscodeSessionStore();
        var liveStream = new LiveStreamSession
        {
            LiveStreamId = "stream-2",
            SessionId = "session-b",
            PlaySessionId = "play-session-b",
            OwnerPod = "pod-a",
            OpenedAtUtc = DateTime.UtcNow,
        };

        await store.SetLiveStreamAsync(liveStream);

        var result = await store.TryGetLiveStreamAsync("stream-2", "play-session-b");
        Assert.NotNull(result);
        Assert.Equal("session-b", result.SessionId);
    }

    /// <summary>
    /// Verifies that <see cref="ITranscodeSessionStore.DeleteLiveStreamAsync"/> removes the live
    /// stream record so that subsequent lookups by either session id or play session id return null.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task DeleteLiveStreamAsync_RemovesBothKeys()
    {
        var store = new InMemoryTranscodeSessionStore();
        var liveStream = new LiveStreamSession
        {
            LiveStreamId = "stream-3",
            SessionId = "session-c",
            PlaySessionId = "play-session-c",
            OwnerPod = "pod-a",
            OpenedAtUtc = DateTime.UtcNow,
        };

        await store.SetLiveStreamAsync(liveStream);
        await store.DeleteLiveStreamAsync("stream-3", "session-c");

        var bySessionId = await store.TryGetLiveStreamAsync("stream-3", "session-c");
        var byPlaySessionId = await store.TryGetLiveStreamAsync("stream-3", "play-session-c");

        Assert.Null(bySessionId);
        Assert.Null(byPlaySessionId);
    }

    /// <summary>
    /// Verifies that <see cref="ITranscodeSessionStore.TryGetLiveStreamAsync"/> returns null when
    /// no matching record exists.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task TryGetLiveStreamAsync_WhenNotPresent_ReturnsNull()
    {
        var store = new InMemoryTranscodeSessionStore();

        var result = await store.TryGetLiveStreamAsync("nonexistent-stream", "nonexistent-session");

        Assert.Null(result);
    }

    /// <summary>
    /// Thread-safe, in-memory implementation of <see cref="ITranscodeSessionStore"/> used within
    /// this test class to avoid a cross-project reference to Jellyfin.MediaEncoding.Tests.
    /// </summary>
    private sealed class InMemoryTranscodeSessionStore : ITranscodeSessionStore
    {
        private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, TranscodeSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LiveStreamSession> _liveStreams = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _lock = new();

        /// <inheritdoc />
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

        /// <inheritdoc />
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

        /// <inheritdoc />
        public Task SetAsync(TranscodeSession session, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _sessions[session.PlaySessionId] = session;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public Task DeleteAsync(string playSessionId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _sessions.Remove(playSessionId);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public Task SetLiveStreamAsync(LiveStreamSession session, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _liveStreams[MakeLiveStreamKey(session.LiveStreamId, session.SessionId)] = session;
                if (!string.IsNullOrEmpty(session.PlaySessionId))
                {
                    _liveStreams[MakeLiveStreamKey(session.LiveStreamId, session.PlaySessionId)] = session;
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<LiveStreamSession?> TryGetLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _liveStreams.TryGetValue(MakeLiveStreamKey(liveStreamId, sessionIdOrPlaySessionId), out var session);
                return Task.FromResult(session);
            }
        }

        /// <inheritdoc />
        public Task DeleteLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_liveStreams.TryGetValue(MakeLiveStreamKey(liveStreamId, sessionIdOrPlaySessionId), out var session))
                {
                    _liveStreams.Remove(MakeLiveStreamKey(liveStreamId, session.SessionId));
                    if (!string.IsNullOrEmpty(session.PlaySessionId))
                    {
                        _liveStreams.Remove(MakeLiveStreamKey(liveStreamId, session.PlaySessionId));
                    }
                }
                else
                {
                    _liveStreams.Remove(MakeLiveStreamKey(liveStreamId, sessionIdOrPlaySessionId));
                }
            }

            return Task.CompletedTask;
        }

        private static string MakeLiveStreamKey(string liveStreamId, string sessionIdOrPlaySessionId)
            => liveStreamId + "\x00" + sessionIdOrPlaySessionId;
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
