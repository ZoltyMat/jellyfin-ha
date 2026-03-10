using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Provides a durable store for HLS transcoding session state, enabling
/// HA recovery and lease-based ownership between pods.
/// </summary>
public interface ITranscodeSessionStore
{
    /// <summary>
    /// Attempts to retrieve a transcoding session by its play session identifier.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The <see cref="TranscodeSession"/> if it exists and its lease has not expired;
    /// otherwise <c>null</c>.
    /// </returns>
    Task<TranscodeSession?> TryGetAsync(string playSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to take over ownership of an existing session by claiming the lease for
    /// <paramref name="claimingPod"/>. Takeover succeeds only when the session exists and
    /// its current lease has already expired.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="claimingPod">The name of the pod attempting to claim ownership.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the takeover succeeded (the claiming pod now holds the lease);
    /// <c>false</c> if the session does not exist, its lease is still valid, or another
    /// concurrent caller already claimed it.
    /// </returns>
    Task<bool> TryTakeoverAsync(string playSessionId, string claimingPod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new or updated transcoding session.
    /// </summary>
    /// <param name="session">The session to store.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task SetAsync(TranscodeSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews the lease for an existing session, extending its
    /// <see cref="TranscodeSession.LeaseExpiresUtc"/> by the store's configured lease duration.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RenewLeaseAsync(string playSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a transcoding session from the store.
    /// </summary>
    /// <param name="playSessionId">The play session identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task DeleteAsync(string playSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all currently active transcoding sessions from the store.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// An enumerable of <see cref="TranscodeSession"/> objects representing all active sessions.
    /// Returns an empty enumerable if no sessions are active or if the store cannot be reached.
    /// </returns>
    Task<IEnumerable<TranscodeSession>> GetActiveSessionsAsync(CancellationToken cancellationToken = default);
}
