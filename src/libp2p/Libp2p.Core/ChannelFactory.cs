// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Extensions;

namespace Nethermind.Libp2p.Core;

public class ChannelFactory : IChannelFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory? _loggerFactory;
    private IDictionary<IId, IChannelFactory> _factories;
    private readonly ILogger? _logger;

    public ChannelFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = _serviceProvider.GetService<ILoggerFactory>();
        _logger = _loggerFactory?.CreateLogger<ChannelFactory>();
    }

    public IEnumerable<IId> SubProtocols => _factories.Keys;

    public IChannel SubDial(IPeerContext context, IChannelRequest? req = null)
    {
        IId? subProtocolId = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        if (subProtocolId is not IProtocol subProtocol)
        {
            throw new Libp2pSetupException($"{nameof(IProtocol)} or {nameof(ITransportProtocol)} should be implemented by {subProtocolId?.GetType()}");
        }

        Channel channel = new();
        ChannelFactory? channelFactory = _factories[subProtocol] as ChannelFactory;

       

        _ = subProtocol.DialAsync(channel.Reverse, channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.DialFailed(subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                await channel.CloseAsync();

                req?.CompletionSource?.SetResult();
            });

        return channel;
    }

    public IChannel SubListen(IPeerContext context, IChannelRequest? req = null)
    {
        IId? subProtocolId = req?.SubProtocol ?? SubProtocols.FirstOrDefault();
        if (subProtocolId is not IProtocol subProtocol)
        {
            throw new Libp2pSetupException($"{nameof(IProtocol)} or {nameof(ITransportProtocol)} should be implemented by {subProtocolId?.GetType()}");
        }

        Channel channel = new();
        ChannelFactory? channelFactory = _factories[subProtocol] as ChannelFactory;


        _ = subProtocol.ListenAsync(channel.Reverse, channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.ListenFailed(subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                await channel.CloseAsync();

                req?.CompletionSource?.SetResult();
            });

        return channel;
    }

    public Task SubDialAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IId? subProtocolId = req?.SubProtocol ?? SubProtocols.FirstOrDefault();

        if (subProtocolId is not IProtocol subProtocol)
        {
            throw new Libp2pSetupException($"{nameof(IProtocol)} or {nameof(ITransportProtocol)} should be implemented by {subProtocolId?.GetType()}");
        }

        ChannelFactory? channelFactory = _factories[subProtocol] as ChannelFactory;

        return subProtocol.DialAsync(((Channel)parent), channelFactory, context)
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.DialAndBindFailed(subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                await parent.CloseAsync();

                req?.CompletionSource?.SetResult();
            });
    }

    public Task SubListenAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IId? subProtocolId = req?.SubProtocol ?? SubProtocols.FirstOrDefault();

        if (subProtocolId is not IProtocol subProtocol)
        {
            throw new Libp2pSetupException($"{nameof(IProtocol)} or {nameof(ITransportProtocol)} should be implemented by {subProtocolId?.GetType()}");
        }

        ChannelFactory? channelFactory = _factories[subProtocol] as ChannelFactory;

        return subProtocol.ListenAsync(((Channel)parent), channelFactory, context)
            .ContinueWith(async task =>
            {
                await parent.CloseAsync();
                req?.CompletionSource?.SetResult();
            });
    }

    public ChannelFactory Setup(IDictionary<IId, IChannelFactory> factories)
    {
        _factories = factories;
        return this;
    }
}
