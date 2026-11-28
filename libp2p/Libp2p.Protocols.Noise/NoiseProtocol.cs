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
using PooledBuffer = Nethermind.Channels.PooledBuffer;
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

    private const int TagSize = 16;
    private const int MinimumPooledPlaintextSize = 4 * 1024;

    /// <summary>Plaintext carried by one Noise message: the transport message limit less its 16-byte tag.</summary>
    private const int MaxPlaintextPerMessage = Protocol.MaxMessageLength - TagSize;

    public async Task DialAsync(IChannel downChannel, IConnectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);

        using KeyPair clientStatic = KeyPair.Generate();
        using HandshakeState handshakeState = _protocol.Create(true, s: clientStatic.PrivateKey);

        var msg0 = EncodeHandshakeMessage(handshakeState, default);
        await downChannel.WriteAsync(msg0.Frame, context.Token).OrThrow();
        var msg1 = await ReadHandshakeMessageAsync(downChannel, handshakeState, context.Token);
        NoiseHandshakePayload? msg1Decoded = NoiseHandshakePayload.Parser.ParseFrom(msg1.Payload.Span);

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

        var msg2 = EncodeHandshakeMessage(handshakeState, payload.ToByteArray());
        using Transport transport = msg2.Transport!;
        await downChannel.WriteAsync(msg2.Frame, context.Token).OrThrow();

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

        using KeyPair serverStatic = KeyPair.Generate();
        using HandshakeState handshakeState =
            _protocol.Create(false,
                s: serverStatic.PrivateKey);

        await ReadHandshakeMessageAsync(downChannel, handshakeState, context.Token);

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

        var msg1 = EncodeHandshakeMessage(handshakeState, payload.ToByteArray());
        await downChannel.WriteAsync(msg1.Frame, context.Token).OrThrow();
        var msg2 = await ReadHandshakeMessageAsync(downChannel, handshakeState, context.Token);
        using Transport transport = msg2.Transport!;
        NoiseHandshakePayload? msg2Decoded = NoiseHandshakePayload.Parser.ParseFrom(msg2.Payload.Span);

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

    private static (ReadOnlySequence<byte> Frame, Transport? Transport) EncodeHandshakeMessage(HandshakeState handshake,
        ReadOnlySpan<byte> payload)
    {
        byte[] scratch = ArrayPool<byte>.Shared.Rent(Protocol.MaxMessageLength);
        try
        {
            var message = handshake.WriteMessage(payload, scratch);
            // Publish only the encoded frame. Readers can retain it after the handshake proceeds.
            byte[] frame = new byte[2 + message.BytesWritten];
            BinaryPrimitives.WriteUInt16BigEndian(frame, checked((ushort)message.BytesWritten));
            scratch.AsSpan(0, message.BytesWritten).CopyTo(frame.AsSpan(2));
            return (new ReadOnlySequence<byte>(frame), message.Transport);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static async ValueTask<(ReadOnlyMemory<byte> Payload, Transport? Transport)> ReadHandshakeMessageAsync(
        IReader reader, HandshakeState handshake, CancellationToken token)
    {
        int length;
        using (ReadResult prefix = await reader.ReadOwnedAsync(2, token: token))
        {
            length = ReadLengthBytes(prefix.OrThrow());
        }
        using ReadResult message = await reader.ReadOwnedAsync(length, token: token);
        ReadOnlySpan<byte> ciphertext = AsSpan(message.OrThrow());
        byte[] payload = new byte[ciphertext.Length];
        var decoded = handshake.ReadMessage(ciphertext, payload);
        return (payload.AsMemory(0, decoded.BytesRead), decoded.Transport);
    }

    private static ReadOnlySpan<byte> AsSpan(ReadOnlySequence<byte> data)
        => data.IsSingleSegment ? data.FirstSpan : data.ToArray();

    private static ValueTask<IOResult> WriteTransportMessageAsync(Transport transport, IWriter writer,
        ReadOnlySequence<byte> data, CancellationToken token)
    {
        int capacity = checked(2 + TagSize + (int)data.Length);
        using PooledBuffer? pooled = data.Length >= MinimumPooledPlaintextSize ? PooledBuffer.Rent(capacity) : null;
        Memory<byte> frame = pooled is null ? new byte[capacity] : pooled.Memory;
        int bytesWritten = transport.WriteMessage(AsSpan(data), frame.Span[2..]);
        BinaryPrimitives.WriteUInt16BigEndian(frame.Span, checked((ushort)bytesWritten));
        int frameLength = 2 + bytesWritten;
        return pooled is null
            ? writer.WriteAsync(new ReadOnlySequence<byte>(frame[..frameLength]), token)
            : writer.WriteAsync(pooled, frameLength, token: token);
    }

    private static int ReadLengthBytes(ReadOnlySequence<byte> bytes)
    {
        SequenceReader<byte> reader = new(bytes);
        return reader.TryReadBigEndian(out short length) ? (ushort)length : reader.UnreadSpan[0];
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
                ValueTask<IOResult> write;
                using (ReadResult read = await upChannel.ReadOwnedAsync(MaxPlaintextPerMessage, ReadBlockingMode.WaitAny, token))
                {
                    if (read.Result != IOResult.Ok)
                    {
                        logger?.LogDebug("End reading, due to {}", read.Result);
                        if (read.Result == IOResult.Ended) await downChannel.WriteEofAsync(token);
                        return read.Result == IOResult.Ended;
                    }
                    write = WriteTransportMessageAsync(transport, downChannel, read.Data, token);
                }
                IOResult result = await write;
                if (result != IOResult.Ok)
                {
                    logger?.LogDebug("End sending, due to {}", result);
                    return result == IOResult.Ended;
                }
            }
        });
        // DOWN -> UP
        Task<bool> downToUp = Task.Run(async () =>
        {
            for (; ; )
            {
                int length;
                bool partialPrefix;
                // Read any available prefix bytes so EOF after one byte cannot look like EOF between records.
                using (ReadResult prefix = await downChannel.ReadOwnedAsync(2, ReadBlockingMode.WaitAny, token: token))
                {
                    if (prefix.Result != IOResult.Ok)
                    {
                        logger?.LogDebug("Receiving packet length failed due to {}", prefix.Result);
                        if (prefix.Result == IOResult.Ended) await upChannel.WriteEofAsync(token);
                        return prefix.Result == IOResult.Ended;
                    }

                    length = ReadLengthBytes(prefix.Data);
                    partialPrefix = prefix.Data.Length == 1;
                }
                if (partialPrefix)
                {
                    using ReadResult tail = await downChannel.ReadOwnedAsync(1, token: token);
                    if (tail.Result != IOResult.Ok) return false;
                    length = (length << 8) | ReadLengthBytes(tail.Data);
                }

                if (length < TagSize)
                {
                    throw new Libp2pException("Noise transport record is shorter than its authentication tag.");
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
                    buffer = new byte[length - TagSize];
                    bytesRead = transport.ReadMessage(AsSpan(dataReadResult.Data), buffer);
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

        Task pumps = Task.WhenAll((Task)upToDown, downToUp);
        bool orderly = false;
        try
        {
            Task<bool> completed = await Task.WhenAny(upToDown, downToUp);
            orderly = await completed && !connectionToken.IsCancellationRequested;
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
                await pumps.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        // A sibling can fault during shutdown even if the first pump ended normally.
        await pumps;
    }
}
