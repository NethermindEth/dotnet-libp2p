// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/multiformats/multistream-select
/// </summary>
public class MultistreamProtocol(ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null) : IConnectionProtocol
{
    public MultistreamProtocol(ILoggerFactory? loggerFactory) : this(loggerFactory, null) { }

    private Deadline StartDeadline(IChannel channel, IConnectionContext context)
        => new(channel, context, timeProvider ?? context.Clock ?? TimeProvider.System);

    private sealed class Deadline : IDisposable
    {
        private readonly CancellationTokenSource _timeout;
        private readonly CancellationTokenSource _linked;
        private readonly CancellationTokenRegistration _abort;
        public CancellationToken Token { get; }
        private int _disposed;
        public Deadline(IChannel channel, IConnectionContext context, TimeProvider clock)
        {
            _timeout = new((context.ResourceLimits ?? PeerResourceLimits.Default).NegotiationTimeout, clock);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token, context.Token, context.UpgradeOptions?.CancellationToken ?? default);
            Token = _linked.Token;
            _abort = Token.Register(() => _ = channel.AbortAsync());
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _abort.Dispose();
            _linked.Dispose();
            _timeout.Dispose();
        }
    }

    private readonly ILogger? _logger = loggerFactory?.CreateLogger<MultistreamProtocol>();
    private const string ProtocolNotSupported = "na";
    public string Id => "/multistream/1.0.0";

    public async Task DialAsync(IChannel channel, IConnectionContext context)
    {
        using Deadline deadline = StartDeadline(channel, context);
        CancellationToken token = deadline.Token;
        _logger?.LogTrace("Hello started");

        IProtocol? selectedProtocol = context.UpgradeOptions?.SelectedProtocol;
        if (selectedProtocol is not null)
        {
            if (!await DialSelectedProtocolOptimistically(channel, selectedProtocol, token))
            {
                _logger?.LogDebug("Negotiation failed");
                context.UpgradeOptions?.CompletionSource?.TrySetException(new ChannelClosedException());
                return;
            }

            _logger?.LogDebug("Protocol selected during dialing: {Id}", selectedProtocol.Id);
            token.ThrowIfCancellationRequested();
            deadline.Dispose();
            await context.Upgrade(channel, selectedProtocol);
            return;
        }

        if (!await SendHello(channel, token))
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
                await channel.WriteLineAsync(selector.Id, token);
                string selectorLine = await channel.ReadLineAsync(token);
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

        if (selected is null)
        {
            _logger?.LogDebug("Negotiation failed");
            return;
        }
        _logger?.LogDebug("Protocol selected during dialing: {Id}", selected.Id);
        token.ThrowIfCancellationRequested();
        deadline.Dispose();
        await context.Upgrade(channel, selected);
    }

    public async Task ListenAsync(IChannel channel, IConnectionContext context)
    {
        using Deadline deadline = StartDeadline(channel, context);
        CancellationToken token = deadline.Token;
        if (!await SendHello(channel, token))
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
                string proto = await channel.ReadLineAsync(token);
                _logger?.LogTrace("Listen: proposed by remote {Proto}", proto ?? "(null)");
                selected = context.SubProtocols.FirstOrDefault(x => x.Id == proto) as IProtocol;
                if (selected is not null)
                {
                    await channel.WriteLineAsync(selected.Id, token);
                    _logger?.LogTrace("Proposed by remote {Proto}, answer: {Selected}", proto, selected.Id);
                    break;
                }

                _logger?.LogTrace("Proposed by remote {Proto}, answer: {Na}", proto, ProtocolNotSupported);
                await channel.WriteLineAsync(ProtocolNotSupported, token);
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
        token.ThrowIfCancellationRequested();
        deadline.Dispose();
        await context.Upgrade(channel, selected);
    }

    private async Task<bool> SendHello(IChannel channel, CancellationToken token)
    {
        try
        {
            _logger?.LogTrace("SendHello: sending multistream id");
            await channel.WriteLineAsync(Id, token);
            string line = await channel.ReadLineAsync(token);
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

    private async Task<bool> DialSelectedProtocolOptimistically(IChannel channel, IProtocol selectedProtocol, CancellationToken token)
    {
        try
        {
            _logger?.LogTrace("SendHello: sending multistream id");
            await channel.WriteLineAsync(Id, token);
            _logger?.LogTrace("DialProtocol: optimistically proposing {Id}", selectedProtocol.Id);
            await channel.WriteLineAsync(selectedProtocol.Id, token);

            string line = await channel.ReadLineAsync(token);
            _logger?.LogTrace("SendHello: received {Line}", line ?? "(null)");
            if (line != Id)
            {
                return false;
            }

            string selectorLine = await channel.ReadLineAsync(token);
            _logger?.LogTrace("Proposed {Id}, answer: {Answer}", selectedProtocol.Id, selectorLine ?? "(null)");
            return selectorLine == selectedProtocol.Id;
        }
        catch (ChannelClosedException)
        {
            _logger?.LogWarning("Channel closed during optimistic protocol negotiation (dial, selector {Id}).", selectedProtocol.Id);
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("Optimistic protocol negotiation canceled (dial, selector {Id}).", selectedProtocol.Id);
            return false;
        }
    }
}
