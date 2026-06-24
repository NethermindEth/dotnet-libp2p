// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Tls;
using System.Diagnostics.CodeAnalysis;

namespace MauiChat;

internal sealed class MauiChatPeerFactoryBuilder(IServiceProvider? serviceProvider = default)
    : PeerFactoryBuilderBase<MauiChatPeerFactoryBuilder, PeerFactory>(serviceProvider)
{
    private bool addQuic;

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IpTcpProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(QuicProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NoiseProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(TlsProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(YamuxProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(MultistreamProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(PingProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IdentifyPushProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IdentifyProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(ChatProtocol))]
    private static void PreserveProtocolMetadata() { }

    public MauiChatPeerFactoryBuilder WithQuic()
    {
        addQuic = true;
        return this;
    }

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        PreserveProtocolMetadata();

        ProtocolRef tcp = Get<IpTcpProtocol>();
        ProtocolRef[] transports = [tcp];
        ProtocolRef[] encryption = [Get<NoiseProtocol>(), Get<TlsProtocol>()];
        ProtocolRef[] muxers = [Get<YamuxProtocol>()];
        ProtocolRef[] selector = [Get<MultistreamProtocol>()];

        Connect(transports, [Get<MultistreamProtocol>()], encryption, [Get<MultistreamProtocol>()], muxers, selector);

        ProtocolRef[] apps = [
            Get<IdentifyProtocol>(),
            Get<IdentifyPushProtocol>(),
            Get<PingProtocol>(),
            .. additionalProtocols,
        ];
        Connect(selector, apps);

        List<ProtocolRef> topTransports = [.. transports];

        if (addQuic)
        {
            ProtocolRef quic = Get<QuicProtocol>();
            Connect([quic], selector);
            topTransports.Add(quic);
        }

        return [.. topTransports];
    }
}
