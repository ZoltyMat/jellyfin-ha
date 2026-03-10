using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Deletes all transcoding temp files.
/// </summary>
public class DeleteTranscodeFileTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILogger<DeleteTranscodeFileTask> _logger;
    private readonly IConfigurationManager _configurationManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalizationManager _localization;
    private readonly ITranscodeSessionStore _sessionStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteTranscodeFileTask"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{DeleteTranscodeFileTask}"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="configurationManager">Instance of the <see cref="IConfigurationManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="sessionStore">Instance of the <see cref="ITranscodeSessionStore"/> interface.</param>
    public DeleteTranscodeFileTask(
        ILogger<DeleteTranscodeFileTask> logger,
        IFileSystem fileSystem,
        IConfigurationManager configurationManager,
        ILocalizationManager localization,
        ITranscodeSessionStore sessionStore)
    {
        _logger = logger;
        _fileSystem = fileSystem;
        _configurationManager = configurationManager;
        _localization = localization;
        _sessionStore = sessionStore;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskCleanTranscode");

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskCleanTranscodeDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    /// <inheritdoc />
    public string Key => "DeleteTranscodeFiles";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger
        };

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var minDateModified = DateTime.UtcNow.AddDays(-1);
        progress.Report(50);

        IEnumerable<TranscodeSession> activeSessions;
        try
        {
            activeSessions = await _sessionStore.GetActiveSessionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve active transcode sessions. Skipping deletion to avoid removing files in use.");
            progress.Report(100);
            return;
        }

        DeleteTempFilesFromDirectory(_configurationManager.GetTranscodePath(), minDateModified, activeSessions, progress, cancellationToken);
    }

    /// <summary>
    /// Deletes the transcoded temp files from directory with a last write time less than a given date,
    /// skipping any files that belong to an active transcode session.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="minDateModified">The min date modified.</param>
    /// <param name="activeSessions">The currently active transcode sessions.</param>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The task cancellation token.</param>
    private void DeleteTempFilesFromDirectory(string directory, DateTime minDateModified, IEnumerable<TranscodeSession> activeSessions, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var activeSessionList = activeSessions.ToList();

        var filesToDelete = _fileSystem.GetFiles(directory, true)
            .Where(f => _fileSystem.GetLastWriteTimeUtc(f) < minDateModified)
            .ToList();

        var index = 0;

        foreach (var file in filesToDelete)
        {
            double percent = index;
            percent /= filesToDelete.Count;

            progress.Report(100 * percent);

            cancellationToken.ThrowIfCancellationRequested();

            if (IsFileProtectedByActiveSession(file.FullName, activeSessionList))
            {
                _logger.LogDebug("Skipping deletion of {FilePath} as it belongs to an active transcode session.", file.FullName);
                index++;
                continue;
            }

            FileSystemHelper.DeleteFile(_fileSystem, file.FullName, _logger);

            index++;
        }

        FileSystemHelper.DeleteEmptyFolders(_fileSystem, directory, _logger);

        progress.Report(100);
    }

    private static bool IsFileProtectedByActiveSession(string filePath, IList<TranscodeSession> activeSessions)
    {
        foreach (var session in activeSessions)
        {
            if (!string.IsNullOrEmpty(session.ManifestPath) &&
                string.Equals(filePath, session.ManifestPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(session.SegmentPathPrefix) &&
                filePath.StartsWith(session.SegmentPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
