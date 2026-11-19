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
    /// Two per peer because the ping
    /// <see href="https://github.com/libp2p/specs/blob/master/ping/ping.md">specification</see> says
    /// a listener "SHOULD accept at most two streams per peer", the second being the overlap while a
    /// dialer rotates streams. The generic 64 that the other families use is far above that.
    /// </remarks>
    public InboundStreamLimit? Ping { get; init; } = new(512, 2);
    public InboundStreamLimit? RelayStop { get; init; } = new(512, 64);
    public InboundStreamLimit? RelayHop { get; init; } = new(512, 64);
    public InboundStreamLimit? Pubsub { get; init; } = new(512, 64);
}
