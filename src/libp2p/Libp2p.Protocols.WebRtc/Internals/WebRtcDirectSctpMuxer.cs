// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using SIPSorcery.Net;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class WebRtcDirectSctpMuxer : IAsyncDisposable
{
    private readonly RTCSctpTransport _transport;
    private readonly ConcurrentDictionary<ushort, SctpWebRtcDataChannel> _channels = new();
    private readonly Channel<SctpWebRtcDataChannel> _inboundChannels = Channel.CreateUnbounded<SctpWebRtcDataChannel>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _streamIdLock = new();
    private ushort _nextOutboundStreamId;
    private int _disposed;

    public WebRtcDirectSctpMuxer(RTCSctpTransport transport, bool isDtlsClient)
    {
        _transport = transport;
        _nextOutboundStreamId = isDtlsClient ? (ushort)2 : (ushort)1;

        _transport.OnStateChanged += OnSctpStateChanged;
        _transport.RTCSctpAssociation.OnDataChannelData += OnDataChannelData;
        _transport.RTCSctpAssociation.OnDataChannelOpened += OnDataChannelOpened;
        _transport.RTCSctpAssociation.OnNewDataChannel += OnNewDataChannel;
    }

    public IAsyncEnumerable<SctpWebRtcDataChannel> InboundChannels => _inboundChannels.Reader.ReadAllAsync();

    public Task Connected => _connected.Task;

    public Task Completed => _completed.Task;

    public SctpWebRtcDataChannel GetNegotiatedChannel(ushort streamId)
    {
        SctpWebRtcDataChannel channel = _channels.GetOrAdd(
            streamId,
            id => new SctpWebRtcDataChannel(_transport.RTCSctpAssociation, id));

        if (_transport.state == RTCSctpTransportState.Connected)
        {
            channel.MarkOpen();
        }

        return channel;
    }

    public async Task<SctpWebRtcDataChannel> OpenStreamAsync(CancellationToken token)
    {
        await _connected.Task.WaitAsync(token);

        ushort streamId = NextOutboundStreamId();
        SctpWebRtcDataChannel channel = _channels.GetOrAdd(
            streamId,
            id => new SctpWebRtcDataChannel(_transport.RTCSctpAssociation, id));

        DataChannelOpenMessage open = new()
        {
            MessageType = (byte)DataChannelMessageTypes.OPEN,
            ChannelType = (byte)DataChannelTypes.DATA_CHANNEL_RELIABLE,
            Label = string.Empty,
            Protocol = string.Empty,
        };

        _transport.RTCSctpAssociation.SendData(
            streamId,
            (uint)DataChannelPayloadProtocols.WebRTC_DCEP,
            open.GetBytes());

        await channel.Opened.WaitAsync(token);
        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _inboundChannels.Writer.TryComplete();
        _completed.TrySetResult();

        try
        {
            _transport.Close();
        }
        catch
        {
        }

        foreach (SctpWebRtcDataChannel channel in _channels.Values)
        {
            await channel.CloseAsync();
        }
    }

    private ushort NextOutboundStreamId()
    {
        lock (_streamIdLock)
        {
            ushort streamId = _nextOutboundStreamId;
            _nextOutboundStreamId += 2;
            if (_nextOutboundStreamId == 0)
            {
                _nextOutboundStreamId = 1;
            }

            return streamId;
        }
    }

    private void OnSctpStateChanged(RTCSctpTransportState state)
    {
        if (state == RTCSctpTransportState.Connected)
        {
            _connected.TrySetResult();
            foreach (SctpWebRtcDataChannel channel in _channels.Values)
            {
                if (channel.StreamId == 0)
                {
                    channel.MarkOpen();
                }
            }
        }
        else if (state == RTCSctpTransportState.Closed)
        {
            _connected.TrySetException(new InvalidOperationException("SCTP transport closed before connecting."));
            _inboundChannels.Writer.TryComplete();
            _completed.TrySetResult();
            foreach (SctpWebRtcDataChannel channel in _channels.Values)
            {
                _ = channel.CloseAsync();
            }
        }
    }

    private void OnDataChannelData(SctpDataFrame frame)
    {
        if (_channels.TryGetValue(frame.StreamID, out SctpWebRtcDataChannel? channel))
        {
            channel.Receive(frame);
        }
    }

    private void OnDataChannelOpened(ushort streamId)
    {
        if (_channels.TryGetValue(streamId, out SctpWebRtcDataChannel? channel))
        {
            channel.MarkOpen();
        }
    }

    private void OnNewDataChannel(
        ushort streamId,
        DataChannelTypes type,
        ushort priority,
        uint reliability,
        string label,
        string protocol)
    {
        SctpWebRtcDataChannel channel = _channels.GetOrAdd(
            streamId,
            id => new SctpWebRtcDataChannel(_transport.RTCSctpAssociation, id));

        _transport.RTCSctpAssociation.SendData(
            streamId,
            (uint)DataChannelPayloadProtocols.WebRTC_DCEP,
            [(byte)DataChannelMessageTypes.ACK]);

        channel.MarkOpen();
        _inboundChannels.Writer.TryWrite(channel);
    }
}
