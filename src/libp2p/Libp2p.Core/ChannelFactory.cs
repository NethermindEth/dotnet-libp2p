// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Extensions;

namespace Nethermind.Libp2p.Core;

public class ChannelFactory : IChannelFactory
{
    private readonly IServiceProvider _serviceProvider;
    private IProtocol? _parent;
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
        IProtocol subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel channel = CreateChannel(subProtocol);
        ChannelFactory? channelFactory = _factories[subProtocol] as ChannelFactory;

        _logger?.DialStarted(channel.Id, subProtocol.Id, channelFactory?.GetSubProtocols());

        _ = subProtocol.DialAsync(channel.Reverse, channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.DialFailed(channel?.Id, subProtocol?.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                if (channel != null && !channel.IsClosed)
                {
                    await channel.CloseAsync(task.Exception is null);
                }

                req?.CompletionSource?.SetResult();
            });

        return channel;
    }

    public IChannel SubListen(IPeerContext context, IChannelRequest? req = null)
    {
        IProtocol subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel channel = CreateChannel(subProtocol);
        ChannelFactory channelFactory = _factories[subProtocol] as ChannelFactory;

        _logger?.ListenStarted(channel.Id, subProtocol.Id, channelFactory.GetSubProtocols());

        _ = subProtocol.ListenAsync(channel.Reverse, channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.ListenFailed(channel.Id, subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                if (!channel.IsClosed)
                {
                    await channel.CloseAsync();
                }

                req?.CompletionSource?.SetResult();
            });

        return channel;
    }

    public IChannel SubDialAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel channel = CreateChannel(subProtocol);
        ChannelFactory? channelFactory = _factories[subProtocol] as ChannelFactory;

        _logger?.DialAndBindStarted(channel.Id, subProtocol.Id, channelFactory.GetSubProtocols());

        channel.Bind(parent);
        _ = subProtocol.DialAsync(channel.Reverse, channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.DialAndBindFailed(channel.Id, subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                if (!channel.IsClosed)
                {
                    await channel.CloseAsync();
                }

                req?.CompletionSource?.SetResult();
            });

        return channel;
    }

    public IChannel SubListenAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IProtocol? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        Channel channel = CreateChannel(subProtocol);
        ChannelFactory? channelFactory = _factories[subProtocol] as ChannelFactory;

        _logger?.ListenAndBindStarted(channel.Id, subProtocol.Id, channelFactory.GetSubProtocols());

        channel.Bind(parent);
        _ = subProtocol.ListenAsync(channel.Reverse, channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.ListenAndBindFailed(channel.Id, subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                if (!channel.IsClosed)
                {
                    await channel.CloseAsync();
                }

                req?.CompletionSource?.SetResult();
            });

        return channel;
    }

    public ChannelFactory Setup(IProtocol parent, IDictionary<IProtocol, IChannelFactory> factories)
    {
        _parent = parent;
        _factories = factories;
        return this;
    }

    private Channel CreateChannel(IProtocol? subProtocol)
    {
        Channel channel = ActivatorUtilities.CreateInstance<Channel>(_serviceProvider);
        channel.Id = $"{_parent.Id} <> {subProtocol?.Id}";
        _logger?.ChannelCreated(channel.Id);
        return channel;
    }
}
