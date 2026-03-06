// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;

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
        _logger?.LogTrace("Hello started");

        if (!await SendHello(channel))
        {
            await channel.CloseAsync();
            _logger?.LogTrace("Hello failed");
            return;
        }
        _logger?.LogTrace("Hello passed");

        async Task<bool?> DialProtocol(IProtocol selector)
        {
            try
            {
                _logger?.LogTrace("DialProtocol: proposing {Id}", selector.Id);
                await channel.WriteLineAsync(selector.Id);
                string selectorLine = await channel.ReadLineAsync();
                _logger?.LogTrace("Proposed {Id}, answer: {Answer}", selector.Id, selectorLine ?? "(null)");
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
            catch (ChannelClosedException)
            {
                _logger?.LogWarning("Channel closed during protocol negotiation (dial, selector {Id}).", selector.Id);
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Protocol negotiation canceled (dial, selector {Id}).", selector.Id);
                return false;
            }
        }

        IProtocol? selected = null;

        IProtocol? selectedProtocol = context.UpgradeOptions?.SelectedProtocol;
        if (selectedProtocol is not null)
        {
            _logger?.LogDebug("Proposing just {Protocol}", selectedProtocol.Id);
            if (await DialProtocol(selectedProtocol) == true)
            {
                selected = selectedProtocol;
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
            _logger?.LogDebug("Negotiation failed");
            return;
        }
        _logger?.LogDebug("Protocol selected during dialing: {Id}", selected.Id);
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
        try
        {
            for (; ; )
            {
                _logger?.LogTrace("Listen: waiting for remote protocol proposal");
                string proto = await channel.ReadLineAsync();
                _logger?.LogTrace("Listen: proposed by remote {Proto}", proto ?? "(null)");
                selected = context.SubProtocols.FirstOrDefault(x => x.Id == proto) as IProtocol;
                if (selected is not null)
                {
                    await channel.WriteLineAsync(selected.Id);
                    _logger?.LogTrace("Proposed by remote {Proto}, answer: {Selected}", proto, selected.Id);
                    break;
                }

                _logger?.LogTrace("Proposed by remote {Proto}, answer: {Na}", proto, ProtocolNotSupported);
                await channel.WriteLineAsync(ProtocolNotSupported);
            }
        }
        catch (ChannelClosedException)
        {
            _logger?.LogWarning("Channel closed during multistream protocol negotiation (listen).");
            return;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Multistream protocol negotiation canceled (listen).");
            return;
        }

        if (selected is null)
        {
            _logger?.LogDebug("Negotiation failed");
            return;
        }

        _logger?.LogDebug("Protocol selected during listening: {Selected}", selected.Id);
        await context.Upgrade(channel, selected);
    }

    private async Task<bool> SendHello(IChannel channel)
    {
        try
        {
            _logger?.LogTrace("SendHello: sending multistream id");
            await channel.WriteLineAsync(Id);
            string line = await channel.ReadLineAsync();
            _logger?.LogTrace("SendHello: received {Line}", line ?? "(null)");
            return line == Id;
        }
        catch (ChannelClosedException)
        {
            _logger?.LogWarning("Channel closed during multistream hello.");
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Multistream hello canceled.");
            return false;
        }
    }
}
