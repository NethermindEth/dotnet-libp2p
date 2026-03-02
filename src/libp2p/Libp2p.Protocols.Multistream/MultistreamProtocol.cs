// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/multiformats/multistream-select
/// </summary>
public class MultistreamProtocol(ILoggerFactory? loggerFactory = null) : IConnectionProtocol
{
    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MultistreamProtocol>();
    private const string ProtocolNotSupported = "na";
    public string Id => "/multistream/1.0.0";

    public async Task DialAsync(IChannel channel, IConnectionContext context)
    {
        _logger?.LogTrace($"Hello started");

        if (!await SendHello(channel))
        {
            await channel.CloseAsync();
            _logger?.LogTrace($"Hello failed");
            return;
        }
        _logger?.LogTrace($"Hello passed");

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

        if (context.UpgradeOptions?.SelectedProtocol is not null)
        {
            _logger?.LogDebug($"Proposing just {context.UpgradeOptions.SelectedProtocol}");
            if (await DialProtocol(context.UpgradeOptions.SelectedProtocol) == true)
            {
                selected = context.UpgradeOptions.SelectedProtocol;
            }
        }
        else
        {
            foreach (IProtocol selector in context!.SubProtocols)
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
        _logger?.LogDebug($"Protocol selected during dialing: {selected.Id}");
        await context.Upgrade(channel, selected);
    }

    public async Task ListenAsync(IChannel channel, IConnectionContext context)
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
            selected = context.SubProtocols.FirstOrDefault(x => x.Id == proto) as IProtocol;
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
        await context.Upgrade(channel, selected);
    }

    private async Task<bool> SendHello(IChannel channel)
    {
        await channel.WriteLineAsync(Id);
        string line = await channel.ReadLineAsync();
        return line == Id;
    }
}