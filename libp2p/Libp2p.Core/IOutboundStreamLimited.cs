// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>
/// A protocol that caps how many outbound streams it keeps for one local/remote pair.
/// </summary>
/// <remarks>
/// <para>
/// The session consults this before it queues a dial request, which is the only point early enough
/// to keep a refused stream from being opened at all. A protocol enforcing the cap inside its own
/// <c>DialAsync</c> runs after multistream has already negotiated it, so the remote has seen the
/// stream by then.
/// </para>
/// <para>
/// The allowance is scoped to the pair, not to the remote alone: one protocol instance is shared by
/// every local peer built from the same factory, so keying on the remote would make two local peers
/// dialing one server contend for a single allowance.
/// </para>
/// </remarks>
public interface IOutboundStreamLimited : IProtocol
{
    /// <summary>
    /// Takes an outbound allowance for this pair, or returns null when none is free. Disposing the
    /// returned handle gives the allowance back; the session does that when the dial completes.
    /// </summary>
    /// <param name="local">The peer opening the stream.</param>
    /// <param name="remote">The peer being dialed, or null when it is not yet identified.</param>
    IDisposable? TryAcquireOutboundStream(ILocalPeer local, PeerId? remote);

    /// <summary>Explains a refusal, for the exception the session raises.</summary>
    string DescribeOutboundRefusal(PeerId? remote);
}
