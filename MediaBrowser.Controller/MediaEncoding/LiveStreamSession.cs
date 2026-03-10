using System;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Represents a durable record of an open live stream session, enabling HA pod recovery
/// when the owning pod crashes or is evicted.
/// </summary>
public sealed class LiveStreamSession
{
    /// <summary>
    /// Gets or sets the live stream identifier (e.g. a TV tuner channel token).
    /// </summary>
    public string LiveStreamId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the session identifier of the client that opened this live stream.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the play session identifier associated with this live stream,
    /// or an empty string when the client did not supply one.
    /// </summary>
    public string PlaySessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the pod that currently holds this live stream open.
    /// </summary>
    public string OwnerPod { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC time at which this record was created.
    /// </summary>
    public DateTime OpenedAtUtc { get; set; }
}
