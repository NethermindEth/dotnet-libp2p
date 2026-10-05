// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class BrowserWebRtcRelayConnection : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly DotNetObjectReference<BrowserWebRtcRelayConnection> _dotNetRef;
    private readonly ConcurrentDictionary<string, BrowserWebRtcDataChannel> _inboundChannelsById = [];
    private readonly ConcurrentDictionary<string, byte> _publishedInboundChannels = [];
    private readonly Channel<BrowserWebRtcDataChannel> _inboundChannels = Channel.CreateUnbounded<BrowserWebRtcDataChannel>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Channel<string> _localIceCandidates = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private string? _connectionId;

    public BrowserWebRtcRelayConnection(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
        _dotNetRef = DotNetObjectReference.Create(this);
    }

    public IAsyncEnumerable<BrowserWebRtcDataChannel> InboundChannels => _inboundChannels.Reader.ReadAllAsync();
    public IAsyncEnumerable<string> LocalIceCandidates => _localIceCandidates.Reader.ReadAllAsync();

    public async Task<string> CreateOfferAsync(CancellationToken token)
    {
        BrowserWebRtcRelayOfferResult result = await _jsRuntime.InvokeAsync<BrowserWebRtcRelayOfferResult>(
            "nethermindLibp2pWebRtcDirect.createRelayedOffer",
            token,
            _dotNetRef);
        _connectionId = result.ConnectionId;
        return result.Sdp;
    }

    public async Task CreateAnswererAsync(CancellationToken token)
    {
        _connectionId = await _jsRuntime.InvokeAsync<string>(
            "nethermindLibp2pWebRtcDirect.createRelayedAnswerer",
            token,
            _dotNetRef);
    }

    public ValueTask<string> AcceptOfferAsync(string offerSdp, CancellationToken token)
        => _jsRuntime.InvokeAsync<string>(
            "nethermindLibp2pWebRtcDirect.acceptRelayedOffer",
            token,
            RequiredConnectionId,
            offerSdp);

    public ValueTask SetRemoteAnswerAsync(string answerSdp, CancellationToken token)
        => _jsRuntime.InvokeVoidAsync(
            "nethermindLibp2pWebRtcDirect.setRelayedAnswer",
            token,
            RequiredConnectionId,
            answerSdp);

    public ValueTask AddIceCandidateAsync(string candidateJson, CancellationToken token)
        => _jsRuntime.InvokeVoidAsync(
            "nethermindLibp2pWebRtcDirect.addRelayedIceCandidate",
            token,
            RequiredConnectionId,
            candidateJson);

    public ValueTask WaitConnectedAsync(CancellationToken token)
        => _jsRuntime.InvokeVoidAsync(
            "nethermindLibp2pWebRtcDirect.waitRelayedConnected",
            token,
            RequiredConnectionId);

    public ValueTask CloseInitChannelAsync(CancellationToken token)
        => _jsRuntime.InvokeVoidAsync(
            "nethermindLibp2pWebRtcDirect.closeRelayedInitChannel",
            token,
            RequiredConnectionId);

    public async Task<BrowserWebRtcDataChannel> OpenStreamAsync(CancellationToken token)
    {
        BrowserWebRtcDataChannel channel = new(_jsRuntime);
        string channelId = await _jsRuntime.InvokeAsync<string>(
            "nethermindLibp2pWebRtcDirect.openStream",
            token,
            RequiredConnectionId,
            channel.DotNetRef);
        channel.SetChannelId(channelId);
        await channel.Opened.WaitAsync(token);
        return channel;
    }

    [JSInvokable]
    public Task LocalIceCandidate(string candidateJson)
    {
        if (!string.IsNullOrWhiteSpace(candidateJson))
        {
            _localIceCandidates.Writer.TryWrite(candidateJson);
        }

        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task InboundChannelOpened(string channelId)
    {
        PublishInboundChannel(GetOrAddInboundChannel(channelId));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task InboundChannelMessage(string channelId, byte[] data)
    {
        BrowserWebRtcDataChannel channel = GetOrAddInboundChannel(channelId);
        PublishInboundChannel(channel);
        return channel.ReceiveMessage(data);
    }

    [JSInvokable]
    public async Task InboundChannelClosed(string channelId, string? reason)
    {
        _publishedInboundChannels.TryRemove(channelId, out _);
        if (_inboundChannelsById.TryRemove(channelId, out BrowserWebRtcDataChannel? channel))
        {
            await channel.ClosedFromJs(reason);
            await channel.DisposeAsync();
        }
    }

    private string RequiredConnectionId => _connectionId ?? throw new InvalidOperationException("Browser WebRTC relay connection has not been initialized.");

    private BrowserWebRtcDataChannel GetOrAddInboundChannel(string channelId) =>
        _inboundChannelsById.GetOrAdd(channelId, id =>
        {
            BrowserWebRtcDataChannel inbound = new(_jsRuntime);
            inbound.SetChannelId(id);
            return inbound;
        });

    private void PublishInboundChannel(BrowserWebRtcDataChannel channel)
    {
        if (_publishedInboundChannels.TryAdd(channel.ChannelId, 0))
        {
            _ = channel.OpenedFromJs();
            _inboundChannels.Writer.TryWrite(channel);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _inboundChannels.Writer.TryComplete();
        _localIceCandidates.Writer.TryComplete();

        foreach (BrowserWebRtcDataChannel channel in _inboundChannelsById.Values)
        {
            await channel.DisposeAsync();
        }

        _dotNetRef.Dispose();

        if (_connectionId is not null)
        {
            try
            {
                await _jsRuntime.InvokeVoidAsync("nethermindLibp2pWebRtcDirect.closeConnection", _connectionId);
            }
            catch (JSException)
            {
            }
        }
    }
}

internal sealed class BrowserWebRtcRelayOfferResult
{
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("sdp")]
    public string Sdp { get; set; } = string.Empty;
}
