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
public class MultiaddressBasedSelector(ILoggerFactory? loggerFactory = null): ITransportProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MultiaddressBasedSelector>();

    public string Id => "multiaddr-select";

    public Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        return ConnectAsync(channel, channelFactory, context, false);
    }

    public Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        return ConnectAsync(channel, channelFactory, context, true);
    }

    protected async Task ConnectAsync(IChannel _, IChannelFactory? channelFactory, IPeerContext context, bool isListener)
    {
        ITransportProtocol protocol = null!;
        // TODO: deprecate quic
        if (context.LocalPeer.Address.Has<QUICv1>())
        {
            protocol = channelFactory!.SubProtocols.FirstOrDefault(proto => proto.Id == "quic-v1") as ITransportProtocol ?? throw new ApplicationException("QUICv1 is not supported");
        }
        else if (context.LocalPeer.Address.Has<TCP>())
        {
            protocol = channelFactory!.SubProtocols.FirstOrDefault(proto => proto.Id == "ip-tcp") as ITransportProtocol ?? throw new ApplicationException("TCP is not supported");
        }
        else if (context.LocalPeer.Address.Has<QUIC>())
        {
            throw new ApplicationException("QUIC is not supported. Use QUICv1 instead.");
        }
        else
        {
            throw new NotImplementedException($"No transport protocol found for the given address: {context.LocalPeer.Address}");
        }

        _logger?.LogPickedProtocol(protocol.Id, isListener ? "listen" : "dial");

        await (isListener
            ? protocol.ListenAsync(_, channelFactory, context)
            : protocol.DialAsync(_, channelFactory, context));
    }
}
