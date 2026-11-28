// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p;

/// <summary>Inbound stream allowances for built-in protocols. Null explicitly disables a family's limit.</summary>
/// <remarks>Totals span all local peers sharing the builder's registrations. Pubsub versions share one allowance.</remarks>
public sealed class BuiltInProtocolLimits
{
    public InboundStreamLimit? Identify { get; init; } = new(512, 64);
    public InboundStreamLimit? IdentifyPush { get; init; } = new(512, 64);
    /// <remarks>
    /// <para>
    /// Ping's specification says a listener "SHOULD accept at most two streams per peer", but two
    /// cannot be applied here yet: one limit instance is shared by every local peer of a factory and
    /// <see cref="InboundStreamLimit"/> counts per remote <c>PeerId</c> only, so a remote holding one
    /// stream to each of three local peers would have the third refused. Scoping the count to the
    /// local peer as well is the prerequisite; until then this keeps the generic allowance.
    /// </para>
    /// <para>
    /// That scoping is not a free change. Counting per remote across the whole registration is
    /// deliberate — it bounds what one remote costs a factory in total, and
    /// <c>KadReadLimitsTests.RegisteredLimitsRefuseBeforeDispatchAndRecoverAfterReadTimeout</c>
    /// asserts it. Keying on the pair instead would let one remote take this allowance at every
    /// local peer, so it is a resource decision for the whole limiter rather than a ping detail.
    /// The outbound half of the rule does not depend on it and is enforced by
    /// <see cref="IOutboundStreamLimited"/>.
    /// </para>
    /// </remarks>
    public InboundStreamLimit? Ping { get; init; } = new(512, 64);
    public InboundStreamLimit? RelayStop { get; init; } = new(512, 64);
    public InboundStreamLimit? RelayHop { get; init; } = new(512, 64);
    public InboundStreamLimit? Pubsub { get; init; } = new(512, 64);
}
