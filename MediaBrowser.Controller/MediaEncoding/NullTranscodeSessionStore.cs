using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// A no-op implementation of <see cref="ITranscodeSessionStore"/> used in single-instance deployments
/// where durable session tracking across pods is not required.
/// </summary>
public sealed class NullTranscodeSessionStore : ITranscodeSessionStore
{
    /// <inheritdoc />
    public Task<TranscodeSession?> TryGetAsync(string playSessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<TranscodeSession?>(null);

    /// <inheritdoc />
    public Task<bool> TryTakeoverAsync(string playSessionId, string claimingPod, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task SetAsync(TranscodeSession session, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task RenewLeaseAsync(string playSessionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteAsync(string playSessionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
