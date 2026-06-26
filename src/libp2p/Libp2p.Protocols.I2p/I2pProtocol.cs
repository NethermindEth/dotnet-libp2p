// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.I2p;

namespace Nethermind.Libp2p.Protocols;

public sealed class I2pProtocol : ITransportProtocol, IPeerScopedProtocol, IAsyncDisposable
{
    private static readonly string DefaultListenDestination = new Garlic64(new byte[387]).Destination;
    private readonly I2pOptions _options;
    private readonly ConcurrentDictionary<PeerId, I2pSamClient> _samClients = new();
    private readonly ILogger<I2pProtocol>? _logger;
    private int _disposed;

    public I2pProtocol(I2pOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        I2pMultiaddr.Register();
        _options = CreateTransportOptions(options);
        _logger = loggerFactory?.CreateLogger<I2pProtocol>();
    }

    public string Id => "i2p";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId)
    {
        I2pMultiaddr.Register();
        return [I2pMultiaddr.FromGarlic64(DefaultListenDestination, peerId)];
    }

    public static bool IsAddressMatch(Multiaddress addr)
    {
        return I2pMultiaddr.IsI2pAddress(addr);
    }

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        I2pSamClient samClient = GetSamClient(context.Peer.Identity.PeerId);
        string destination = await samClient.CreateSessionAsync(token).ConfigureAwait(false);
        Multiaddress advertisedAddress = I2pMultiaddr.FromGarlic64(destination, context.Peer.Identity.PeerId);
        context.ListenerReady(advertisedAddress);
        _logger?.LogDebug("I2P listener ready at {Address}", advertisedAddress);

        while (!token.IsCancellationRequested)
        {
            I2pSamStream accepted = await samClient.AcceptStreamAsync(token).ConfigureAwait(false);
            INewConnectionContext connectionContext = context.CreateConnection();
            connectionContext.State.LocalAddress = advertisedAddress;
            connectionContext.State.RemoteAddress = string.IsNullOrWhiteSpace(accepted.RemoteDestination)
                ? listenAddr
                : I2pMultiaddr.FromGarlic64(accepted.RemoteDestination);
            IChannel upChannel = connectionContext.Upgrade();
            _ = PumpAsync(accepted.Stream, upChannel, connectionContext, token);
        }
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        I2pSamClient samClient = GetSamClient(context.Peer.Identity.PeerId);
        string destination = I2pMultiaddr.GetDestination(remoteAddr);
        NetworkStream stream = await samClient.ConnectStreamAsync(destination, token).ConfigureAwait(false);

        INewConnectionContext connectionContext = context.CreateConnection();
        connectionContext.State.RemoteAddress = remoteAddr;
        IChannel upChannel = connectionContext.Upgrade();
        await PumpAsync(stream, upChannel, connectionContext, token).ConfigureAwait(false);
    }

    private I2pSamClient GetSamClient(PeerId peerId)
    {
        ThrowIfDisposed();
        I2pSamClient client = _samClients.GetOrAdd(peerId, id =>
        {
            ThrowIfDisposed();
            return new I2pSamClient(CreatePeerOptions(id));
        });

        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_samClients.TryRemove(peerId, out I2pSamClient? removed))
            {
                DisposeClientSynchronously(removed);
            }

            ThrowIfDisposed();
        }

        return client;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static void DisposeClientSynchronously(I2pSamClient client)
    {
        ValueTask disposeTask = client.DisposeAsync();
        if (!disposeTask.IsCompletedSuccessfully)
        {
            disposeTask.AsTask().GetAwaiter().GetResult();
        }
    }

    private static I2pOptions CreateTransportOptions(I2pOptions? options)
    {
        if (options is null)
        {
            return new I2pOptions { UsePrimarySessionForStreams = false };
        }

        if (options.HasExplicitUsePrimarySessionForStreams)
        {
            return options;
        }

        I2pOptions transportOptions = new()
        {
            SamHost = options.SamHost,
            SamPort = options.SamPort,
            SamUdpHost = options.SamUdpHost,
            SamUdpPort = options.SamUdpPort,
            SessionId = options.SessionId,
            StreamSessionId = options.StreamSessionId,
            DatagramSessionId = options.DatagramSessionId,
            PrimarySessionStyle = options.PrimarySessionStyle,
            UsePrimarySessionForStreams = false,
            Destination = options.Destination,
            DestinationKeyFile = options.DestinationKeyFile,
            DestinationSignatureType = options.DestinationSignatureType,
            ConnectTimeoutMilliseconds = options.ConnectTimeoutMilliseconds,
            DatagramHost = options.DatagramHost,
            DatagramPort = options.DatagramPort,
            MaxDatagramPayloadSize = options.MaxDatagramPayloadSize
        };

        transportOptions.SessionOptions.Clear();
        foreach ((string key, string value) in options.SessionOptions)
        {
            transportOptions.SessionOptions[key] = value;
        }

        return transportOptions;
    }

    private I2pOptions CreatePeerOptions(PeerId peerId)
    {
        string peerIdText = peerId.ToString();
        I2pOptions peerOptions = new()
        {
            SamHost = _options.SamHost,
            SamPort = _options.SamPort,
            SamUdpHost = _options.SamUdpHost,
            SamUdpPort = _options.SamUdpPort,
            SessionId = $"{_options.SessionId}-{peerIdText[..Math.Min(12, peerIdText.Length)]}",
            StreamSessionId = $"{_options.StreamSessionId}-{peerIdText[..Math.Min(12, peerIdText.Length)]}",
            DatagramSessionId = $"{_options.DatagramSessionId}-{peerIdText[..Math.Min(12, peerIdText.Length)]}",
            PrimarySessionStyle = _options.PrimarySessionStyle,
            UsePrimarySessionForStreams = _options.UsePrimarySessionForStreams,
            Destination = _options.Destination,
            DestinationKeyFile = _options.DestinationKeyFile,
            DestinationSignatureType = _options.DestinationSignatureType,
            ConnectTimeoutMilliseconds = _options.ConnectTimeoutMilliseconds,
            DatagramHost = _options.DatagramHost,
            DatagramPort = _options.DatagramPort,
            MaxDatagramPayloadSize = _options.MaxDatagramPayloadSize
        };

        peerOptions.SessionOptions.Clear();
        foreach ((string key, string value) in _options.SessionOptions)
        {
            peerOptions.SessionOptions[key] = value;
        }

        return peerOptions;
    }

    private async Task PumpAsync(NetworkStream stream, IChannel upChannel, INewConnectionContext connectionContext, CancellationToken token)
    {
        await using (stream.ConfigureAwait(false))
        {
            using CancellationTokenSource pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token, connectionContext.Token);
            Task readTask = Task.Run(async () =>
            {
                byte[] buffer = new byte[8192];
                try
                {
                    while (!pumpCts.Token.IsCancellationRequested)
                    {
                        int read = await stream.ReadAsync(buffer, pumpCts.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        byte[] ownedBytes = buffer.AsMemory()[..read].ToArray();
                        IOResult result = await upChannel.WriteAsync(new ReadOnlySequence<byte>(ownedBytes), pumpCts.Token).ConfigureAwait(false);
                        if (result != IOResult.Ok)
                        {
                            break;
                        }
                    }
                }
                catch (IOException ex) when (pumpCts.IsCancellationRequested)
                {
                    _logger?.LogDebug(ex, "I2P stream read ended.");
                }
                catch (IOException ex)
                {
                    _logger?.LogWarning(ex, "I2P stream read failed.");
                }
                catch (OperationCanceledException) when (pumpCts.IsCancellationRequested)
                {
                }
                finally
                {
                    await upChannel.WriteEofAsync(CancellationToken.None).ConfigureAwait(false);
                }
            });

            Task writeTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync(pumpCts.Token).ConfigureAwait(false))
                    {
                        foreach (ReadOnlyMemory<byte> segment in data)
                        {
                            await stream.WriteAsync(segment, pumpCts.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (IOException ex) when (pumpCts.IsCancellationRequested)
                {
                    _logger?.LogDebug(ex, "I2P stream write ended.");
                }
                catch (IOException ex)
                {
                    _logger?.LogWarning(ex, "I2P stream write failed.");
                }
                catch (Exception ex) when (IsExpectedPumpShutdown(ex, pumpCts.IsCancellationRequested))
                {
                    _logger?.LogDebug(ex, "I2P channel write ended.");
                }
                catch (OperationCanceledException) when (pumpCts.IsCancellationRequested)
                {
                }
            });

            await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
            await pumpCts.CancelAsync().ConfigureAwait(false);
            await upChannel.CloseAsync().ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(readTask, writeTask).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpectedPumpShutdown(ex, pumpCts.IsCancellationRequested))
            {
                _logger?.LogDebug(ex, "I2P stream pump ended.");
            }
            connectionContext.Dispose();
        }
    }

    private static bool IsExpectedPumpShutdown(Exception exception, bool isCanceled)
        => isCanceled && (exception is IOException or ObjectDisposedException or ChannelClosedException or OperationCanceledException
            || exception.GetType() == typeof(Exception));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach ((PeerId peerId, _) in _samClients.ToArray())
        {
            await ReleasePeerAsync(peerId).ConfigureAwait(false);
        }
    }

    public async ValueTask ReleasePeerAsync(PeerId peerId)
    {
        if (_samClients.TryRemove(peerId, out I2pSamClient? client))
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
