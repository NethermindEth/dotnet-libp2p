// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Core;

public class ChannelFactory : IChannelFactory
{
    private static int id = 1;
    private readonly IServiceProvider _serviceProvider;
    private IProtocol _parent;
    private IChannelFactory _subchannelsFactory;
    private readonly ILogger? _logger;

    public ChannelFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = _serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<ChannelFactory>();
    }

    public IEnumerable<IProtocol> SubProtocols { get; private set; }
    public BlockingCollection<IChannelRequest> SubDialRequests { get; } = new();

    public IChannel SubDial(IPeerContext context, IChannelRequest? req = null)
    {
        IProtocol? subProtocol = SubProtocols.FirstOrDefault();

        Channel chan = CreateChannel(subProtocol);
        switch (subProtocol)
        {
            default:
                ChannelFactory? sf = _subchannelsFactory as ChannelFactory;
                ChannelFactory? s = req is null
                    ? sf
                    : ActivatorUtilities.CreateInstance<ChannelFactory>(_serviceProvider)
                        .Connect(sf._parent, sf._subchannelsFactory, req.SubProtocol);
                subProtocol.DialAsync(chan.Reverse, s, context).ContinueWith(async t =>
                {
                    if (!chan.IsClosed)
                    {
                        await chan.CloseAsync();
                    }

                    req.CompletionSource.SetResult(true);
                });

                break;
        }

        return chan;
    }

    public IChannel SubListen(IPeerContext context, IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel chan = CreateChannel(subProtocol);
        switch (subProtocol)
        {
            default:
                _ = subProtocol.ListenAsync(chan.Reverse, _subchannelsFactory, context);
                break;
        }

        return chan;
    }

    public IChannel SubDialAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel chan = CreateChannel(subProtocol);
        chan.Bind(parent);
        switch (subProtocol)
        {
            default:
                _ = subProtocol.DialAsync(chan.Reverse, _subchannelsFactory, context);
                break;
        }

        return chan;
    }

    public IChannel SubListenAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel chan = CreateChannel(subProtocol);
        chan.Bind(parent);
        switch (subProtocol)
        {
            default:
                _ = subProtocol.ListenAsync(chan.Reverse, _subchannelsFactory, context);
                break;
        }

        return chan;
    }

    public void Connected(IRemotePeer peer)
    {
        OnRemotePeerConnection?.Invoke(peer);
    }

    public event RemotePeerConnected? OnRemotePeerConnection;

    public ChannelFactory Connect(IProtocol parent, IChannelFactory subchannelsFactory,
        params IProtocol[] subProtocols)
    {
        _parent = parent;
        _subchannelsFactory = subchannelsFactory;
        SubProtocols = subProtocols;
        return this;
    }

    private Channel CreateChannel(IProtocol subprotocol)
    {
        Channel chan = ActivatorUtilities.CreateInstance<Channel>(_serviceProvider);
        chan.Id = $"{_parent.Id} <> {subprotocol?.Id}";
        _logger?.LogDebug("Create chan {0}", chan.Id);
        return chan;
    }
}
