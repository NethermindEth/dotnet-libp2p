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
    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory,
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
            _logger?.LogTrace($"Proposed {selector.Id}, answer: {selectorLine}");
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
            selected = context.SpecificProtocolRequest.SubProtocol;

            context.SpecificProtocolRequest = null;
            if (await DialProtocol(selected) != true)
            {
                return;
            }
        }
        else
        {
            foreach (IProtocol selector in channelFactory!.SubProtocols)
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
            _logger?.LogDebug($"Negotiation failed");
            return;
        }
        _logger?.LogDebug($"Protocol selected during dialing: {selected}");
        await channelFactory.SubDialAndBind(channel, context, selected);
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        if (!await SendHello(channel))
        {
            await channel.CloseAsync();
            return;
        }

        IProtocol? selected = null;
        for (; ; )
        {
            string proto = await channel.ReadLineAsync();
            selected = channelFactory!.SubProtocols.FirstOrDefault(x => x.Id == proto);
            if (selected is not null)
            {
                await channel.WriteLineAsync(selected.Id);
                _logger?.LogTrace($"Proposed by remote {proto}, answer: {selected?.Id}");
                break;
            }

            _logger?.LogTrace($"Proposed by remote {proto}, answer: {ProtocolNotSupported}");
            await channel.WriteLineAsync(ProtocolNotSupported);
        }

        if (selected is null)
        {
            _logger?.LogDebug($"Negotiation failed");
            return;
        }

        _logger?.LogDebug($"Protocol selected during listening: {selected}");
        await channelFactory.SubListenAndBind(channel, context, selected);
    }

    private async Task<bool> SendHello(IChannel channel)
    {
        await channel.WriteLineAsync(Id);
        string line = await channel.ReadLineAsync();
        return line == Id;
    }
}
