// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Noise;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Protocols.Noise.Dto;
using PublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;
using Org.BouncyCastle.Utilities.Encoders;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// </summary>
public class NoiseProtocol : IProtocol
{
    private readonly Protocol _protocol;
    private readonly byte[][] _psks;
    private readonly ILogger? _logger;
    public string Id => "/noise";
    private const string PayloadSigPrefix = "noise-libp2p-static-key:";

    public NoiseProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<NoiseProtocol>();
        _protocol = new Protocol(
            HandshakePattern.XX,
            CipherFunction.ChaChaPoly,
            HashFunction.Sha256
        );
        _psks = Array.Empty<byte[]>();
    }

    public async Task DialAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    {
        KeyPair? clientStatic = KeyPair.Generate();
        using HandshakeState? handshakeState = _protocol.Create(true, s: clientStatic.PrivateKey);
        byte[] buffer = new byte[Protocol.MaxMessageLength];

        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg0 = handshakeState.WriteMessage(null, buffer);

        byte[]? lenBytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg0.BytesWritten);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes));
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg0.BytesWritten));

        lenBytes = (await downChannel.ReadAsync(2)).ToArray();
        int len = (int)BinaryPrimitives.ReadInt16BigEndian(lenBytes.AsSpan());
        ReadOnlySequence<byte> received = await downChannel.ReadAsync(len);
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg1 =
            handshakeState.ReadMessage(received.ToArray(), buffer);
        NoiseHandshakePayload? msg1Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg1.BytesRead));
        PublicKey? msg1KeyDecoded = PublicKey.Parser.ParseFrom(msg1Decoded.IdentityKey);
        //var key = new byte[] { 0x1 }.Concat(clientStatic.PublicKey).ToArray();

        byte[] msg = Encoding.UTF8.GetBytes(PayloadSigPrefix)
            .Concat(ByteString.CopyFrom(clientStatic.PublicKey))
            .ToArray();
        byte[] sig = new byte[64];
        Ed25519.Sign(context.LocalPeer.Identity.PrivateKey.Data.ToArray(), 0, msg, 0, msg.Length, sig, 0);
        NoiseHandshakePayload payload = new()
        {
            IdentityKey = context.LocalPeer.Identity.PublicKey.ToByteString(),
            IdentitySig = ByteString.CopyFrom(sig),
            Extensions = new NoiseExtensions
            {
                //StreamMuxers = { "/yamux/1.0.0" }
                StreamMuxers = { "na" }
            }
        };
        _logger?.LogInformation("local  pub key {0}", clientStatic.PublicKey);
        _logger?.LogInformation("local  prv key {0}", clientStatic.PrivateKey);
        _logger?.LogInformation("remote pub key {0}", handshakeState.RemoteStaticPublicKey.ToArray());
        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg2 =
            handshakeState.WriteMessage(payload.ToByteArray(), buffer);
        BinaryPrimitives.WriteInt16BigEndian(lenBytes.AsSpan(), (short)msg2.BytesWritten);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes));
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg2.BytesWritten));
        Transport? transport = msg2.Transport;

        IChannel upChannel = upChannelFactory.SubDial(context);
        downChannel.OnClose(() => upChannel.CloseAsync());
        // UP -> DOWN
        Task t = Task.Run(async () =>
        {
            while (!downChannel.IsClosed && !upChannel.IsClosed)
            {
                ReadOnlySequence<byte> request =
                    await upChannel.ReadAsync(Protocol.MaxMessageLength - 16, ReadBlockingMode.WaitAny);
                byte[] buffer = new byte[2 + 16 + request.Length];

                int bytesWritten = transport.WriteMessage(request.ToArray(), buffer.AsSpan(2));
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(), (ushort)bytesWritten);

                string str = Encoding.UTF8.GetString(request.ToArray());
                _logger?.LogTrace($"> {buffer.Length}(payload {request.Length})");

                await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer));
            }
        });
        // DOWN -> UP
        Task t2 = Task.Run(async () =>
        {
            while (!downChannel.IsClosed && !upChannel.IsClosed)
            {
                lenBytes = (await downChannel.ReadAsync(2)).ToArray();
                int len = (int)BinaryPrimitives.ReadUInt16BigEndian(lenBytes.AsSpan());
                ReadOnlySequence<byte> request =
                    await downChannel.ReadAsync(len);
                byte[] buffer = new byte[len - 16];

                _logger?.LogTrace("start READ");

                int bytesRead = transport.ReadMessage(request.ToArray(), buffer);
                _logger?.LogTrace("READ");
                _logger?.LogTrace($"< {len + 2}/(payload {bytesRead}) {Hex.ToHexString(buffer)} {Encoding.UTF8.GetString(buffer).ReplaceLineEndings()}");
                await upChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, bytesRead));
            }
        });

        await Task.WhenAll(t, t2);
    }

    public async Task ListenAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    {
        KeyPair? serverStatic = KeyPair.Generate();
        using HandshakeState? handshakeState =
            _protocol.Create(false,
                s: serverStatic.PrivateKey);

        byte[]? lenBytes = (await downChannel.ReadAsync(2)).ToArray();
        short len = BinaryPrimitives.ReadInt16BigEndian(lenBytes);
        byte[] buffer = new byte[Protocol.MaxMessageLength];
        ReadOnlySequence<byte> msg0Bytes = await downChannel.ReadAsync(len);
        handshakeState.ReadMessage(msg0Bytes.ToArray(), buffer);

        byte[] msg = Encoding.UTF8.GetBytes(PayloadSigPrefix)
            .Concat(ByteString.CopyFrom(serverStatic.PublicKey))
            .ToArray();
        byte[] sig = new byte[64];
        Ed25519.Sign(context.LocalPeer.Identity.PrivateKey.Data.ToArray(), 0, msg, 0, msg.Length, sig, 0);
        NoiseHandshakePayload payload = new()
        {
            IdentityKey = context.LocalPeer.Identity.PublicKey.ToByteString(),
            IdentitySig = ByteString.CopyFrom(sig),
            Extensions = new NoiseExtensions
            {
                //StreamMuxers = { "/yamux/1.0.0" }
                StreamMuxers = { "na" }
            }
        };

        // Send the second handshake message to the client.
        buffer = new byte[Protocol.MaxMessageLength];
        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg1 =
            handshakeState.WriteMessage(payload.ToByteArray(), buffer.AsSpan(2));
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(), (short)msg1.BytesWritten);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg1.BytesWritten + 2));

        lenBytes = (await downChannel.ReadAsync(2)).ToArray();
        len = BinaryPrimitives.ReadInt16BigEndian(lenBytes);
        ReadOnlySequence<byte> hs2Bytes = await downChannel.ReadAsync(len);
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg2 =
            handshakeState.ReadMessage(hs2Bytes.ToArray(), buffer);
        NoiseHandshakePayload? msg2Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg2.BytesRead));
        PublicKey? msg2KeyDecoded = PublicKey.Parser.ParseFrom(msg2Decoded.IdentityKey);
        Transport? transport = msg2.Transport;

        IChannel upChannel = upChannelFactory.SubListen(context);
        // UP -> DOWN
        Task t = Task.Run(async () =>
        {
            while (!downChannel.IsClosed && !upChannel.IsClosed)
            {
                ReadOnlySequence<byte> request =
                    await upChannel.ReadAsync(Protocol.MaxMessageLength - 16, ReadBlockingMode.WaitAny);
                byte[] buffer = new byte[2 + 16 + request.Length];

                int bytesWritten = transport.WriteMessage(request.ToArray(), buffer.AsSpan(2));
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(), (ushort)bytesWritten);

                _logger?.LogTrace($"> {request.Length}");
                await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer));
            }
        });

        // DOWN -> UP
        Task t2 = Task.Run(async () =>
        {
            while (!downChannel.IsClosed && !upChannel.IsClosed)
            {
                lenBytes = (await downChannel.ReadAsync(2)).ToArray();
                int len = BinaryPrimitives.ReadUInt16BigEndian(lenBytes.AsSpan());
                ReadOnlySequence<byte> request =
                    await downChannel.ReadAsync(len);
                byte[] buffer = new byte[len - 16];

                int bytesRead = transport.ReadMessage(request.ToArray(), buffer);
                _logger?.LogTrace($"< {bytesRead}");
                await upChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, bytesRead));
            }
        });

        await Task.WhenAll(t, t2);
    }
}
