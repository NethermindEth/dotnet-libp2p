// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// A topic configured for the Gossipsub v1.3 Partial Messages extension.
/// </summary>
public interface IPartialMessagesTopic : ITopic
{
    /// <summary>
    /// Raised for application-defined partial message payloads received for this topic.
    /// </summary>
    event Action<PeerId, PartialMessage>? OnPartialMessage;

    bool RequestsPartialMessages { get; }
    bool SupportsSendingPartialMessages { get; }

    /// <summary>
    /// Sends an application-defined partial message to the topic mesh or fanout peers.
    /// </summary>
    void PublishPartial(byte[] groupId, byte[]? partialMessage = null, byte[]? partsMetadata = null);

    /// <summary>
    /// Sends an application-defined partial message to a connected peer, including
    /// a non-mesh peer selected by application gossip logic.
    /// </summary>
    void SendPartial(PeerId peerId, byte[] groupId, byte[]? partialMessage = null, byte[]? partsMetadata = null);
}
