// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// Select protocol based on multiaddr
/// </summary>
public class MultiaddrBasedSelectorProtocol(ILoggerFactory? loggerFactory = null) : SymmetricProtocol, IProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MultiaddrBasedSelectorProtocol>();

    public string Id => "multiaddr-select";

    protected override async Task ConnectAsync(IChannel _, IChannelFactory? channelFactory, IPeerContext context, bool isListener)
    {
        IProtocol protocol = null!;
        if (context.LocalPeer.Address.Has<QUIC>())
        {
            protocol = channelFactory!.SubProtocols.FirstOrDefault(proto => proto.Id.Contains("quic")) ?? throw new ApplicationException("QUIC is not supported");
        }
        else if (context.LocalPeer.Address.Has<QUIC>())
        {
            throw new ApplicationException("QUIC version draft-29 is not supported.");
        }
        else if (context.LocalPeer.Address.Has<TCP>())
        {
            protocol = channelFactory!.SubProtocols.FirstOrDefault(proto => proto.Id.Contains("tcp")) ?? throw new ApplicationException("TCP is not supported");
        }
        else
        {
            throw new NotImplementedException($"No transport protocol found for the given address: {context.LocalPeer.Address}");
        }

        _logger?.LogPickedProtocol(protocol.Id, isListener ? "listen" : "dial");

        await (isListener
            ? channelFactory.SubListen(context, protocol)
            : channelFactory.SubDial(context, protocol));
    }
}
