// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Noise;
using System.Text;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.Noise.Dto;
using PublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// </summary>
public class NoiseProtocol : IConnectionProtocol
{
    private readonly Protocol _protocol = new(
            HandshakePattern.XX,
            CipherFunction.ChaChaPoly,
            HashFunction.Sha256
        );

    private readonly ILogger? _logger;
    private const int LengthPrefixSize = 2;
    private const int NoiseTagSize = 16;
    private const int MaxPlaintextLength = Protocol.MaxMessageLength - NoiseTagSize;

    public NoiseProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<NoiseProtocol>();
    }

    private NoiseExtensions _extensions => new()
    {
        StreamMuxers = { } // TODO: return the following after go question resolution:
        //{
        //   multiplexerSettings is null || !multiplexerSettings.Multiplexers.Any() ? ["na"] : [.. multiplexerSettings.Multiplexers.Select(proto => proto.Id)]
        //}
    };

    public string Id => "/noise";

    private const string PayloadSigPrefix = "noise-libp2p-static-key:";

    public async Task DialAsync(IChannel downChannel, IConnectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);

        KeyPair? clientStatic = KeyPair.Generate();
        using HandshakeState? handshakeState = _protocol.Create(true, s: clientStatic.PrivateKey);
        byte[] buffer = new byte[Protocol.MaxMessageLength];

        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg0 = handshakeState.WriteMessage(null, buffer);

        byte[]? lenBytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg0.BytesWritten);
        await downChannel.WriteAsync(lenBytes);
        await downChannel.WriteAsync(buffer.AsMemory(0, msg0.BytesWritten));

        using ReadResult lenResult = await downChannel.ReadAsync(2).OrThrow();
        lenBytes = lenResult.ToArray();

        int len = BinaryPrimitives.ReadInt16BigEndian(lenBytes.AsSpan());
        using ReadResult received = await downChannel.ReadAsync(len).OrThrow();
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg1 =
            handshakeState.ReadMessage(received.Data, buffer);
        NoiseHandshakePayload? msg1Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg1.BytesRead));

        if (msg1Decoded is null)
        {
            throw new Libp2pException("Bad handshake message has been received.");
        }

        PublicKey? msg1KeyDecoded = PublicKey.Parser.ParseFrom(msg1Decoded.IdentityKey);

        if (msg1KeyDecoded is null)
        {
            throw new Libp2pException($"{nameof(PublicKey)} is absent in the handshake message.");
        }

        if (msg1Decoded.IdentitySig is null || msg1Decoded.IdentitySig.IsEmpty)
        {
            throw new Libp2pException("Responder identity signature is missing in the handshake payload.");
        }

        byte[] remoteNoiseStaticKey = handshakeState.RemoteStaticPublicKey.ToArray();
        if (remoteNoiseStaticKey.Length == 0)
        {
            throw new Libp2pException("Responder noise static public key is absent after handshake.");
        }

        byte[] responderSignedMessage = [.. Encoding.UTF8.GetBytes(PayloadSigPrefix), .. remoteNoiseStaticKey];
        Identity responderIdentity = new(msg1KeyDecoded);
        if (!responderIdentity.VerifySignature(responderSignedMessage, msg1Decoded.IdentitySig.ToByteArray()))
        {
            throw new Libp2pException("Noise handshake signature verification failed: responder identity key does not match noise static key.");
        }

        context.State.RemotePublicKey = msg1KeyDecoded;


        List<string> responderMuxers = msg1Decoded.Extensions?.StreamMuxers?
            .Where(m => !string.IsNullOrEmpty(m))
            .ToList() ?? [];
        IProtocol? commonMuxer = null;// multiplexerSettings?.Multiplexers.FirstOrDefault(m => responderMuxers.Contains(m.Id));

        UpgradeOptions? upgradeOptions = null;

        if (commonMuxer is not null)
        {
            upgradeOptions = new UpgradeOptions
            {
                SelectedProtocol = commonMuxer,
            };
        }

        PeerId remotePeerId = new(msg1KeyDecoded);
        if (!context.State.RemoteAddress.Has<P2P>())
        {
            context.State.RemoteAddress.Add(new P2P(remotePeerId.ToString()));
        }

        byte[] msg = [.. Encoding.UTF8.GetBytes(PayloadSigPrefix), .. ByteString.CopyFrom(clientStatic.PublicKey)];
        byte[] sig = context.Peer.Identity.Sign(msg);
        NoiseHandshakePayload payload = new()
        {
            IdentityKey = context.Peer.Identity.PublicKey.ToByteString(),
            IdentitySig = ByteString.CopyFrom(sig),
            Extensions = _extensions
        };

        if (_logger is not null && _logger.IsEnabled(LogLevel.Trace))
        {
            _logger?.LogTrace("Local public key {0}", Convert.ToHexString(clientStatic.PublicKey));
            //_logger?.LogTrace("local  prv key {0}", clientStatic.PrivateKey);
            _logger?.LogTrace("Remote public key {0}", Convert.ToHexString(handshakeState.RemoteStaticPublicKey.ToArray()));
        }

        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg2 =
            handshakeState.WriteMessage(payload.ToByteArray(), buffer);
        BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg2.BytesWritten);
        await downChannel.WriteAsync(lenBytes);
        await downChannel.WriteAsync(buffer.AsMemory(0, msg2.BytesWritten));
        Transport? transport = msg2.Transport;

        _logger?.LogDebug("Established connection to {peer}", context.State.RemoteAddress);

        IChannel upChannel = context.Upgrade(upgradeOptions);

        await ExchangeData(transport, downChannel, upChannel, _logger);

        _ = upChannel.CloseAsync();
        _ = downChannel.CloseAsync();
        _logger?.LogDebug("Closed");
    }

    public async Task ListenAsync(IChannel downChannel, IConnectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);

        KeyPair? serverStatic = KeyPair.Generate();
        using HandshakeState? handshakeState =
            _protocol.Create(false,
                s: serverStatic.PrivateKey);

        using ReadResult msg0Length = await downChannel.ReadAsync(2).OrThrow();
        short len = BinaryPrimitives.ReadInt16BigEndian(msg0Length.Data);
        byte[] buffer = new byte[Protocol.MaxMessageLength];
        using ReadResult msg0Bytes = await downChannel.ReadAsync(len).OrThrow();
        handshakeState.ReadMessage(msg0Bytes.Data, buffer);

        byte[] msg = Encoding.UTF8.GetBytes(PayloadSigPrefix)
            .Concat(ByteString.CopyFrom(serverStatic.PublicKey))
            .ToArray();
        byte[] sig = context.Peer.Identity.Sign(msg);

        NoiseHandshakePayload payload = new()
        {
            IdentityKey = context.Peer.Identity.PublicKey.ToByteString(),
            IdentitySig = ByteString.CopyFrom(sig),
            Extensions = _extensions
        };

        // Send the second handshake message to the client.
        buffer = new byte[Protocol.MaxMessageLength];
        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg1 =
            handshakeState.WriteMessage(payload.ToByteArray(), buffer.AsSpan(2));
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(), (short)msg1.BytesWritten);
        await downChannel.WriteAsync(buffer.AsMemory(0, msg1.BytesWritten + 2));

        using ReadResult hs2Length = await downChannel.ReadAsync(2).OrThrow();
        len = BinaryPrimitives.ReadInt16BigEndian(hs2Length.Data);
        using ReadResult hs2Bytes = await downChannel.ReadAsync(len).OrThrow();
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg2 =
            handshakeState.ReadMessage(hs2Bytes.Data, buffer);
        NoiseHandshakePayload? msg2Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg2.BytesRead));

        if (msg2Decoded is null)
        {
            throw new Libp2pException("Bad handshake message has not been received.");
        }

        PublicKey? msg2KeyDecoded = PublicKey.Parser.ParseFrom(msg2Decoded.IdentityKey);

        if (msg2KeyDecoded is null)
        {
            throw new Libp2pException($"{nameof(PublicKey)} is absent in the handshake message.");
        }

        if (msg2Decoded.IdentitySig is null || msg2Decoded.IdentitySig.IsEmpty)
        {
            throw new Libp2pException("Initiator identity signature is missing in the handshake payload.");
        }

        byte[] remoteNoiseStaticKey = handshakeState.RemoteStaticPublicKey.ToArray();
        if (remoteNoiseStaticKey.Length == 0)
        {
            throw new Libp2pException("Initiator noise static public key is absent after handshake.");
        }

        byte[] initiatorSignedMessage = [.. Encoding.UTF8.GetBytes(PayloadSigPrefix), .. remoteNoiseStaticKey];
        Identity initiatorIdentity = new(msg2KeyDecoded);
        if (!initiatorIdentity.VerifySignature(initiatorSignedMessage, msg2Decoded.IdentitySig.ToByteArray()))
        {
            throw new Libp2pException("Noise handshake signature verification failed: initiator identity key does not match noise static key.");
        }

        context.State.RemotePublicKey = msg2KeyDecoded;

        Transport? transport = msg2.Transport;

        List<string> initiatorMuxers = msg2Decoded.Extensions?.StreamMuxers?.Where(m => !string.IsNullOrEmpty(m)).ToList() ?? [];
        _logger?.LogTrace("Initiator muxers received are {initiatorMuxers}.", string.Join(",", initiatorMuxers));

        IProtocol? commonMuxer = null; // multiplexerSettings?.Multiplexers.FirstOrDefault(m => initiatorMuxers.Contains(m.Id));

        UpgradeOptions? upgradeOptions = null;

        if (commonMuxer is not null)
        {
            upgradeOptions = new UpgradeOptions
            {
                SelectedProtocol = commonMuxer,
            };
        }

        if (!context.State.RemoteAddress.Has<P2P>())
        {
            PeerId remotePeerId = new(msg2KeyDecoded);
            context.State.RemoteAddress.Add(new P2P(remotePeerId.ToString()));
        }

        _logger?.LogDebug("Established connection to {peer}", context.State.RemoteAddress);

        IChannel upChannel = context.Upgrade(upgradeOptions);

        await ExchangeData(transport, downChannel, upChannel, _logger);

        _ = upChannel.CloseAsync();
        _ = downChannel.CloseAsync();
        _logger?.LogDebug("Closed");
    }

    private static Task ExchangeData(Transport transport, IChannel downChannel, IChannel upChannel, ILogger? logger)
    {
        // UP -> DOWN
        Task upToDown = Task.Run(async () =>
        {
            for (; ; )
            {
                ReadResult dataReadResult = await upChannel.ReadAsync(MaxPlaintextLength, ReadBlockingMode.WaitAny);
                if (dataReadResult.Result != IOResult.Ok)
                {
                    logger?.LogDebug("End reading, due to {}", dataReadResult.Result);
                    dataReadResult.Dispose();
                    return;
                }

                using PooledBuffer buffer = PooledBuffer.Rent(LengthPrefixSize + NoiseTagSize + dataReadResult.Length);

                int bytesWritten = transport.WriteMessage(dataReadResult.Data, buffer.Span[LengthPrefixSize..]);
                dataReadResult.Dispose();
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Span, (ushort)bytesWritten);
                IOResult writeResult = await downChannel.WriteAsync(buffer, LengthPrefixSize + bytesWritten);
                if (writeResult != IOResult.Ok)
                {
                    logger?.LogDebug("End sending, due to {}", writeResult);
                    return;
                }
            }
        });
        // DOWN -> UP
        Task downToUp = Task.Run(async () =>
        {
            for (; ; )
            {
                ReadResult lengthBytesReadResult = await downChannel.ReadAsync(LengthPrefixSize, ReadBlockingMode.WaitAll);
                if (lengthBytesReadResult.Result != IOResult.Ok)
                {
                    logger?.LogDebug("Receiving packet length failed due to {}", lengthBytesReadResult.Result);
                    lengthBytesReadResult.Dispose();
                    return;
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytesReadResult.Data);
                lengthBytesReadResult.Dispose();

                ReadResult dataReadResult = await downChannel.ReadAsync(length);
                if (dataReadResult.Result != IOResult.Ok)
                {
                    logger?.LogDebug("Receiving header failed due to {}", dataReadResult);
                    dataReadResult.Dispose();
                    return;
                }
                using PooledBuffer buffer = PooledBuffer.Rent(length - NoiseTagSize);

                int bytesRead = transport.ReadMessage(dataReadResult.Data, buffer.Span);
                dataReadResult.Dispose();

                if (bytesRead != 0)
                {
                    IOResult writeResult = await upChannel.WriteAsync(buffer, bytesRead);
                    if (writeResult != IOResult.Ok)
                    {
                        logger?.LogDebug("Receiving data failed due to {}", dataReadResult);
                        return;
                    }
                }
            }
        });

        return Task.WhenAny(upToDown, downToUp);
    }
}
