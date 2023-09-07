// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/multiformats/multistream-select
/// </summary>
public class MultistreamProtocol : IProtocol
{
    private readonly ILogger? _logger;
    private const string ProtocolNotSupported = "na";
    public string Id => "/multistream/1.0.0";

    public MultistreamProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<MultistreamProtocol>();
    }
    public async Task DialAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context)
    {
        if (!await SendHello(channel))
        {
            await channel.CloseAsync();
            return;
        }

        async Task<bool?> DialProtocol(IProtocol selector)
        {
            await channel.WriteLineAsync(selector.Id);
            string selectorLine = await channel.ReadLineAsync();
            _logger?.LogDebug($"Sent {selector.Id}, recv {selectorLine}");
            if (selectorLine == selector.Id)
            {
                return true;
            }

            if (selectorLine != ProtocolNotSupported)
            {
                return false;
            }

            return null;
        }

        IProtocol? selected = null;

        if (context.SpecificProtocolRequest?.SubProtocol is not null)
        {
            _logger?.LogDebug($"DIAL FOR SPECIFIC PROTOCOL {context.SpecificProtocolRequest.SubProtocol}");
            if (await DialProtocol(context.SpecificProtocolRequest.SubProtocol) == true)
            {
                selected = context.SpecificProtocolRequest.SubProtocol;
            }
            context.SpecificProtocolRequest = null;
        }
        else
        {
            foreach (IProtocol selector in channelFactory.SubProtocols)
            {
                bool? dialResult = await DialProtocol(selector);
                if (dialResult == true)
                {
                    selected = selector;
                    break;
                }
                else if (dialResult == false)
                {
                    break;
                }
            }
        }

        if (selected is null)
        {
            _logger?.LogDebug($"DIAL NEG FAILED {string.Join(", ", channelFactory.SubProtocols)}");
            return;
        }
        _logger?.LogDebug($"DIAL NEG SUCCEED {string.Join(", ", channelFactory.SubProtocols)} -> {selected}");
        await channelFactory.SubDialAndBind(channel, context, selected);
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context)
    {
        if (!await SendHello(channel))
        {
            await channel.CloseAsync();
            return;
        }

        IProtocol? selected = null;
        while (!channel.IsClosed)
        {
            string proto = await channel.ReadLineAsync();
            selected = channelFactory.SubProtocols.FirstOrDefault(x => x.Id == proto);
            if (selected is not null)
            {
                await channel.WriteLineAsync(selected.Id);
                _logger?.LogDebug($"Recv {proto}, sent {selected?.Id}");
                break;
            }

            _logger?.LogDebug($"Recv {proto}, sent {ProtocolNotSupported}");
            await channel.WriteLineAsync(ProtocolNotSupported);
        }

        if (selected is null)
        {
            _logger?.LogDebug($"LIST NEG FAILED {string.Join(", ", channelFactory.SubProtocols)}");
            return;
        }

        _logger?.LogDebug($"LIST NEG SUCCEED {string.Join(", ", channelFactory.SubProtocols)} -> {selected}");
        await channelFactory.SubListenAndBind(channel, context, selected);
    }

    private async Task<bool> SendHello(IChannel channel)
    {
        await channel.WriteLineAsync(Id);
        string line = await channel.ReadLineAsync();
        return line == Id;
    }
}
