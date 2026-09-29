// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
/// Bounded local partial-message group state used to give applications concrete
/// group identifiers when gossiping to non-mesh peers.
/// </summary>
internal sealed class PartialMessageGossipCache
{
    private sealed class Group(byte[] id, int remainingHeartbeats)
    {
        public byte[] Id { get; } = id;
        public int RemainingHeartbeats { get; set; } = remainingHeartbeats;
        public LinkedListNode<Group>? AgeNode { get; set; }
    }

    private sealed class TopicGroups
    {
        public Dictionary<string, Group> ById { get; } = [];
        public LinkedList<Group> ByAge { get; } = [];
    }

    private readonly Dictionary<string, TopicGroups> topics = [];
    private readonly int maxGroupsPerTopic;
    private readonly int groupTtlHeartbeats;

    public PartialMessageGossipCache(int maxGroupsPerTopic, int groupTtlHeartbeats)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxGroupsPerTopic);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupTtlHeartbeats);
        this.maxGroupsPerTopic = maxGroupsPerTopic;
        this.groupTtlHeartbeats = groupTtlHeartbeats;
    }

    public void Track(string topic, ReadOnlySpan<byte> groupId)
    {
        TopicGroups groups = topics.GetValueOrDefault(topic) ?? CreateTopic(topic);
        string key = Convert.ToHexString(groupId);
        if (groups.ById.TryGetValue(key, out Group? existing))
        {
            existing.RemainingHeartbeats = groupTtlHeartbeats;
            groups.ByAge.Remove(existing.AgeNode!);
            existing.AgeNode = groups.ByAge.AddLast(existing);
            return;
        }

        if (groups.ById.Count == maxGroupsPerTopic)
        {
            Remove(groups, groups.ByAge.First!.Value);
        }

        Group group = new(groupId.ToArray(), groupTtlHeartbeats);
        group.AgeNode = groups.ByAge.AddLast(group);
        groups.ById.Add(key, group);
    }

    public IReadOnlyList<byte[]> GetGroupIds(string topic)
    {
        return topics.TryGetValue(topic, out TopicGroups? groups)
            ? groups.ByAge.Select(group => group.Id.ToArray()).ToArray()
            : [];
    }

    public void Heartbeat()
    {
        foreach ((string topic, TopicGroups groups) in topics.ToArray())
        {
            LinkedListNode<Group>? node = groups.ByAge.First;
            while (node is not null)
            {
                LinkedListNode<Group>? next = node.Next;
                Group group = node.Value;
                group.RemainingHeartbeats--;
                if (group.RemainingHeartbeats == 0)
                {
                    Remove(groups, group);
                }

                node = next;
            }

            if (groups.ById.Count == 0)
            {
                topics.Remove(topic);
            }
        }
    }

    private TopicGroups CreateTopic(string topic)
    {
        TopicGroups groups = new();
        topics.Add(topic, groups);
        return groups;
    }

    private static void Remove(TopicGroups groups, Group group)
    {
        groups.ById.Remove(Convert.ToHexString(group.Id));
        groups.ByAge.Remove(group.AgeNode!);
        group.AgeNode = null;
    }
}
