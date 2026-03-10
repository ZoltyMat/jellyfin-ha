using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Emby.Server.Implementations.MediaEncoding;

/// <summary>
/// A Redis-backed implementation of <see cref="ITranscodeSessionStore"/> that provides
/// durable, distributed session tracking with lease-based ownership between pods.
/// </summary>
public sealed class RedisTranscodeSessionStore : ITranscodeSessionStore
{
    private const string KeyPrefix = "jellyfin:transcode:";
    private const string LiveStreamKeyPrefix = "jellyfin:livestream:";

    /// <summary>
    /// Lua script for atomic takeover: reads the stored session, checks whether the lease has
    /// expired (comparing <c>LeaseExpiresUtc.Ticks</c> against the caller-supplied current ticks),
    /// and if expired, updates the owner and expiry before returning 1; returns 0 otherwise.
    /// </summary>
    private const string TakeoverScript = @"
local raw = redis.call('GET', KEYS[1])
if not raw then return 0 end
local session = cjson.decode(raw)
local currentTicks = tonumber(ARGV[1])
if session['LeaseExpiresUtc'] > currentTicks then return 0 end
session['OwnerPod'] = ARGV[2]
local leaseDurationMs = tonumber(ARGV[3])
local newTicks = currentTicks + (leaseDurationMs * 10000)
session['LeaseExpiresUtc'] = newTicks
redis.call('SET', KEYS[1], cjson.encode(session), 'PX', leaseDurationMs)
return 1";

    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly TranscodeStoreOptions _options;
    private readonly ILogger<RedisTranscodeSessionStore> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisTranscodeSessionStore"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="options">The transcode store configuration options.</param>
    /// <param name="logger">The logger.</param>
    public RedisTranscodeSessionStore(
        IConnectionMultiplexer redis,
        IOptions<TranscodeStoreOptions> options,
        ILogger<RedisTranscodeSessionStore> logger)
    {
        _redis = redis;
        _db = redis.GetDatabase();
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SetAsync(TranscodeSession session, CancellationToken cancellationToken = default)
    {
        var key = GetKey(session.PlaySessionId);
        var json = JsonSerializer.Serialize(session);
        var leaseDurationMs = (long)_options.LeaseDurationSeconds * 1000;
        await _db.StringSetAsync(key, json, TimeSpan.FromMilliseconds(leaseDurationMs)).ConfigureAwait(false);
        _logger.LogDebug("Set transcode session {PlaySessionId} in Redis.", session.PlaySessionId);
    }

    /// <inheritdoc />
    public async Task<TranscodeSession?> TryGetAsync(string playSessionId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(playSessionId);
        var raw = await _db.StringGetAsync(key).ConfigureAwait(false);
        if (!raw.HasValue)
        {
            return null;
        }

        var session = JsonSerializer.Deserialize<TranscodeSession>(raw.ToString());

        // Check LeaseExpiresUtc in addition to Redis TTL to guard against the window between
        // Redis TTL evaluation and the GET result being returned to the caller.
        if (session is null || session.LeaseExpiresUtc <= DateTime.UtcNow)
        {
            return null;
        }

        return session;
    }

    /// <inheritdoc />
    public async Task RenewLeaseAsync(string playSessionId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(playSessionId);
        var raw = await _db.StringGetAsync(key).ConfigureAwait(false);
        if (!raw.HasValue)
        {
            return;
        }

        var session = JsonSerializer.Deserialize<TranscodeSession>(raw.ToString());
        if (session is null)
        {
            return;
        }

        var leaseDurationMs = (long)_options.LeaseDurationSeconds * 1000;
        session.LeaseExpiresUtc = DateTime.UtcNow.AddMilliseconds(leaseDurationMs);
        var json = JsonSerializer.Serialize(session);
        await _db.StringSetAsync(key, json, TimeSpan.FromMilliseconds(leaseDurationMs)).ConfigureAwait(false);
        _logger.LogDebug("Renewed lease for transcode session {PlaySessionId}.", playSessionId);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string playSessionId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(playSessionId);
        await _db.KeyDeleteAsync(key).ConfigureAwait(false);
        _logger.LogDebug("Deleted transcode session {PlaySessionId} from Redis.", playSessionId);
    }

    /// <inheritdoc />
    public async Task<bool> TryTakeoverAsync(string playSessionId, string claimingPod, CancellationToken cancellationToken = default)
    {
        var key = GetKey(playSessionId);
        var leaseDurationMs = (long)_options.LeaseDurationSeconds * 1000;
        var currentTicks = DateTime.UtcNow.Ticks;

        var result = (long?)await _db.ScriptEvaluateAsync(
            TakeoverScript,
            keys: new RedisKey[] { key },
            values: new RedisValue[] { currentTicks, claimingPod, leaseDurationMs }).ConfigureAwait(false);

        var succeeded = result == 1;
        if (succeeded)
        {
            _logger.LogInformation("Pod {ClaimingPod} successfully took over transcode session {PlaySessionId}.", claimingPod, playSessionId);
        }

        return succeeded;
    }

    private static string GetKey(string playSessionId) => KeyPrefix + playSessionId;

    private static string GetLiveStreamKey(string liveStreamId, string sessionIdOrPlaySessionId)
        => LiveStreamKeyPrefix + liveStreamId + ":" + sessionIdOrPlaySessionId;

    /// <inheritdoc />
    public async Task<IEnumerable<TranscodeSession>> GetActiveSessionsAsync(CancellationToken cancellationToken = default)
    {
        var sessions = new List<TranscodeSession>();
        var servers = _redis.GetServers();

        foreach (var server in servers)
        {
            if (!server.IsConnected)
            {
                continue;
            }

            var keys = new List<RedisKey>();
            await foreach (var key in server.KeysAsync(database: _db.Database, pattern: KeyPrefix + "*", pageSize: 1000).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                keys.Add(key);
            }

            var tasks = keys.Select(key => _db.StringGetAsync(key)).ToList();
            var values = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var raw in values)
            {
                if (!raw.HasValue)
                {
                    continue;
                }

                TranscodeSession? session;
                try
                {
                    session = JsonSerializer.Deserialize<TranscodeSession>(raw.ToString());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize transcode session from Redis.");
                    continue;
                }

                if (session is not null)
                {
                    sessions.Add(session);
                }
            }
        }

        return sessions;
    }

