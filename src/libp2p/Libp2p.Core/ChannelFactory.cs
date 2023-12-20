// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Extensions;

namespace Nethermind.Libp2p.Core;

public class ChannelFactory : IChannelFactory
{
    private readonly IServiceProvider _serviceProvider;
    private IId _parent;
    private IDictionary<IId, IChannelFactory> _factories;
    private readonly ILogger? _logger;

    public ChannelFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = _serviceProvider.GetService<ILoggerFactory>()?.CreateLogger<ChannelFactory>();
    }

    public IEnumerable<IId> SubProtocols => _factories.Keys;

    public IChannel SubDial(IPeerContext context, IChannelRequest? req = null)
    {
        IId subProtocol = GetSubProtocol(context, req);
        Channel channel = CreateChannel(subProtocol);
        SetupDialing(subProtocol, channel, context, req);
        return channel;
    }

    public IChannel SubDialAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IId subProtocol = GetSubProtocol(context, req);
        Channel channel = CreateChannel(subProtocol);
        channel.Bind(parent);
        SetupDialing(subProtocol, channel, context, req);
        return channel;
    }

    private IId GetSubProtocol(IPeerContext context, IChannelRequest? req = null)
    {
        IId? subProtocol = req?.SubProtocol ?? SubProtocols.FirstOrDefault();

        if (subProtocol is null)
        {
            throw new Exception("No protocol to dial");
        }

        return subProtocol;
    }

    private void SetupDialing(IId subProtocol, Channel channel, IPeerContext context, IChannelRequest? req)
    {
        ChannelFactory? channelFactory = _factories[subProtocol] as ChannelFactory;
        Func<IId, IChannel, IChannelFactory?, IPeerContext, Task>? call = (req as ChannelRequest)?.Dial;

        _logger?.DialStarted(channel.Id, subProtocol.Id, channelFactory.GetSubProtocols());

        Task diallingTask = null!;

        (diallingTask = call is null ? ((IDialer)subProtocol).DialAsync(channel.Reverse, channelFactory, context) : call(subProtocol, channel.Reverse, channelFactory, context))
            .ContinueWith(async task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    _logger?.DialFailed(channel.Id, subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                }
                if (!channel.IsClosed)
                {
                    await channel.CloseAsync(task.Exception is null);
                }

                (req as ChannelRequest)?.SetResult?.Invoke(diallingTask);
            });
    }

    public IChannel SubListen(IPeerContext context, IChannelRequest? req = null)
    {
        IId subProtocol = GetSubProtocol(context, req);
        Channel channel = CreateChannel(subProtocol);
        SetupListening(subProtocol, channel, context, req);
        return channel;
    }

    public IChannel SubListenAndBind(IChannel parent, IPeerContext context,
        IChannelRequest? req = null)
    {
        IId subProtocol = GetSubProtocol(context, req);
        Channel channel = CreateChannel(subProtocol);
        channel.Bind(parent);
        SetupListening(subProtocol, channel, context, req);
        return channel;
    }

    private void SetupListening(IId subProtocol, Channel channel, IPeerContext context, IChannelRequest? req)
    {
        ChannelFactory? channelFactory = _factories[subProtocol] as ChannelFactory;

        _logger?.ListenStarted(channel.Id, subProtocol.Id, channelFactory.GetSubProtocols());

        Task listeningTask = null!;

        (listeningTask = ((IListener)subProtocol).ListenAsync(channel.Reverse, channelFactory, context))
            .ContinueWith(async task =>
            {
                try
                {
                    if (!task.IsCompletedSuccessfully)
                    {
                        _logger?.ListenFailed(channel.Id, subProtocol.Id, task.Exception, task.Exception.GetErrorMessage());
                    }
                    if (!channel.IsClosed)
                    {
                        await channel.CloseAsync();
                    }

                (req as ChannelRequest)?.SetResult?.Invoke(listeningTask);
                }
                catch
                {

                }
            });
    }

    public ChannelFactory Setup(IId parent, IDictionary<IId, IChannelFactory> factories)
    {
        _parent = parent;
        _factories = factories;
        return this;
    }

    private Channel CreateChannel(IId? subProtocol)
    {
        Channel channel = ActivatorUtilities.CreateInstance<Channel>(_serviceProvider);
        channel.Id = $"{_parent.Id} <> {subProtocol?.Id}";
        _logger?.ChannelCreated(channel.Id);
        return channel;
    }
}
