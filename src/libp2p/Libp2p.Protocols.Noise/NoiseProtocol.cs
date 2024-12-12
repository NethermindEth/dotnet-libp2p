// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Noise;
using System.Text;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Protocols.Noise.Dto;
using PublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// </summary>
public class NoiseProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null) : IConnectionProtocol
{
    private readonly Protocol _protocol = new(
            HandshakePattern.XX,
            CipherFunction.ChaChaPoly,
            HashFunction.Sha256
        );

    private readonly ILogger? _logger = loggerFactory?.CreateLogger<NoiseProtocol>();
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
        if (context.State.RemoteAddress is null)
        {
            throw new Libp2pException();
        }

        KeyPair? clientStatic = KeyPair.Generate();
        using HandshakeState? handshakeState = _protocol.Create(true, s: clientStatic.PrivateKey);
        byte[] buffer = new byte[Protocol.MaxMessageLength];

        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg0 = handshakeState.WriteMessage(null, buffer);

        byte[]? lenBytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg0.BytesWritten);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes));
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg0.BytesWritten));

        lenBytes = (await downChannel.ReadAsync(2).OrThrow()).ToArray();

        int len = BinaryPrimitives.ReadInt16BigEndian(lenBytes.AsSpan());
        ReadOnlySequence<byte> received = await downChannel.ReadAsync(len).OrThrow();
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg1 =
            handshakeState.ReadMessage(received.ToArray(), buffer);
        NoiseHandshakePayload? msg1Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg1.BytesRead));

        PublicKey? msg1KeyDecoded = PublicKey.Parser.ParseFrom(msg1Decoded.IdentityKey);
        context.State.RemotePublicKey = msg1KeyDecoded;
        // TODO: verify signature

        List<string> responderMuxers = msg1Decoded.Extensions.StreamMuxers
            .Where(m => !string.IsNullOrEmpty(m))
            .ToList();
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
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes));
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg2.BytesWritten));
        Transport? transport = msg2.Transport;

        _logger?.LogDebug("Established connection to {peer}", context.State.RemoteAddress);

        IChannel upChannel = context.Upgrade(upgradeOptions);

        await ExchangeData(transport, downChannel, upChannel);

        _ = upChannel.CloseAsync();
        _logger?.LogDebug("Closed");
    }

    public async Task ListenAsync(IChannel downChannel, IConnectionContext context)
    {
        if (context.State.RemoteAddress is null)
        {
            throw new Libp2pException();
        }

        KeyPair? serverStatic = KeyPair.Generate();
        using HandshakeState? handshakeState =
            _protocol.Create(false,
                s: serverStatic.PrivateKey);

        byte[]? lenBytes = (await downChannel.ReadAsync(2).OrThrow()).ToArray();
        short len = BinaryPrimitives.ReadInt16BigEndian(lenBytes);
        byte[] buffer = new byte[Protocol.MaxMessageLength];
        ReadOnlySequence<byte> msg0Bytes = await downChannel.ReadAsync(len).OrThrow();
        handshakeState.ReadMessage(msg0Bytes.ToArray(), buffer);

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
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg1.BytesWritten + 2));

        lenBytes = (await downChannel.ReadAsync(2).OrThrow()).ToArray();
        len = BinaryPrimitives.ReadInt16BigEndian(lenBytes);
        ReadOnlySequence<byte> hs2Bytes = await downChannel.ReadAsync(len).OrThrow();
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg2 =
            handshakeState.ReadMessage(hs2Bytes.ToArray(), buffer);
        NoiseHandshakePayload? msg2Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg2.BytesRead));
        PublicKey? msg2KeyDecoded = PublicKey.Parser.ParseFrom(msg2Decoded.IdentityKey);
        context.State.RemotePublicKey = msg2KeyDecoded;
        // TODO: verify signature

        Transport? transport = msg2.Transport;
        List<string> initiatorMuxers = msg2Decoded.Extensions.StreamMuxers.Where(m => !string.IsNullOrEmpty(m)).ToList();
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

        await ExchangeData(transport, downChannel, upChannel);

        _ = upChannel.CloseAsync();
        _logger?.LogDebug("Closed");
    }

    private static Task ExchangeData(Transport transport, IChannel downChannel, IChannel upChannel)
    {
        // UP -> DOWN
        Task t = Task.Run(async () =>
        {
            for (; ; )
            {
                ReadResult dataReadResult = await upChannel.ReadAsync(Protocol.MaxMessageLength - 16, ReadBlockingMode.WaitAny);
                if (dataReadResult.Result != IOResult.Ok)
                {
                    return;
                }

                byte[] buffer = new byte[2 + 16 + dataReadResult.Data.Length];

                int bytesWritten = transport.WriteMessage(dataReadResult.Data.ToArray(), buffer.AsSpan(2));
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(), (ushort)bytesWritten);
                IOResult writeResult = await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer));
                if (writeResult != IOResult.Ok)
                {
                    return;
                }
            }
        });
        // DOWN -> UP
        Task t2 = Task.Run(async () =>
        {
            for (; ; )
            {
                ReadResult lengthBytesReadResult = await downChannel.ReadAsync(2, ReadBlockingMode.WaitAll);
                if (lengthBytesReadResult.Result != IOResult.Ok)
                {
                    return;
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytesReadResult.Data.ToArray().AsSpan());

                ReadResult dataReadResult = await downChannel.ReadAsync(length);
                if (dataReadResult.Result != IOResult.Ok)
                {
                    return;
                }
                byte[] buffer = new byte[length - 16];

                int bytesRead = transport.ReadMessage(dataReadResult.Data.ToArray(), buffer);

                IOResult writeResult = await upChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, bytesRead));
                if (writeResult != IOResult.Ok)
                {
                    return;
                }
            }
        });

        return Task.WhenAll(t, t2).ContinueWith((t) =>
        {

        });
    }
}
