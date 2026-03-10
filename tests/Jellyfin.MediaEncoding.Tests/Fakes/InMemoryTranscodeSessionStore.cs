using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.MediaEncoding.Tests.Fakes;

/// <summary>
/// Thread-safe, in-memory implementation of <see cref="ITranscodeSessionStore"/> for use in unit tests.
/// </summary>
public sealed class InMemoryTranscodeSessionStore : ITranscodeSessionStore
{
    /// <summary>
    /// The duration added to <see cref="DateTime.UtcNow"/> when a lease is renewed or first claimed.
    /// </summary>
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

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
                // Another pod's lease is still valid – takeover not permitted.
                return Task.FromResult(false);
            }

            // Lease has expired – claim it atomically.
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
            var sessions = _sessions.Values.Select(Clone).ToList();
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
