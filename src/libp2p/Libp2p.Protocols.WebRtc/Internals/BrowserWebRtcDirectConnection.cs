// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.JSInterop;
using Multiformats.Address;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class BrowserWebRtcDirectConnection : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly DotNetObjectReference<BrowserWebRtcDirectConnection> _dotNetRef;
    private readonly ConcurrentDictionary<string, BrowserWebRtcDataChannel> _inboundChannelsById = [];
    private readonly ConcurrentDictionary<string, byte> _publishedInboundChannels = [];
    private readonly Channel<BrowserWebRtcDataChannel> _inboundChannels = System.Threading.Channels.Channel.CreateUnbounded<BrowserWebRtcDataChannel>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private string? _connectionId;

    public BrowserWebRtcDirectConnection(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
        _dotNetRef = DotNetObjectReference.Create(this);
        HandshakeChannel = new BrowserWebRtcDataChannel(jsRuntime);
    }

    public BrowserWebRtcDataChannel HandshakeChannel { get; }
    public IAsyncEnumerable<BrowserWebRtcDataChannel> InboundChannels => _inboundChannels.Reader.ReadAllAsync();

    public async Task<BrowserWebRtcDirectConnectResult> ConnectAsync(Multiaddress remoteAddr, CancellationToken token)
    {
        BrowserWebRtcDirectConnectResult result = await _jsRuntime.InvokeAsync<BrowserWebRtcDirectConnectResult>(
            "nethermindLibp2pWebRtcDirect.dial",
            token,
            HandshakeChannel.DotNetRef,
            _dotNetRef,
            remoteAddr.ToString());

        _connectionId = result.ConnectionId;
        HandshakeChannel.SetChannelId(result.HandshakeChannelId);
        await HandshakeChannel.Opened.WaitAsync(token);
        return result;
    }

    public async Task<BrowserWebRtcDataChannel> OpenStreamAsync(CancellationToken token)
    {
        if (_connectionId is null)
        {
            throw new InvalidOperationException("Browser WebRTC-Direct connection has not been established.");
        }

        BrowserWebRtcDataChannel channel = new(_jsRuntime);
        string channelId = await _jsRuntime.InvokeAsync<string>(
            "nethermindLibp2pWebRtcDirect.openStream",
            token,
            _connectionId,
            channel.DotNetRef);
        channel.SetChannelId(channelId);
        await channel.Opened.WaitAsync(token);
        return channel;
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

        await HandshakeChannel.DisposeAsync();

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

internal sealed class BrowserWebRtcDirectConnectResult
{
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("handshakeChannelId")]
    public string HandshakeChannelId { get; set; } = string.Empty;

    [JsonPropertyName("localFingerprint")]
    public string LocalFingerprint { get; set; } = string.Empty;
}