    /// <inheritdoc />
    public async Task SetLiveStreamAsync(LiveStreamSession session, CancellationToken cancellationToken = default)
    {
        var key = GetLiveStreamKey(session.LiveStreamId, session.SessionId);
        var json = JsonSerializer.Serialize(session);
        // Live stream records use the same lease duration as transcode sessions.
        var leaseDurationMs = (long)_options.LeaseDurationSeconds * 1000;
        await _db.StringSetAsync(key, json, TimeSpan.FromMilliseconds(leaseDurationMs)).ConfigureAwait(false);

        // Also index by play session id so the caller can look up by either key.
        if (!string.IsNullOrEmpty(session.PlaySessionId))
        {
            var playKey = GetLiveStreamKey(session.LiveStreamId, session.PlaySessionId);
            await _db.StringSetAsync(playKey, json, TimeSpan.FromMilliseconds(leaseDurationMs)).ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Set live stream session {LiveStreamId}/{SessionId} in Redis.",
            session.LiveStreamId,
            session.SessionId);
    }

    /// <inheritdoc />
    public async Task<LiveStreamSession?> TryGetLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default)
    {
        var key = GetLiveStreamKey(liveStreamId, sessionIdOrPlaySessionId);
        var raw = await _db.StringGetAsync(key).ConfigureAwait(false);
        if (!raw.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize<LiveStreamSession>(raw.ToString());
    }

    /// <inheritdoc />
    public async Task DeleteLiveStreamAsync(string liveStreamId, string sessionIdOrPlaySessionId, CancellationToken cancellationToken = default)
    {
        var key = GetLiveStreamKey(liveStreamId, sessionIdOrPlaySessionId);
        var raw = await _db.StringGetAsync(key).ConfigureAwait(false);
        if (raw.HasValue)
        {
            var session = JsonSerializer.Deserialize<LiveStreamSession>(raw.ToString());
            if (session is not null)
            {
                // Remove both the session-id key and the play-session-id key if present.
                var keysToDelete = new System.Collections.Generic.List<RedisKey>
                {
                    GetLiveStreamKey(liveStreamId, session.SessionId)
                };

                if (!string.IsNullOrEmpty(session.PlaySessionId))
                {
                    keysToDelete.Add(GetLiveStreamKey(liveStreamId, session.PlaySessionId));
                }

                await _db.KeyDeleteAsync(keysToDelete.ToArray()).ConfigureAwait(false);
                _logger.LogDebug(
                    "Deleted live stream session {LiveStreamId}/{SessionId} from Redis.",
                    liveStreamId,
                    session.SessionId);
                return;
            }
        }

        // Fallback: delete just the key that was supplied.
        await _db.KeyDeleteAsync(key).ConfigureAwait(false);
    }
}
