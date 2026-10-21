// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Liveness check shared by the Kad-DHT message senders. The Kad-DHT PING message is deprecated, so a peer is
/// probed with the dedicated ping protocol (/ipfs/ping/1.0.0), which is a real round trip on the session.
/// </summary>
internal static class LivenessProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fails fast when the local protocol stack cannot ping, so a custom stack without
    /// <see cref="PingProtocol"/> gets a clear error instead of a check that never runs.
    /// </summary>
    public static void EnsureSupported(ILocalPeer localPeer)
    {
        if (localPeer.GetProtocol<PingProtocol>() is null)
            throw new Libp2pSetupException(
                $"Kad-DHT liveness checks need {nameof(PingProtocol)} in the peer's protocol stack, but it is not registered.");
    }

    /// <summary>
    /// Pings the remote end of <paramref name="session"/>. Any failure other than the caller cancelling
    /// (timeout, closed connection, bad pong) disconnects the session so that the next dial is a fresh one.
    /// </summary>
    public static async Task ProbeAsync(ISession session, TimeSpan timeout, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await session.DialAsync<PingProtocol>(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await DisconnectQuietly(session).ConfigureAwait(false);
            if (ex is OperationCanceledException)
                throw new Libp2pException("Ping did not complete: the connection was closed or the probe timed out.", ex);
            throw;
        }
    }

    private static async Task DisconnectQuietly(ISession session)
    {
        try { await session.DisconnectAsync().ConfigureAwait(false); }
        catch { /* the session is already considered dead */ }
    }
}
