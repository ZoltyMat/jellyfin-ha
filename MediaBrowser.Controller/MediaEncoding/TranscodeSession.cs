using System;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Represents a durable record of an HLS transcoding session for HA pod recovery.
/// </summary>
public sealed class TranscodeSession
{
    /// <summary>
    /// Gets or sets the unique play session identifier.
    /// </summary>
    public string PlaySessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the pod that currently owns this session's lease.
    /// </summary>
    public string OwnerPod { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC time at which the owning pod's lease expires.
    /// </summary>
    public DateTime LeaseExpiresUtc { get; set; }

    /// <summary>
    /// Gets or sets the absolute path to the HLS manifest (.m3u8) file on shared storage.
    /// </summary>
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path prefix for transcoded segment files on shared storage.
    /// </summary>
    public string SegmentPathPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the media source identifier associated with this session.
    /// </summary>
    public string MediaSourceId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the zero-based index of the last segment that was fully written to durable storage.
    /// </summary>
    public int LastCompletedSegmentIndex { get; set; }

    /// <summary>
    /// Gets or sets the last durable playback offset in ticks, used to resume playback after failover.
    /// </summary>
    public long LastDurablePlaybackOffset { get; set; }
}
