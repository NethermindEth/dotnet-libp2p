// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// Application-defined data carried by the Gossipsub v1.3 Partial Messages extension.
/// </summary>
public sealed class PartialMessage
{
    public PartialMessage(string topicId, byte[] groupId, byte[]? partialData, byte[]? partsMetadata)
    {
        ArgumentNullException.ThrowIfNull(topicId);
        ArgumentNullException.ThrowIfNull(groupId);

        TopicId = topicId;
        GroupId = groupId;
        PartialData = partialData;
        PartsMetadata = partsMetadata;
    }

    public string TopicId { get; }
    public byte[] GroupId { get; }
    public byte[]? PartialData { get; }
    public byte[]? PartsMetadata { get; }
}

/// <summary>
/// Per-topic capabilities advertised through Gossipsub subscription options.
/// </summary>
public sealed class PartialMessagesTopicOptions
{
    /// <summary>
    /// Requests partial data from peers. This also requires support for sending
    /// partial messages.
    /// </summary>
    public bool RequestPartialMessages { get; init; }

    /// <summary>
    /// Signals that this topic can send partial data and receive parts metadata.
    /// </summary>
    public bool SupportSendingPartialMessages { get; init; }
}
