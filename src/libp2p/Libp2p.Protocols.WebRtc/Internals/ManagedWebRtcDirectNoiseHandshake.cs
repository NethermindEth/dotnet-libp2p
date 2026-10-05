// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;
using BcChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal static class ManagedWebRtcDirectNoiseHandshake
{
    private const string ProtocolName = "Noise_XX_25519_ChaChaPoly_SHA256";
    private const int PublicKeySize = 32;
    private const int TagSize = 16;

    public static async Task<(IChannel Encrypted, PublicKey RemoteKey)> HandshakeAsync(
        IChannel channel,
        Identity localIdentity,
        byte[] prologue,
        bool isInitiator,
        CancellationToken token)
    {
        ManagedNoiseHandshakeState state = new(isInitiator, prologue);
        ManagedNoiseKeyPair staticKey = ManagedNoiseKeyPair.Generate();

        PublicKey remoteKey;

        if (isInitiator)
        {
            byte[] msg0 = state.WriteInitialMessage();
            await WebRtcDirectNoiseHandshake.WriteFramedAsync(channel, msg0, msg0.Length, token);

            byte[] msg1 = await WebRtcDirectNoiseHandshake.ReadFramedAsync(channel, token);
            byte[] payload1 = state.ReadResponseMessage(msg1, staticKey);
            remoteKey = WebRtcDirectNoiseHandshake.ParseAndValidateRemoteKey(payload1, payload1.Length, state.RemoteStaticPublicKey);

            byte[] payload2 = WebRtcDirectNoiseHandshake.BuildIdentityPayload(localIdentity, staticKey.PublicKey);
            byte[] msg2 = state.WriteFinalMessage(staticKey, payload2);
            await WebRtcDirectNoiseHandshake.WriteFramedAsync(channel, msg2, msg2.Length, token);
        }
        else
        {
            byte[] msg0 = await WebRtcDirectNoiseHandshake.ReadFramedAsync(channel, token);
            state.ReadInitialMessage(msg0);

            byte[] payload1 = WebRtcDirectNoiseHandshake.BuildIdentityPayload(localIdentity, staticKey.PublicKey);
            byte[] msg1 = state.WriteResponseMessage(staticKey, payload1);
            await WebRtcDirectNoiseHandshake.WriteFramedAsync(channel, msg1, msg1.Length, token);

            byte[] msg2 = await WebRtcDirectNoiseHandshake.ReadFramedAsync(channel, token);
            byte[] payload2 = state.ReadFinalMessage(msg2);
            remoteKey = WebRtcDirectNoiseHandshake.ParseAndValidateRemoteKey(payload2, payload2.Length, state.RemoteStaticPublicKey);
        }

        (ManagedNoiseCipherState sendCipher, ManagedNoiseCipherState receiveCipher) = state.Split();
        return (new ManagedNoiseEncryptedChannel(channel, sendCipher, receiveCipher), remoteKey);
    }

    private sealed class ManagedNoiseHandshakeState
    {
        private byte[] _chainingKey;
        private byte[] _hash;
        private ManagedNoiseCipherState _cipher = new(null);
        private readonly bool _isInitiator;
        private ManagedNoiseKeyPair? _localEphemeral;
        private byte[]? _remoteEphemeral;
        private byte[]? _remoteStaticPublicKey;

        public ManagedNoiseHandshakeState(bool isInitiator, byte[] prologue)
        {
            _isInitiator = isInitiator;
            _hash = InitializeHash();
            _chainingKey = _hash.ToArray();
            MixHash(prologue);
        }

        public ReadOnlySpan<byte> RemoteStaticPublicKey => _remoteStaticPublicKey;

        public byte[] WriteInitialMessage()
        {
            _localEphemeral = ManagedNoiseKeyPair.Generate();
            MixHash(_localEphemeral.PublicKey);
            return [.. _localEphemeral.PublicKey, .. EncryptAndHash([])];
        }

        public void ReadInitialMessage(byte[] message)
        {
            if (message.Length < PublicKeySize)
            {
                throw new CryptographicException("Noise message is missing the remote ephemeral key.");
            }

            _remoteEphemeral = message[..PublicKeySize];
            MixHash(_remoteEphemeral);
            _ = DecryptAndHash(message.AsSpan(PublicKeySize));
        }

        public byte[] WriteResponseMessage(ManagedNoiseKeyPair staticKey, byte[] payload)
        {
            RequireRemoteEphemeral();

            _localEphemeral = ManagedNoiseKeyPair.Generate();
            MixHash(_localEphemeral.PublicKey);
            MixKey(_localEphemeral.Dh(_remoteEphemeral!));
            byte[] encryptedStatic = EncryptAndHash(staticKey.PublicKey);
            MixKey(staticKey.Dh(_remoteEphemeral!));
            byte[] encryptedPayload = EncryptAndHash(payload);

            return [.. _localEphemeral.PublicKey, .. encryptedStatic, .. encryptedPayload];
        }

        public byte[] ReadResponseMessage(byte[] message, ManagedNoiseKeyPair staticKey)
        {
            RequireLocalEphemeral();
            if (message.Length < PublicKeySize + PublicKeySize + TagSize)
            {
                throw new CryptographicException("Noise response message is too short.");
            }

            _remoteEphemeral = message[..PublicKeySize];
            MixHash(_remoteEphemeral);
            MixKey(_localEphemeral!.Dh(_remoteEphemeral));
            _remoteStaticPublicKey = DecryptAndHash(message.AsSpan(PublicKeySize, PublicKeySize + TagSize));
            MixKey(_localEphemeral.Dh(_remoteStaticPublicKey));
            return DecryptAndHash(message.AsSpan(PublicKeySize + PublicKeySize + TagSize));
        }

        public byte[] WriteFinalMessage(ManagedNoiseKeyPair staticKey, byte[] payload)
        {
            RequireRemoteEphemeral();

            byte[] encryptedStatic = EncryptAndHash(staticKey.PublicKey);
            MixKey(staticKey.Dh(_remoteEphemeral!));
            byte[] encryptedPayload = EncryptAndHash(payload);
            return [.. encryptedStatic, .. encryptedPayload];
        }

        public byte[] ReadFinalMessage(byte[] message)
        {
            RequireLocalEphemeral();
            if (message.Length < PublicKeySize + TagSize)
            {
                throw new CryptographicException("Noise final message is too short.");
            }

            _remoteStaticPublicKey = DecryptAndHash(message.AsSpan(0, PublicKeySize + TagSize));
            MixKey(_localEphemeral!.Dh(_remoteStaticPublicKey));
            return DecryptAndHash(message.AsSpan(PublicKeySize + TagSize));
        }

        public (ManagedNoiseCipherState Send, ManagedNoiseCipherState Receive) Split()
        {
            (byte[] tempKey1, byte[] tempKey2) = Hkdf2(_chainingKey, []);
            ManagedNoiseCipherState first = new(tempKey1);
            ManagedNoiseCipherState second = new(tempKey2);
            return _isInitiator ? (first, second) : (second, first);
        }

        private void MixHash(ReadOnlySpan<byte> data)
        {
            byte[] input = new byte[_hash.Length + data.Length];
            _hash.CopyTo(input, 0);
            data.CopyTo(input.AsSpan(_hash.Length));
            _hash = SHA256.HashData(input);
        }

        private void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
        {
            (byte[] nextChainingKey, byte[] tempKey) = Hkdf2(_chainingKey, inputKeyMaterial);
            _chainingKey = nextChainingKey;
            _cipher = new ManagedNoiseCipherState(tempKey);
        }

        private byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
        {
            byte[] ciphertext = _cipher.Encrypt(_hash, plaintext);
            MixHash(ciphertext);
            return ciphertext;
        }

        private byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
        {
            byte[] plaintext = _cipher.Decrypt(_hash, ciphertext);
            MixHash(ciphertext);
            return plaintext;
        }

        private static byte[] InitializeHash()
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes(ProtocolName);
            if (name.Length > SHA256.HashSizeInBytes)
            {
                return SHA256.HashData(name);
            }

            byte[] hash = new byte[SHA256.HashSizeInBytes];
            name.CopyTo(hash, 0);
            return hash;
        }

        private static (byte[] First, byte[] Second) Hkdf2(ReadOnlySpan<byte> chainingKey, ReadOnlySpan<byte> inputKeyMaterial)
        {
            byte[] tempKey = Hmac(chainingKey, inputKeyMaterial);
            byte[] output1 = Hmac(tempKey, [0x01]);
            byte[] output2Input = [.. output1, 0x02];
            byte[] output2 = Hmac(tempKey, output2Input);
            return (output1, output2);
        }

        private static byte[] Hmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
        {
            using HMACSHA256 hmac = new(key.ToArray());
            return hmac.ComputeHash(data.ToArray());
        }

        private void RequireLocalEphemeral()
        {
            if (_localEphemeral is null)
            {
                throw new CryptographicException("Noise local ephemeral key is missing.");
            }
        }

        private void RequireRemoteEphemeral()
        {
            if (_remoteEphemeral is null)
            {
                throw new CryptographicException("Noise remote ephemeral key is missing.");
            }
        }
    }

    private sealed class ManagedNoiseKeyPair
    {
        private readonly X25519PrivateKeyParameters _privateKey;

        private ManagedNoiseKeyPair(X25519PrivateKeyParameters privateKey, byte[] publicKey)
        {
            _privateKey = privateKey;
            PublicKey = publicKey;
        }

        public byte[] PublicKey { get; }

        public static ManagedNoiseKeyPair Generate()
        {
            X25519PrivateKeyParameters privateKey = new(new SecureRandom());
            X25519PublicKeyParameters publicKey = privateKey.GeneratePublicKey();
            byte[] publicKeyBytes = new byte[PublicKeySize];
            publicKey.Encode(publicKeyBytes, 0);
            return new ManagedNoiseKeyPair(privateKey, publicKeyBytes);
        }

        public byte[] Dh(ReadOnlySpan<byte> remotePublicKey)
        {
            X25519Agreement agreement = new();
            agreement.Init(_privateKey);
            X25519PublicKeyParameters remote = new(remotePublicKey.ToArray(), 0);
            byte[] shared = new byte[PublicKeySize];
            agreement.CalculateAgreement(remote, shared, 0);
            return shared;
        }
    }
}

