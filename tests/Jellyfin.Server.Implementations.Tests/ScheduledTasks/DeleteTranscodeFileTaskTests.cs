using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Moq;
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
    /// Creates a mock <see cref="IConfigurationManager"/> that returns <paramref name="transcodePath"/>
    /// as the configured transcode path, used by the <c>GetTranscodePath</c> extension method.
    /// </summary>
    private static Mock<IConfigurationManager> CreateConfigMock(string transcodePath)
    {
        var appPathsMock = new Mock<IApplicationPaths>();
        appPathsMock
            .Setup(p => p.CreateAndCheckMarker(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()));

        var configMock = new Mock<IConfigurationManager>();
        configMock
            .Setup(c => c.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = transcodePath });
        configMock
            .Setup(c => c.CommonApplicationPaths)
            .Returns(appPathsMock.Object);

        return configMock;
    }

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
    /// Files that belong to an active session (manifest or segments) must NOT be deleted
    /// even when their modification time is older than <c>minDateModified</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task ExecuteAsync_WithActiveSession_DoesNotDeleteActiveFiles()
    {
        // Arrange
        const string TranscodePath = "/transcode";
        const string SessionId = "active-session-1";
        const string ManifestPath = "/transcode/active-session-1/manifest.m3u8";
        const string SegmentPath = "/transcode/active-session-1/segment0.ts";

        var store = new CleanupTestSessionStore();
        var session = new TranscodeSession
        {
            PlaySessionId = SessionId,
            OwnerPod = "pod-a",
            LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(5),
            ManifestPath = ManifestPath,
            SegmentPathPrefix = "/transcode/active-session-1/segment",
            MediaSourceId = "media-source-1",
        };
        await store.SetAsync(session);

        var deletedFiles = new List<string>();
        var oldModifyTime = DateTime.UtcNow.AddDays(-2);

        var fileSystemMock = new Mock<IFileSystem>();
        fileSystemMock
            .Setup(fs => fs.GetFiles(TranscodePath, true))
            .Returns(new[]
            {
                new FileSystemMetadata { FullName = ManifestPath, IsDirectory = false },
                new FileSystemMetadata { FullName = SegmentPath, IsDirectory = false },
            });
        fileSystemMock
            .Setup(fs => fs.GetLastWriteTimeUtc(It.IsAny<FileSystemMetadata>()))
            .Returns(oldModifyTime);
        fileSystemMock
            .Setup(fs => fs.DeleteFile(It.IsAny<string>()))
            .Callback<string>(path => deletedFiles.Add(path));
        fileSystemMock
            .Setup(fs => fs.GetFiles(TranscodePath, false))
            .Returns(Enumerable.Empty<FileSystemMetadata>());
        fileSystemMock
            .Setup(fs => fs.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(Enumerable.Empty<FileSystemMetadata>());

        var configMock = CreateConfigMock(TranscodePath);

        var localizationMock = new Mock<MediaBrowser.Model.Globalization.ILocalizationManager>();
        localizationMock
            .Setup(l => l.GetLocalizedString(It.IsAny<string>()))
            .Returns<string>(s => s);

        var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<Emby.Server.Implementations.ScheduledTasks.Tasks.DeleteTranscodeFileTask>>();

        var task = new Emby.Server.Implementations.ScheduledTasks.Tasks.DeleteTranscodeFileTask(
            loggerMock.Object,
            fileSystemMock.Object,
            configMock.Object,
            localizationMock.Object,
            store);

        // Act
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Assert – neither the manifest nor the segment should have been deleted
        Assert.DoesNotContain(ManifestPath, deletedFiles);
        Assert.DoesNotContain(SegmentPath, deletedFiles);
    }

    /// <summary>
    /// Files whose session lease has expired are NOT returned by <see cref="ITranscodeSessionStore.GetActiveSessionsAsync"/>
    /// and therefore should be eligible for time-based deletion.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task ExecuteAsync_WithExpiredSession_DeletesFiles()
    {
        // Arrange
        const string TranscodePath = "/transcode";
        const string SessionId = "expired-session-1";
        const string ManifestPath = "/transcode/expired-session-1/manifest.m3u8";

        var store = new CleanupTestSessionStore();
        // Lease expired two hours ago
        var session = new TranscodeSession
        {
            PlaySessionId = SessionId,
            OwnerPod = "pod-a",
            LeaseExpiresUtc = DateTime.UtcNow.AddHours(-2),
            ManifestPath = ManifestPath,
            SegmentPathPrefix = "/transcode/expired-session-1/segment",
            MediaSourceId = "media-source-1",
        };
        await store.SetAsync(session);

        var deletedFiles = new List<string>();
        var oldModifyTime = DateTime.UtcNow.AddDays(-2);

        var fileSystemMock = new Mock<IFileSystem>();
        fileSystemMock
            .Setup(fs => fs.GetFiles(TranscodePath, true))
            .Returns(new[]
            {
                new FileSystemMetadata { FullName = ManifestPath, IsDirectory = false },
            });
        fileSystemMock
            .Setup(fs => fs.GetLastWriteTimeUtc(It.IsAny<FileSystemMetadata>()))
            .Returns(oldModifyTime);
        fileSystemMock
            .Setup(fs => fs.DeleteFile(It.IsAny<string>()))
            .Callback<string>(path => deletedFiles.Add(path));
        fileSystemMock
            .Setup(fs => fs.GetDirectories(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(Enumerable.Empty<FileSystemMetadata>());

        var configMock = CreateConfigMock(TranscodePath);

        var localizationMock = new Mock<MediaBrowser.Model.Globalization.ILocalizationManager>();
        localizationMock
            .Setup(l => l.GetLocalizedString(It.IsAny<string>()))
            .Returns<string>(s => s);

        var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<Emby.Server.Implementations.ScheduledTasks.Tasks.DeleteTranscodeFileTask>>();

        var task = new Emby.Server.Implementations.ScheduledTasks.Tasks.DeleteTranscodeFileTask(
            loggerMock.Object,
            fileSystemMock.Object,
            configMock.Object,
            localizationMock.Object,
            store);

        // Act
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Assert – expired session files are eligible for time-based deletion
        Assert.Contains(ManifestPath, deletedFiles);
    }

    /// <summary>
    /// When <see cref="ITranscodeSessionStore.GetActiveSessionsAsync"/> throws an exception,
    /// the task should abort deletion safely rather than risk removing files in use.
    /// </summary>
    [Fact]
    [Trait("Category", "UnitTest")]
    public async Task ExecuteAsync_WhenStoreFails_AbortsDeletion()
    {
        // Arrange
        const string TranscodePath = "/transcode";
        const string ManifestPath = "/transcode/session-1/manifest.m3u8";

        var deletedFiles = new List<string>();
        var oldModifyTime = DateTime.UtcNow.AddDays(-2);

        var fileSystemMock = new Mock<IFileSystem>();
        fileSystemMock
            .Setup(fs => fs.GetFiles(TranscodePath, true))
            .Returns(new[]
            {
                new FileSystemMetadata { FullName = ManifestPath, IsDirectory = false },
            });
        fileSystemMock
            .Setup(fs => fs.GetLastWriteTimeUtc(It.IsAny<FileSystemMetadata>()))
            .Returns(oldModifyTime);
        fileSystemMock
            .Setup(fs => fs.DeleteFile(It.IsAny<string>()))
            .Callback<string>(path => deletedFiles.Add(path));

        var configMock = CreateConfigMock(TranscodePath);

        var localizationMock = new Mock<MediaBrowser.Model.Globalization.ILocalizationManager>();
        localizationMock
            .Setup(l => l.GetLocalizedString(It.IsAny<string>()))
            .Returns<string>(s => s);

        var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<Emby.Server.Implementations.ScheduledTasks.Tasks.DeleteTranscodeFileTask>>();

        var failingStoreMock = new Mock<ITranscodeSessionStore>();
        failingStoreMock
            .Setup(s => s.GetActiveSessionsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Redis unavailable"));

        var task = new Emby.Server.Implementations.ScheduledTasks.Tasks.DeleteTranscodeFileTask(
            loggerMock.Object,
            fileSystemMock.Object,
            configMock.Object,
            localizationMock.Object,
            failingStoreMock.Object);

        // Act
        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        // Assert – when the store fails, no files should be deleted (safe abort)
        Assert.Empty(deletedFiles);
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
