// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Benchmarks;

/// <summary>
/// Builds a started router whose peers are in-memory writers that complete at once,
/// so a benchmark measures the router and not a transport.
/// </summary>
internal static class RouterFixture
{
    private static readonly Task Never = new TaskCompletionSource().Task;

    public static PubsubSettings Settings(Func<Message, MessageId>? getMessageId = null, bool sign = false)
    {
        PubsubSettings settings = new()
        {
            AutoConnect = false,
            HeartbeatInterval = int.MaxValue,
            DecayInterval = int.MaxValue,
            MessageCacheTtl = int.MaxValue,
            DefaultSignaturePolicy = sign ? PubsubSettings.SignaturePolicy.StrictSign : PubsubSettings.SignaturePolicy.StrictNoSign,
        };
        if (getMessageId is not null)
        {
            settings.GetMessageId = getMessageId;
        }

        return settings;
    }

    public static PubsubRouter Start(PubsubSettings settings)
    {
        PubsubRouter router = new(new PeerStore(), settings);
        router.StartAsync(new LocalPeer(Identity(0))).GetAwaiter().GetResult();
        return router;
    }

    public static Identity Identity(int index)
    {
        byte[] key = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(28), index + 1);
        return new Identity(key);
    }

    /// <summary>Connects a peer that subscribes to <paramref name="topics"/> and grafts the first <paramref name="graftTopics"/> of them.</summary>
    public static PubsubRouter.PubsubPeer Connect(PubsubRouter router, Identity identity, string protocol, string[] topics, int graftTopics)
    {
        Multiaddress address = $"/p2p/{identity.PeerId}";
        PubsubRouter.PubsubPeer peer = router.OutboundConnection(address, protocol, Never,
            static (_, _) => ValueTask.FromResult(IOResult.Ok), static () => ValueTask.CompletedTask, static () => Task.CompletedTask)
            ?? throw new InvalidOperationException("The router rejected the benchmark peer.");

        Rpc hello = new();
        foreach (string topic in topics)
        {
            hello.Subscriptions.Add(new Rpc.Types.SubOpts { Subscribe = true, Topicid = topic });
        }

        if (graftTopics > 0)
        {
            hello.Control = new ControlMessage();
            for (int i = 0; i < graftTopics; i++)
            {
                hello.Control.Graft.Add(new ControlGraft { TopicID = topics[i] });
            }
        }

        router.OnRpcAsync(identity.PeerId, hello, protocol).GetAwaiter().GetResult();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        return peer;
    }

    private sealed class LocalPeer(Identity identity) : ILocalPeer
    {
        public Identity Identity { get; } = identity;
        public ObservableCollection<Multiaddress> ListenAddresses { get; } = [];
        public event Connected? OnConnected { add { } remove { } }
        public Task<ISession> DialAsync(Multiaddress addr, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ISession> DialAsync(Multiaddress[] samePeerAddrs, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ISession> DialAsync(PeerId peerId, CancellationToken token = default) => throw new NotSupportedException();
        public Task StartListenAsync(Multiaddress[]? addrs = default, CancellationToken token = default) => Task.CompletedTask;
        public T? GetProtocol<T>() where T : class, IProtocol => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
