// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Core;

public class ChannelFactory : IChannelFactory
{
    private readonly IServiceProvider _serviceProvider;
    private IProtocol _parent;
    private IDictionary<IProtocol, IChannelFactory> _factories;
    private readonly ILogger? _logger;

    public ChannelFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = _serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<ChannelFactory>();
    }

    public IEnumerable<IProtocol> SubProtocols => _factories.Keys;

    public IChannel SubDial(IPeerContext context, IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();

        Channel chan = CreateChannel(subProtocol);
        ChannelFactory? sf = _factories[subProtocol] as ChannelFactory;

        _logger?.LogDebug("Dial {chan} {sf}", chan.Id, sf.SubProtocols);
        _ = subProtocol.DialAsync(chan.Reverse, sf, context).ContinueWith(async t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                _logger?.LogError("Dial error {proto} via {chan}: {error}", chan.Id, subProtocol.Id, t.Exception?.Message ?? "unknown");
            }
            if (!chan.IsClosed)
            {
                await chan.CloseAsync(t.Exception is null);
            }

            req?.CompletionSource?.SetResult();
        });

        return chan;
    }

    public IChannel SubListen(IPeerContext context, IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        PeerContext peerContext = (PeerContext)context;

        Channel chan = CreateChannel(subProtocol);

        _logger?.LogDebug("Listen {chan} on protocol {sp} with sub-protocols {sf}", chan.Id, subProtocol.Id, _factories[subProtocol].SubProtocols.Select(s => s.Id));

        _ = subProtocol.ListenAsync(chan.Reverse, _factories[subProtocol], context).ContinueWith(async t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                _logger?.LogError("Listen error {proto} via {chan}: {error}", subProtocol.Id, chan.Id, t.Exception?.Message ?? "unknown");
            }
            IEnumerable<IProtocol> dd = _factories[subProtocol].SubProtocols;

            if (!chan.IsClosed)
            {
                await chan.CloseAsync();
            }

            req?.CompletionSource?.SetResult();
        });

        return chan;
    }

    public IChannel SubDialAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel chan = CreateChannel(subProtocol);
        chan.Bind(parent);
        _ = subProtocol.DialAsync(chan.Reverse, _factories[subProtocol], context).ContinueWith(async t =>
       {
           if (!t.IsCompletedSuccessfully)
           {
               _logger?.LogError("SubDialAndBind error {proto} via {chan}: {error}", chan.Id, subProtocol.Id, t.Exception?.Message ?? "unknown");
           }

           if (!chan.IsClosed)
           {
               await chan.CloseAsync();
           }

           req?.CompletionSource?.SetResult();
       });

        return chan;
    }

    public IChannel SubListenAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel chan = CreateChannel(subProtocol);
        chan.Bind(parent);
        _ = subProtocol.ListenAsync(chan.Reverse, _factories[subProtocol], context).ContinueWith(async t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                _logger?.LogError("SubListenAndBind error {proto} via {chan}: {error}", subProtocol.Id, chan.Id, t.Exception?.Message ?? "unknown");
            }
            if (!chan.IsClosed)
            {
                await chan.CloseAsync();
            }

            req?.CompletionSource?.SetResult();
        });

        return chan;
    }

    public ChannelFactory Setup(IProtocol parent, IDictionary<IProtocol, IChannelFactory> factories)
    {
        _parent = parent;
        _factories = factories;
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
