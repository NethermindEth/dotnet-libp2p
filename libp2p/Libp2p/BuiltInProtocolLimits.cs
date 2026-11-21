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
    /// Ping's specification says a listener "SHOULD accept at most two streams per peer", but two
    /// cannot be applied here yet: one limit instance is shared by every local peer of a factory and
    /// <see cref="InboundStreamLimit"/> counts per remote <c>PeerId</c> only, so a remote holding one
    /// stream to each of three local peers would have the third refused. Scoping the count to the
    /// local peer as well is the prerequisite; until then this keeps the generic allowance.
    /// </remarks>
    public InboundStreamLimit? Ping { get; init; } = new(512, 64);
    public InboundStreamLimit? RelayStop { get; init; } = new(512, 64);
    public InboundStreamLimit? RelayHop { get; init; } = new(512, 64);
    public InboundStreamLimit? Pubsub { get; init; } = new(512, 64);
}
