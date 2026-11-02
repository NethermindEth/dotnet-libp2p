// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
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

    /// <summary>Plaintext carried by one Noise message: the transport message limit less its 16-byte tag.</summary>
    private const int MaxPlaintextPerMessage = Protocol.MaxMessageLength - 16;

    public async Task DialAsync(IChannel downChannel, IConnectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);

        KeyPair? clientStatic = KeyPair.Generate();
        using HandshakeState? handshakeState = _protocol.Create(true, s: clientStatic.PrivateKey);
        byte[] buffer = new byte[Protocol.MaxMessageLength];

        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg0 = handshakeState.WriteMessage(null, buffer);

        byte[]? lenBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBytes.AsSpan(), checked((ushort)msg0.BytesWritten));
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes), token: context.Token);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg0.BytesWritten), token: context.Token);

        using (ReadResult lengthRead = await downChannel.ReadOwnedAsync(2, token: context.Token))
        {
            lengthRead.OrThrow().CopyTo(lenBytes);
        }

        int len = BinaryPrimitives.ReadUInt16BigEndian(lenBytes.AsSpan());
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg1;
        using (ReadResult messageRead = await downChannel.ReadOwnedAsync(len, token: context.Token))
        {
            ReadOnlySequence<byte> received = messageRead.OrThrow();
            msg1 = handshakeState.ReadMessage(received.IsSingleSegment ? received.FirstSpan : received.ToArray(), buffer);
        }
        NoiseHandshakePayload? msg1Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg1.BytesRead));

        if (msg1Decoded is null)
        {
            throw new Libp2pException("Bad handshake message has been received.");
        }

        PublicKey msg1KeyDecoded = PublicKeyDecoder.Decode(msg1Decoded.IdentityKey);

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

        SetRemoteIdentity(context, msg1KeyDecoded);

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
        BinaryPrimitives.WriteUInt16BigEndian(lenBytes.AsSpan(), checked((ushort)msg2.BytesWritten));
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(lenBytes), token: context.Token);
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg2.BytesWritten), token: context.Token);
        Transport? transport = msg2.Transport;

        _logger?.LogDebug("Established connection to {peer}", context.State.RemoteAddress);

        // Each write becomes one Noise message, so a write of this size costs one record.
        context.State.PreferredWriteSize = MaxPlaintextPerMessage;

        IChannel upChannel = context.Upgrade(upgradeOptions);

        await ExchangeData(transport, downChannel, upChannel, _logger, context.Token);

        _logger?.LogDebug("Closed");
    }

    public async Task ListenAsync(IChannel downChannel, IConnectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);

        KeyPair? serverStatic = KeyPair.Generate();
        using HandshakeState? handshakeState =
            _protocol.Create(false,
                s: serverStatic.PrivateKey);

        int len;
        using (ReadResult lengthRead = await downChannel.ReadOwnedAsync(2, token: context.Token))
        {
            ReadOnlySequence<byte> lengthBytes = lengthRead.OrThrow();
            len = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes.IsSingleSegment ? lengthBytes.FirstSpan : lengthBytes.ToArray());
        }
        byte[] buffer = new byte[Protocol.MaxMessageLength];
        using (ReadResult messageRead = await downChannel.ReadOwnedAsync(len, token: context.Token))
        {
            ReadOnlySequence<byte> msg0Bytes = messageRead.OrThrow();
            handshakeState.ReadMessage(msg0Bytes.IsSingleSegment ? msg0Bytes.FirstSpan : msg0Bytes.ToArray(), buffer);
        }

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
        buffer = new byte[Protocol.MaxMessageLength + 2];
        (int BytesWritten, byte[] HandshakeHash, Transport Transport) msg1 =
            handshakeState.WriteMessage(payload.ToByteArray(), buffer.AsSpan(2));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(), checked((ushort)msg1.BytesWritten));
        await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, msg1.BytesWritten + 2), token: context.Token);

        using (ReadResult lengthRead = await downChannel.ReadOwnedAsync(2, token: context.Token))
        {
            ReadOnlySequence<byte> lengthBytes = lengthRead.OrThrow();
            len = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes.IsSingleSegment ? lengthBytes.FirstSpan : lengthBytes.ToArray());
        }
        (int BytesRead, byte[] HandshakeHash, Transport Transport) msg2;
        using (ReadResult messageRead = await downChannel.ReadOwnedAsync(len, token: context.Token))
        {
            ReadOnlySequence<byte> hs2Bytes = messageRead.OrThrow();
            msg2 = handshakeState.ReadMessage(hs2Bytes.IsSingleSegment ? hs2Bytes.FirstSpan : hs2Bytes.ToArray(), buffer);
        }
        NoiseHandshakePayload? msg2Decoded = NoiseHandshakePayload.Parser.ParseFrom(buffer.AsSpan(0, msg2.BytesRead));

        if (msg2Decoded is null)
        {
            throw new Libp2pException("Bad handshake message has not been received.");
        }

        PublicKey msg2KeyDecoded = PublicKeyDecoder.Decode(msg2Decoded.IdentityKey);

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

        SetRemoteIdentity(context, msg2KeyDecoded);

        _logger?.LogDebug("Established connection to {peer}", context.State.RemoteAddress);

        // Each write becomes one Noise message, so a write of this size costs one record.
        context.State.PreferredWriteSize = MaxPlaintextPerMessage;

        IChannel upChannel = context.Upgrade(upgradeOptions);

        await ExchangeData(transport, downChannel, upChannel, _logger, context.Token);

        _logger?.LogDebug("Closed");
    }

    private static void SetRemoteIdentity(IConnectionContext context, PublicKey remotePublicKey)
    {
        if (context.State.RemotePublicKey is { } existingRemotePublicKey && existingRemotePublicKey.ToByteString() != remotePublicKey.ToByteString())
        {
            throw new Libp2pException("Noise identity does not match the previously authenticated remote public key.");
        }

        PeerId remotePeerId = new(remotePublicKey);
        PeerId? expectedPeerId = context.State.RemoteAddress?.GetPeerId();
        if (expectedPeerId is not null && expectedPeerId != remotePeerId)
        {
            throw new Libp2pException("Noise handshake identity does not match the expected remote peer ID.");
        }

        context.State.RemotePublicKey = remotePublicKey;
        if (context.State.RemoteAddress is { } remoteAddress && expectedPeerId is null)
        {
            remoteAddress.Add(new P2P(remotePeerId.ToString()));
        }
    }

    private static async Task ExchangeData(Transport transport, IChannel downChannel, IChannel upChannel, ILogger? logger, CancellationToken connectionToken)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        CancellationToken token = lifetime.Token;
        using CancellationTokenRegistration abort = token.Register(() =>
        {
            if (!upChannel.GetAwaiter().IsCompleted) _ = upChannel.AbortAsync();
            if (!downChannel.GetAwaiter().IsCompleted) _ = downChannel.AbortAsync();
        });
        // UP -> DOWN
        Task<bool> upToDown = Task.Run(async () =>
        {
            for (; ; )
            {
                byte[] buffer;
                int bytesWritten;
                using (ReadResult dataReadResult = await upChannel.ReadOwnedAsync(MaxPlaintextPerMessage, ReadBlockingMode.WaitAny, token: token))
                {
                    if (dataReadResult.Result != IOResult.Ok)
                    {
                        logger?.LogDebug("End reading, due to {}", dataReadResult.Result);
                        if (dataReadResult.Result == IOResult.Ended) await downChannel.WriteEofAsync(token);
                        return dataReadResult.Result == IOResult.Ended;
                    }

                    ReadOnlySequence<byte> data = dataReadResult.Data;
                    buffer = new byte[2 + 16 + data.Length];
                    bytesWritten = transport.WriteMessage(data.IsSingleSegment ? data.FirstSpan : data.ToArray(), buffer.AsSpan(2));
                }

                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(), (ushort)bytesWritten);
                IOResult writeResult = await downChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, 2 + bytesWritten), token: token);
                if (writeResult != IOResult.Ok)
                {
                    logger?.LogDebug("End sending, due to {}", writeResult);
                    return writeResult == IOResult.Ended;
                }
            }
        });
        // DOWN -> UP
        Task<bool> downToUp = Task.Run(async () =>
        {
            for (; ; )
            {
                int length;
                // Read any available prefix bytes so EOF after one byte cannot look like EOF between records.
                using (ReadResult prefix = await downChannel.ReadOwnedAsync(2, ReadBlockingMode.WaitAny, token: token))
                {
                    if (prefix.Result != IOResult.Ok)
                    {
                        logger?.LogDebug("Receiving packet length failed due to {}", prefix.Result);
                        if (prefix.Result == IOResult.Ended) await upChannel.WriteEofAsync(token);
                        return prefix.Result == IOResult.Ended;
                    }

                    if (prefix.Data.Length == 1)
                    {
                        int highByte = (prefix.Data.IsSingleSegment ? prefix.Data.FirstSpan[0] : prefix.Data.ToArray()[0]) << 8;
                        using ReadResult tail = await downChannel.ReadOwnedAsync(1, token: token);
                        if (tail.Result != IOResult.Ok) return false;
                        length = highByte | (tail.Data.IsSingleSegment ? tail.Data.FirstSpan[0] : tail.Data.ToArray()[0]);
                    }
                    else
                    {
                        ReadOnlySequence<byte> bytes = prefix.Data;
                        length = BinaryPrimitives.ReadUInt16BigEndian(bytes.IsSingleSegment ? bytes.FirstSpan : bytes.ToArray());
                    }
                }

                byte[] buffer;
                int bytesRead;
                using (ReadResult dataReadResult = await downChannel.ReadOwnedAsync(length, token: token))
                {
                    if (dataReadResult.Result != IOResult.Ok)
                    {
                        logger?.LogDebug("Receiving header failed due to {}", dataReadResult);
                        // EOF inside an encrypted record is truncation, not an orderly boundary.
                        return false;
                    }
                    buffer = new byte[length - 16];
                    ReadOnlySequence<byte> data = dataReadResult.Data;
                    bytesRead = transport.ReadMessage(data.IsSingleSegment ? data.FirstSpan : data.ToArray(), buffer);
                }

                if (bytesRead != 0)
                {
                    IOResult writeResult = await upChannel.WriteAsync(new ReadOnlySequence<byte>(buffer, 0, bytesRead), token: token);
                    if (writeResult != IOResult.Ok)
                    {
                        logger?.LogDebug("Receiving data failed due to {}", writeResult);
                        return writeResult == IOResult.Ended;
                    }
                }
            }
        });

        bool orderly = false;
        try
        {
            Task<bool> completed = await Task.WhenAny(upToDown, downToUp);
            orderly = completed.IsCompletedSuccessfully && completed.Result && !connectionToken.IsCancellationRequested;
        }
        finally
        {
            try
            {
                if (orderly)
                {
                    await upChannel.CloseAsync();
                    await downChannel.CloseAsync();
                }
                else
                {
                    await upChannel.AbortAsync();
                    await downChannel.AbortAsync();
                }
            }
            finally
            {
                lifetime.Cancel();
                await ((Task)Task.WhenAll(upToDown, downToUp)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
}
