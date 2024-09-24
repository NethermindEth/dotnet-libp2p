// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using System.Net.Security;
using System.Runtime.Versioning;

namespace Nethermind.Libp2p.Stack;

public class Libp2pPeerFactoryBuilder : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>,
    ILibp2pPeerFactoryBuilder
{
    private bool enforcePlaintext;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly MultiplexerSettings? _multiplexerSettings;

    public ILibp2pPeerFactoryBuilder WithPlaintextEnforced()
    {
        enforcePlaintext = true;
        return this;
    }

    public Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : base(serviceProvider)
    {
        _loggerFactory = serviceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        _multiplexerSettings = serviceProvider?.GetService(typeof(MultiplexerSettings)) as MultiplexerSettings;

    }

    protected override ProtocolStack BuildStack()
    {

        ProtocolStack tcpEncryptionStack = enforcePlaintext ?
            Over<PlainTextProtocol>() :
         Over<NoiseProtocol>().Or(new TlsProtocol(_multiplexerSettings, _loggerFactory));

        ProtocolStack tcpStack =
            Over<IpTcpProtocol>()
            .Over<MultistreamProtocol>()
            .Over(tcpEncryptionStack)
            .Over<MultistreamProtocol>()
            .Over<YamuxProtocol>();

        return
            Over<MultiaddressBasedSelectorProtocol>()
            // Quic is not working well, and requires consumers to mark projects with preview
            //.Over<QuicProtocol>().Or(tcpStack)
            .Over(tcpStack)
            .Over<MultistreamProtocol>()
            .AddAppLayerProtocol<IdentifyProtocol>()
            .AddAppLayerProtocol<PingProtocol>()
            //.AddAppLayerProtocol<GossipsubProtocolV12>()
            //.AddAppLayerProtocol<GossipsubProtocolV11>()
            .AddAppLayerProtocol<GossipsubProtocol>()
            .AddAppLayerProtocol<FloodsubProtocol>();
    }
}
