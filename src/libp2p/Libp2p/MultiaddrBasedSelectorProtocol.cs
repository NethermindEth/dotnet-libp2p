// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

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
        IProtocol protocol = context.LocalPeer.Address.Has(Core.Enums.Multiaddr.Quic) ?
                channelFactory!.SubProtocols.FirstOrDefault(proto => proto.Id.Contains("quic")) ?? throw new ApplicationException("QUIC is not supported") :
                channelFactory!.SubProtocols.FirstOrDefault(proto => proto.Id.Contains("tcp")) ?? throw new ApplicationException("TCP is not supported");

        _logger?.LogDebug("{protocol} has been picked to {action}", protocol.Id, isListener ? "listen" : "dial");

        await (isListener
            ? channelFactory.SubListen(context, protocol)
            : channelFactory.SubDial(context, protocol));
    }
}
