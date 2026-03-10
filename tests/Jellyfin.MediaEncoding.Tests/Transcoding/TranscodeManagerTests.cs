using System;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Tests.Fakes;
using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Transcoding;

/// <summary>
/// Unit tests for the HA session-store contract: lease expiry, double-claim prevention,
/// heartbeat renewal, and stale-session cleanup.
/// All tests exercise <see cref="InMemoryTranscodeSessionStore"/> which implements the
/// <see cref="ITranscodeSessionStore"/> interface that will be backed by Redis in Phase 5.2.
/// </summary>
public class TranscodeManagerTests
{
    private static TranscodeSession CreateSession(
        string id,
        string pod,
        DateTime leaseExpiry,
        int lastSegmentIndex = 0,
        long lastOffset = 0L)
        => new TranscodeSession
        {
            PlaySessionId = id,
            OwnerPod = pod,
            LeaseExpiresUtc = leaseExpiry,
            ManifestPath = $"/transcode/{id}/manifest.m3u8",
            SegmentPathPrefix = $"/transcode/{id}/segment",
            MediaSourceId = $"media-source-{id}",
            LastCompletedSegmentIndex = lastSegmentIndex,
            LastDurablePlaybackOffset = lastOffset,
        };

    /// <summary>
    /// Lease expiry: <see cref="ITranscodeSessionStore.TryGetAsync"/> returns <c>null</c>
    /// once <see cref="TranscodeSession.LeaseExpiresUtc"/> has passed.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task TryGetAsync_AfterLeaseExpires_ReturnsNull()
    {
        var store = new InMemoryTranscodeSessionStore();
        var session = CreateSession("session-expired", "pod-a", DateTime.UtcNow.AddMilliseconds(-1));
        await store.SetAsync(session);

        var result = await store.TryGetAsync("session-expired");

        Assert.Null(result);
    }

    /// <summary>
    /// A session whose lease has not yet expired is returned correctly.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task TryGetAsync_WithinLease_ReturnsSession()
    {
        var store = new InMemoryTranscodeSessionStore();
        var session = CreateSession("session-live", "pod-a", DateTime.UtcNow.AddMinutes(5), lastSegmentIndex: 3, lastOffset: 18_000_000L);
        await store.SetAsync(session);

        var result = await store.TryGetAsync("session-live");

        Assert.NotNull(result);
        Assert.Equal("session-live", result.PlaySessionId);
        Assert.Equal("pod-a", result.OwnerPod);
        Assert.Equal(3, result.LastCompletedSegmentIndex);
        Assert.Equal(18_000_000L, result.LastDurablePlaybackOffset);
    }

    /// <summary>
    /// Double-claim prevention: <see cref="ITranscodeSessionStore.TryTakeoverAsync"/> returns
    /// <c>false</c> while the first pod's lease is still valid.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task TryTakeoverAsync_WhileLeaseValid_ReturnsFalse()
    {
        var store = new InMemoryTranscodeSessionStore();
        var session = CreateSession("session-valid", "pod-a", DateTime.UtcNow.AddMinutes(5));
        await store.SetAsync(session);

        var firstAttempt = await store.TryTakeoverAsync("session-valid", "pod-b");
        var secondAttempt = await store.TryTakeoverAsync("session-valid", "pod-c");

        Assert.False(firstAttempt);
        Assert.False(secondAttempt);
    }

    /// <summary>
    /// After a lease expires, the first concurrent caller that invokes
    /// <see cref="ITranscodeSessionStore.TryTakeoverAsync"/> wins; the second caller returns <c>false</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task TryTakeoverAsync_AfterLeaseExpires_OnlyFirstClaimerSucceeds()
    {
        var store = new InMemoryTranscodeSessionStore();
        var session = CreateSession("session-stale", "pod-a", DateTime.UtcNow.AddMilliseconds(-1));
        await store.SetAsync(session);

        // First pod wins; its takeover renews the lease atomically.
        var firstTakeover = await store.TryTakeoverAsync("session-stale", "pod-b");

        // Second pod is too late – pod-b already holds a fresh lease.
        var secondTakeover = await store.TryTakeoverAsync("session-stale", "pod-c");

        Assert.True(firstTakeover);
        Assert.False(secondTakeover);
    }

    /// <summary>
    /// Heartbeat renewal: <see cref="ITranscodeSessionStore.RenewLeaseAsync"/> extends
    /// <see cref="TranscodeSession.LeaseExpiresUtc"/> beyond its original value.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task RenewLeaseAsync_ExtendsLeaseExpiry()
    {
        var store = new InMemoryTranscodeSessionStore();
        var originalExpiry = DateTime.UtcNow.AddSeconds(5);
        var session = CreateSession("session-renew", "pod-a", originalExpiry, lastSegmentIndex: 2, lastOffset: 10_000_000L);
        await store.SetAsync(session);

        await store.RenewLeaseAsync("session-renew");

        var renewed = await store.TryGetAsync("session-renew");
        Assert.NotNull(renewed);
        Assert.True(
            renewed.LeaseExpiresUtc > originalExpiry,
            "Renewed lease expiry should be later than the original expiry.");
    }

    /// <summary>
    /// Stale-session cleanup: an expired session can be deleted without error, and a
    /// subsequent <see cref="ITranscodeSessionStore.TryGetAsync"/> returns <c>null</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task DeleteAsync_ExpiredSession_CompletesWithoutError()
    {
        var store = new InMemoryTranscodeSessionStore();
        var session = CreateSession("session-delete", "pod-a", DateTime.UtcNow.AddMilliseconds(-1));
        await store.SetAsync(session);

        var ex = await Record.ExceptionAsync(() => store.DeleteAsync("session-delete"));
        Assert.Null(ex);

        var result = await store.TryGetAsync("session-delete");
        Assert.Null(result);
    }

    /// <summary>
    /// Deleting a session that was never stored must complete without error.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task DeleteAsync_NonExistentSession_CompletesWithoutError()
    {
        var store = new InMemoryTranscodeSessionStore();

        var ex = await Record.ExceptionAsync(() => store.DeleteAsync("nonexistent-session"));

        Assert.Null(ex);
    }
}