internal sealed class ManagedNoiseCipherState
{
    private const int TagSize = 16;
    private readonly byte[]? _key;
    private ulong _nonce;

    public ManagedNoiseCipherState(byte[]? key)
    {
        _key = key;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
    {
        if (_key is null)
        {
            return plaintext.ToArray();
        }

        BcChaCha20Poly1305 cipher = new();
        cipher.Init(true, new AeadParameters(new KeyParameter(_key), TagSize * 8, BuildNonce(), associatedData.ToArray()));
        byte[] output = new byte[cipher.GetOutputSize(plaintext.Length)];
        int written = cipher.ProcessBytes(plaintext.ToArray(), 0, plaintext.Length, output, 0);
        written += cipher.DoFinal(output, written);
        return output[..written];
    }

    public byte[] Decrypt(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertextWithTag)
    {
        if (_key is null)
        {
            return ciphertextWithTag.ToArray();
        }

        if (ciphertextWithTag.Length < TagSize)
        {
            throw new CryptographicException("Noise ciphertext is missing the authentication tag.");
        }

        BcChaCha20Poly1305 cipher = new();
        byte[] input = ciphertextWithTag.ToArray();
        cipher.Init(false, new AeadParameters(new KeyParameter(_key), TagSize * 8, BuildNonce(), associatedData.ToArray()));
        byte[] output = new byte[cipher.GetOutputSize(input.Length)];
        int written = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        written += cipher.DoFinal(output, written);
        return output[..written];
    }

    private byte[] BuildNonce()
    {
        byte[] nonce = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), _nonce++);
        return nonce;
    }
}
