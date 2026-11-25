// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using Nethermind.Libp2p.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Ping;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/ping/ping.md
/// </summary>
public class PingProtocol : ISessionProtocol, IOutboundStreamLimited
{
    private const int PayloadLength = 32;

    public string Id => "/ipfs/ping/1.0.0";
    private readonly ILogger<PingProtocol>? _logger;

    /// <summary>
    /// Pairs with an outbound ping in flight. One protocol instance is shared by every local peer
    /// built from the same factory, so the key carries the local peer as well as the remote;
    /// otherwise two local peers dialing one server would contend for a single allowance. Entries
    /// are removed when the allowance is disposed, so the table holds only what is running.
    /// </summary>
    private readonly ConcurrentDictionary<(ILocalPeer Local, PeerId Remote), byte> _dialing = new();

    public PingProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PingProtocol>();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A remote that is not yet identified is let through: the rule is one stream per peer, and
    /// there is no peer to count against until the handshake names one. A session protocol is
    /// dialed after security, so in practice the identity is known.
    /// </remarks>
    public IDisposable? TryAcquireOutboundStream(ILocalPeer local, PeerId? remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (remote is null) return Allowance.Unscoped;

        (ILocalPeer, PeerId) key = (local, remote);
        return _dialing.TryAdd(key, 0) ? new Allowance(_dialing, key) : null;
    }

    /// <inheritdoc />
    public string DescribeOutboundRefusal(PeerId? remote) =>
        $"A ping to {remote?.ToString() ?? "the remote peer"} is already in flight; " +
        "the ping protocol keeps one outbound stream per peer.";

    /// <summary>Holds one pair's outbound allowance until disposed. Disposing twice is harmless.</summary>
    private sealed class Allowance : IDisposable
    {
        /// <summary>Handed out when there is no identity to scope the allowance to.</summary>
        internal static readonly IDisposable Unscoped = new Allowance(null, default);

        private readonly ConcurrentDictionary<(ILocalPeer, PeerId), byte>? _dialing;
        private readonly (ILocalPeer, PeerId) _key;
        private int _released;

        internal Allowance(ConcurrentDictionary<(ILocalPeer, PeerId), byte>? dialing, (ILocalPeer, PeerId) key)
        {
            _dialing = dialing;
            _key = key;
        }

        public void Dispose()
        {
            if (_dialing is null) return;
            if (Interlocked.Exchange(ref _released, 1) == 0) _dialing.TryRemove(_key, out _);
        }
    }

    /// <summary>
    /// Sends one ping and verifies the echo.
    /// </summary>
    /// <remarks>
    /// The <see href="https://github.com/libp2p/specs/blob/master/ping/ping.md">specification</see>
    /// says a dialing peer "MUST NOT keep more than one outbound stream for the ping protocol per
    /// peer". That is enforced through <see cref="IOutboundStreamLimited"/>, which the session
    /// consults before it queues the dial, so a second ping is refused with
    /// <see cref="ProtocolBusyException"/> without a stream being opened or negotiated. The
    /// dedicated exception type is what lets a caller tell local contention from a dead peer.
    /// </remarks>
    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        State session = context.State;
        ArgumentNullException.ThrowIfNull(session.RemoteAddress);

        await PingOnceAsync(channel, session.RemoteAddress);
    }

    private async Task PingOnceAsync(IChannel channel, Multiaddress remoteAddress)
    {
        byte[] ping = new byte[PayloadLength];
        Random.Shared.NextBytes(ping);
        ReadOnlySequence<byte> bytes = new(ping);

        _logger?.LogPing(remoteAddress);
        await channel.WriteAsync(bytes);
        _logger?.LogTrace("Sent ping: {ping}", Convert.ToHexString(ping));

        _logger?.ReadingPong(remoteAddress);
        ReadOnlySequence<byte> response = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll).OrThrow();
        _logger?.LogTrace("Received pong: {ping}", Convert.ToHexString(ping));

        _logger?.VerifyingPong(remoteAddress);
        if (!ping[0..PayloadLength].SequenceEqual(response.ToArray()))
        {
            _logger?.PingFailed(remoteAddress);
            throw new ApplicationException();
        }

        _logger?.LogPinged(remoteAddress);
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        Multiaddress? remoteAddress = context.State.RemoteAddress;
        ArgumentNullException.ThrowIfNull(remoteAddress);

        _logger?.PingListenStarted(remoteAddress);

        while (true)
        {
            _logger?.ReadingPing(remoteAddress);
            ReadResult read = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll);
            if (read.Result != IOResult.Ok)
            {
                break;
            }

            byte[] ping = read.Data.ToArray();
            _logger?.LogTrace("Received ping: {ping}", Convert.ToHexString(ping));

            _logger?.ReturningPong(remoteAddress);
            await channel.WriteAsync(new ReadOnlySequence<byte>(ping));
            _logger?.LogTrace("Sent pong: {ping}", Convert.ToHexString(ping));
        }

        _logger?.PingFinished(remoteAddress);
    }
}
