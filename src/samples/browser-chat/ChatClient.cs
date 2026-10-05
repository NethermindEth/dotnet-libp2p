// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Relay.Dto;

namespace BrowserChat;

public sealed class ChatClient : IAsyncDisposable
{
    private readonly IPeerFactory _peerFactory;

    private ILocalPeer? _localPeer;
    private ISession? _session;
    private ISession? _relaySession;

    public ChatClient(IPeerFactory peerFactory)
    {
        _peerFactory = peerFactory;
    }

    public bool IsConnected => _session is not null;
    public bool IsListening => _relaySession is not null;
    public string? RemoteAddress => _session?.RemoteAddress.ToString();
    public string? ListenAddress { get; private set; }

    public async Task ConnectAsync(string remoteAddress, CancellationToken token)
    {
        await DisposePeerAsync();

        _localPeer = _peerFactory.Create();
        _session = await _localPeer.DialAsync(Multiaddress.Decode(remoteAddress), token);
    }

    public async Task<string> ListenThroughRelayAsync(string relayAddress, CancellationToken token)
    {
        await DisposePeerAsync();

        Multiaddress relayMultiaddr = Multiaddress.Decode(relayAddress);
        _localPeer = _peerFactory.Create();
        _relaySession = await _localPeer.DialAsync(relayMultiaddr, token);

        HopMessage reserveResponse = await _relaySession.DialAsync<RelayHopProtocol, HopMessage, HopMessage>(new HopMessage
        {
            Type = HopMessage.Types.Type.Reserve
        }, token);

        if (reserveResponse.Status != Status.Ok)
        {
            throw new Libp2pException($"Relay reservation failed with status {reserveResponse.Status}.");
        }

        Multiaddress listenMultiaddr = Multiaddress.Decode($"{relayMultiaddr}/p2p-circuit/webrtc/p2p/{_localPeer.Identity.PeerId}");
        _localPeer.ListenAddresses.Add(listenMultiaddr);
        ListenAddress = listenMultiaddr.ToString();
        return ListenAddress;
    }

    public async Task<string> SendAsync(string message, CancellationToken token)
    {
        if (_session is null)
        {
            throw new InvalidOperationException("Connect before sending a chat message.");
        }

        return await _session.DialAsync<BrowserChatProtocol, string, string>(message, token);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposePeerAsync();
    }

    private async Task DisposePeerAsync()
    {
        if (_session is not null)
        {
            await _session.DisconnectAsync();
            _session = null;
        }

        if (_relaySession is not null)
        {
            await _relaySession.DisconnectAsync();
            _relaySession = null;
        }

        if (_localPeer is not null)
        {
            await _localPeer.DisposeAsync();
            _localPeer = null;
        }

        ListenAddress = null;
    }
}
