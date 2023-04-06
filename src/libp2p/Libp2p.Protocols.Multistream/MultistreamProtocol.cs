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

        IProtocol? selected = null;
        foreach (IProtocol selector in channelFactory.SubProtocols)
        {
            await channel.WriteLineAsync(selector.Id);
            string selectorLine = await channel.ReadLineAsync();
            if (selectorLine == selector.Id)
            {
                selected = selector;
                break;
            }

            if (selectorLine != ProtocolNotSupported)
            {
                break;
            }
        }

        if (selected is null)
        {
            _logger?.LogTrace($"DIAL NEG FAILED {string.Join(", ", channelFactory.SubProtocols)}");
            return;
        }
        _logger?.LogTrace($"DIAL NEG SUCCEED {string.Join(", ", channelFactory.SubProtocols)} -> {selected}");
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
                break;
            }

            await channel.WriteLineAsync(ProtocolNotSupported);
        }

        if (selected is null)
        {
            _logger?.LogTrace($"LIST NEG FAILED {string.Join(", ", channelFactory.SubProtocols)}");
            return;
        }

        _logger?.LogTrace($"LIST NEG SUCCEED {string.Join(", ", channelFactory.SubProtocols)} -> {selected}");
        await channelFactory.SubListenAndBind(channel, context, selected);
    }

    private async Task<bool> SendHello(IChannel channel)
    {
        await channel.WriteLineAsync(Id);
        string line = await channel.ReadLineAsync();
        return line == Id;
    }
}
