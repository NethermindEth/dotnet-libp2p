// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal enum WebRtcDirectSignalType
{
    Offer,
    Answer,
}

internal sealed class WebRtcDirectReplayWindow
{
    private readonly ConcurrentDictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly long _maxAgeMs;
    private readonly int _maxEntries;
    private int _cleanupInProgress;

    public WebRtcDirectReplayWindow(long maxAgeMs = 120_000, int maxEntries = 16_384)
    {
        _maxAgeMs = maxAgeMs;
        _maxEntries = maxEntries;
    }

    public bool TryAccept(string key, long timestampMs, long nowMs)
    {
        if (!_seen.TryAdd(key, timestampMs))
        {
            return false;
        }

        if (_seen.Count > _maxEntries)
        {
            Cleanup(nowMs);
        }

        return true;
    }

    private void Cleanup(long nowMs)
    {
        if (Interlocked.Exchange(ref _cleanupInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            foreach ((string key, long ts) in _seen)
            {
                if (nowMs - ts > _maxAgeMs)
                {
                    _seen.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _cleanupInProgress, 0);
        }
    }
}

internal static class WebRtcDirectSignaling
{
    private const int Version = 1;
    private const long MaxClockSkewMs = 60_000;
    private const int NonceBytes = 16;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string NewSessionId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceBytes)).ToLowerInvariant();

    public static string BuildSignedPayload(WebRtcDirectSignalType type, Identity signer, string sessionId, string sdp)
    {
        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceBytes)).ToLowerInvariant();
        byte[] payloadBytes = Encoding.UTF8.GetBytes(sdp);
        string payloadHashHex = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();

        SignalingEnvelope envelope = new()
        {
            Version = Version,
            Type = type.ToString().ToUpperInvariant(),
            SessionId = sessionId,
            TimestampMs = timestampMs,
            Nonce = nonce,
            Payload = sdp,
            SignerPublicKey = Convert.ToBase64String(signer.PublicKey.ToByteArray()),
            Signature = Convert.ToBase64String(signer.Sign(SigningBytes(type, sessionId, timestampMs, nonce, payloadHashHex))),
        };

        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static (string SessionId, string Sdp, Identity SignerIdentity) ParseAndValidate(
        string signedPayload,
        WebRtcDirectSignalType expectedType,
        string? expectedSessionId,
        WebRtcDirectReplayWindow replayWindow)
    {
        SignalingEnvelope envelope = JsonSerializer.Deserialize<SignalingEnvelope>(signedPayload, JsonOptions)
            ?? throw new FormatException("Signaling envelope is null.");

        if (envelope.Version != Version)
        {
            throw new FormatException($"Unsupported signaling envelope version: {envelope.Version}.");
        }

        if (!string.Equals(envelope.Type, expectedType.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Unexpected signaling envelope type: {envelope.Type}.");
        }

        if (!string.IsNullOrWhiteSpace(expectedSessionId) &&
            !string.Equals(envelope.SessionId, expectedSessionId, StringComparison.Ordinal))
        {
            throw new FormatException("Signaling envelope session id mismatch.");
        }

        if (string.IsNullOrWhiteSpace(envelope.Payload))
        {
            throw new FormatException("Signaling envelope payload is empty.");
        }

        if (string.IsNullOrWhiteSpace(envelope.SignerPublicKey) || string.IsNullOrWhiteSpace(envelope.Signature))
        {
            throw new FormatException("Signaling envelope is missing signer material.");
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Math.Abs(nowMs - envelope.TimestampMs) > MaxClockSkewMs)
        {
            throw new FormatException("Signaling envelope timestamp is outside permitted clock skew.");
        }

        byte[] payloadBytes = Encoding.UTF8.GetBytes(envelope.Payload);
        string payloadHashHex = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();

        PublicKey signerPublicKey = PublicKey.Parser.ParseFrom(Convert.FromBase64String(envelope.SignerPublicKey));
        Identity signerIdentity = new(signerPublicKey);

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            throw new CryptographicException("Invalid signaling envelope signature.");
        }

        byte[] signingBytes = SigningBytes(expectedType, envelope.SessionId, envelope.TimestampMs, envelope.Nonce, payloadHashHex);
        if (!signerIdentity.VerifySignature(signingBytes, signature))
        {
            throw new CryptographicException("Invalid signaling envelope signature.");
        }

        string replayKey = $"{envelope.Type}:{envelope.SessionId}:{envelope.Nonce}:{Convert.ToHexString(SHA256.HashData(signature)).ToLowerInvariant()}";
        if (!replayWindow.TryAccept(replayKey, envelope.TimestampMs, nowMs))
        {
            throw new CryptographicException("Replay detected in signaling envelope.");
        }

        return (envelope.SessionId, envelope.Payload, signerIdentity);
    }

    private static byte[] SigningBytes(WebRtcDirectSignalType type, string sessionId, long timestampMs, string nonce, string payloadHashHex)
    {
        string canonical = string.Join('\n',
            Version,
            type.ToString().ToUpperInvariant(),
            sessionId,
            timestampMs,
            nonce,
            payloadHashHex);
        return Encoding.UTF8.GetBytes(canonical);
    }

    private sealed class SignalingEnvelope
    {
        public int Version { get; init; }
        public string Type { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public long TimestampMs { get; init; }
        public string Nonce { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
        public string SignerPublicKey { get; init; } = string.Empty;
        public string Signature { get; init; } = string.Empty;
    }
}
