// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Stack;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// Select protocol based on multiaddr
/// </summary>
//public class MultiaddressBasedSelector(ILoggerFactory? loggerFactory = null): ITransportProtocol
//{
//    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MultiaddressBasedSelector>();

//    public string Id => "multiaddr-select";

//    public Task DialAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
//    {
//        return ConnectAsync(context, listenAddr, token, false);
//    }

//    public Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
//    {
//        return ConnectAsync(context, listenAddr, token, true);
//    }

//    protected async Task ConnectAsync(ITransportContext context, Multiaddress addr, CancellationToken token, bool isListener)
//    {
//        throw new NotImplementedException();
//        //ITransportProtocol protocol = null!;

//        //if (addr.Has<QUICv1>())
//        //{
//        //    protocol = context!.SubProtocols.FirstOrDefault(proto => proto.Id == "quic-v1") as ITransportProtocol ?? throw new ApplicationException("QUICv1 is not supported");
//        //}
//        //else if (addr.Has<TCP>())
//        //{
//        //    protocol = channelFactory!.SubProtocols.FirstOrDefault(proto => proto.Id == "ip-tcp") as ITransportProtocol ?? throw new ApplicationException("TCP is not supported");
//        //}
//        //else
//        //{
//        //    throw new NotImplementedException($"No transport protocol found for the given address: {addr}");
//        //}

//        //_logger?.LogPickedProtocol(protocol.Id, isListener ? "listen" : "dial");

//        //await (isListener
//        //    ? protocol.ListenAsync(context, addr, token)
//        //    : protocol.DialAsync(context, addr, token));
//    }
//}
