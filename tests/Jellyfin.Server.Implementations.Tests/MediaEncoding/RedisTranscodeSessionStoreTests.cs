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
    /// Thread-safe, in-memory implementation of <see cref="ITranscodeSessionStore"/> used within
    /// this test class to avoid a cross-project reference to Jellyfin.MediaEncoding.Tests.
    /// </summary>
    private sealed class InMemoryTranscodeSessionStore : ITranscodeSessionStore
    {
        private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, TranscodeSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
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
