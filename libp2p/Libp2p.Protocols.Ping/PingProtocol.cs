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
public class PingProtocol : ISessionProtocol
{
    private const int PayloadLength = 32;

    public string Id => "/ipfs/ping/1.0.0";
    private readonly ILogger<PingProtocol>? _logger;

    /// <summary>
    /// Sessions with an outbound ping in flight, keyed by the session's own state object. Entries
    /// are removed in a finally, so the table holds only what is running.
    /// <para>
    /// One registration is shared by every local peer built from the same factory, and
    /// <see cref="ISessionContext"/> carries no local identity — <c>State.LocalAddress</c> is an
    /// endpoint without a <c>/p2p</c> suffix — so keying on the remote peer alone would make two
    /// local nodes dialing one server contend for a single allowance. Keying on the session gives
    /// per connection to a peer rather than strictly per peer, so a node holding two connections to
    /// one remote could keep two outbound pings. That is the part of the rule this cannot enforce
    /// without a local identity on the context.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<State, byte> _dialing = new();

    public PingProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PingProtocol>();
    }

    /// <summary>
    /// Sends one ping and verifies the echo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see href="https://github.com/libp2p/specs/blob/master/ping/ping.md">specification</see>
    /// says a dialing peer "MUST NOT keep more than one outbound stream for the ping protocol per
    /// peer", so overlapping pings on a session run one at a time rather than together.
    /// </para>
    /// <para>
    /// The second ping is refused with <see cref="ProtocolBusyException"/> rather than queued: by the
    /// time this runs the stream is already open and negotiated, so waiting would park an idle ping
    /// stream on the session instead of ending it. The dedicated type is what lets a caller tell
    /// local contention from a dead peer — <c>LivenessProbe</c> disconnects on anything else.
    /// </para>
    /// <para>
    /// This cannot keep the second stream from being opened at all, which is what the rule actually
    /// asks: the request is queued and <c>/ipfs/ping/1.0.0</c> negotiated before any protocol code
    /// runs. Doing that needs a seam ahead of negotiation, or serialization in the caller.
    /// </para>
    /// </remarks>
    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        State session = context.State;
        ArgumentNullException.ThrowIfNull(session.RemoteAddress);

        if (!_dialing.TryAdd(session, 0))
        {
            throw new ProtocolBusyException(
                $"A ping to {session.RemotePeerId?.ToString() ?? session.RemoteAddress.ToString()} is already in flight; " +
                "the ping protocol keeps one outbound stream per peer.");
        }

        try
        {
            await PingOnceAsync(channel, session.RemoteAddress);
        }
        finally
        {
            _dialing.TryRemove(session, out _);
        }
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
