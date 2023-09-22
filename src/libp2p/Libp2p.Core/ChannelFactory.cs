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

        _logger?.LogAction(nameof(SubDial), chan.Id, subProtocol.Id, sf?.SubProtocols.Select(protocol => protocol.Id) ?? Enumerable.Empty<string>());

        _ = subProtocol.DialAsync(chan.Reverse, sf, context).ContinueWith(async t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                _logger?.LogCompletedUnsuccessfully(nameof(SubDial), chan.Id, subProtocol.Id, t.Exception, t.Exception?.Message ?? "unknown");
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
        Channel chan = CreateChannel(subProtocol);
        ChannelFactory? sf = _factories[subProtocol] as ChannelFactory;

        _logger?.LogAction(nameof(SubListen), chan.Id, subProtocol.Id, sf?.SubProtocols.Select(s => s.Id) ?? Enumerable.Empty<string>());

        _ = subProtocol.ListenAsync(chan.Reverse, sf, context).ContinueWith(async t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                _logger?.LogCompletedUnsuccessfully(nameof(SubListen), chan.Id, subProtocol.Id, t.Exception, t.Exception?.Message ?? "unknown");
            }
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
        ChannelFactory? sf = _factories[subProtocol] as ChannelFactory;

        _logger?.LogAction(nameof(SubDialAndBind), chan.Id, subProtocol.Id, sf?.SubProtocols.Select(s => s.Id) ?? Enumerable.Empty<string>());

        chan.Bind(parent);
        _ = subProtocol.DialAsync(chan.Reverse, sf, context).ContinueWith(async t =>
       {
           if (!t.IsCompletedSuccessfully)
           {
               _logger?.LogCompletedUnsuccessfully(nameof(SubDialAndBind), chan.Id, subProtocol.Id, t.Exception, t.Exception?.Message ?? "unknown");
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
        ChannelFactory? sf = _factories[subProtocol] as ChannelFactory;

        _logger?.LogAction(nameof(SubListenAndBind), chan.Id, subProtocol.Id, sf?.SubProtocols.Select(s => s.Id) ?? Enumerable.Empty<string>());

        chan.Bind(parent);
        _ = subProtocol.ListenAsync(chan.Reverse, _factories[subProtocol], context).ContinueWith(async t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                _logger?.LogCompletedUnsuccessfully(nameof(SubListenAndBind), chan.Id, subProtocol.Id, t.Exception, t.Exception?.Message ?? "unknown");
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

    private Channel CreateChannel(IProtocol? subProtocol)
    {
        Channel chan = ActivatorUtilities.CreateInstance<Channel>(_serviceProvider);
        chan.Id = $"{_parent.Id} <> {subProtocol?.Id}";
        _logger?.ChanCreated(chan.Id);
        return chan;
    }
}
