// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace PubsubChat;

public class ChatService
{
    private readonly ServiceProvider _services;
    private readonly ILocalPeer _peer;
    private readonly ITopic _topic;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _messages = new();
    private readonly List<string> _logs = new();
    private readonly ConcurrentDictionary<string, ConnectedPeer> _connectedPeers = new();
    private readonly ILogger<ChatService> _logger;
    private readonly PubsubRouter? _pubsubRouter;

    public IReadOnlyList<string> Messages => _messages.AsReadOnly();
    public IReadOnlyList<string> Logs => _logs.AsReadOnly();
    public IReadOnlyDictionary<string, ConnectedPeer> ConnectedPeers => _connectedPeers;

    public string LocalPeerId => _peer.Identity.PeerId.ToString();

    public class ConnectedPeer
    {
        public string PeerId { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public DateTime ConnectedAt { get; set; }
        public string UserAgent { get; set; } = string.Empty;
    }

    public ChatService(ServiceProvider services)
    {
        _services = services;

        var peerFactory = _services.GetService<IPeerFactory>()!;
        var localPeerIdentity = new Identity();
        _peer = peerFactory.Create(localPeerIdentity);

        var router = _services.GetService<PubsubRouter>()!;
        _topic = router.GetTopic("chat-room:awesome-chat-room");

        _topic.OnMessage += OnMessageReceived;

        var loggerFactory = _services.GetService<ILoggerFactory>()!;
        _logger = loggerFactory.CreateLogger<ChatService>();

        loggerFactory.AddProvider(new InMemoryLogProvider(_logs));

        _pubsubRouter = router;

        AddLog($"Local peer ID: {LocalPeerId}");
    }

    private void UpdateConnectedPeers()
    {
        if (_pubsubRouter != null)
        {
            var topicPeers = new List<PeerId>();

            foreach (var peerId in topicPeers)
            {
                string peerIdStr = peerId.ToString();
                if (!_connectedPeers.ContainsKey(peerIdStr))
                {
                    // This is a new peer
                    _connectedPeers[peerIdStr] = new ConnectedPeer
                    {
                        PeerId = peerIdStr,
                        Address = "Unknown", // We don't have address info from just the topic
                        ConnectedAt = DateTime.Now,
                        UserAgent = "Unknown"
                    };

                    AddLog($"Connected to peer: {peerIdStr}");
                }
            }

            // Check for disconnected peers
            var disconnectedPeers = new List<string>();
            foreach (var peer in _connectedPeers)
            {
                if (!topicPeers.Contains(new PeerId(peer.Key)))
                {
                    disconnectedPeers.Add(peer.Key);
                }
            }

            foreach (var peerId in disconnectedPeers)
            {
                if (_connectedPeers.TryRemove(peerId, out var peer))
                {
                    AddLog($"Disconnected from peer: {peerId}");
                }
            }
        }
    }

    private void AddLog(string message)
    {
        string timestamp = DateTime.Now.ToString("[HH:mm:ss.fff]");
        lock (_logs)
        {
            _logs.Add($"{timestamp} {message}");
            // Keep logs limited to avoid excessive memory usage
            if (_logs.Count > 1000)
            {
                _logs.RemoveAt(0);
            }
        }
    }

    public async Task StartAsync()
    {
        string addr = $"/ip4/0.0.0.0/tcp/0/p2p/{_peer.Identity.PeerId}";

        var listenAddresses = new[] { Multiformats.Address.Multiaddress.Decode(addr) };
        await _peer.StartListenAsync(listenAddresses, _cts.Token);
        _ = _services.GetService<MDnsDiscoveryProtocol>()!.StartDiscoveryAsync(_peer.ListenAddresses, token: _cts.Token);
        await _services.GetService<PubsubRouter>()!.StartAsync(_peer, token: _cts.Token);

        // Start a background task to periodically check for peer updates
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                UpdateConnectedPeers();
                await Task.Delay(3000, _cts.Token); // Check every 3 seconds
            }
        }, _cts.Token);

        AddLog($"Started listening on {string.Join(", ", _peer.ListenAddresses)}");
    }

    private void OnMessageReceived(byte[] msg)
    {
        try
        {
            var chatMsg = JsonSerializer.Deserialize<global::ChatMessage>(Encoding.UTF8.GetString(msg));
            if (chatMsg is not null)
            {
                lock (_messages)
                {
                    _messages.Add($"{chatMsg.SenderNick}: {chatMsg.Message}");
                }
            }
        }
        catch
        {
            lock (_messages)
            {
                _messages.Add("[!] Failed to decode chat message");
            }
        }
    }

    public void Publish(string message, string nickName)
    {
        var chatMsg = new ChatMessage(message, _peer.Identity.PeerId.ToString(), nickName);
        _topic.Publish(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(chatMsg)));
    }

    public void Stop()
    {
        _cts.Cancel();
    }
}
