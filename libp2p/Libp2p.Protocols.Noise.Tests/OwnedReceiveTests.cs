// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Noise;
using NUnit.Framework;
using PooledBuffer = Nethermind.Channels.PooledBuffer;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

[Parallelizable(ParallelScope.All)]
public class OwnedReceiveTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CiphertextIsReleasedBeforePublishingPlaintextOrEndingOnAuthenticationFailure(
        bool fragmentedBody, bool corruptCiphertext)
    {
        (Transport local, Transport remote) = CreateTransports();
        TestChannel down = new();
        TestChannel up = new();
        Task pump = ExchangeData(local, down, up);
        byte[] expected = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
        byte[] ciphertext = new byte[expected.Length + 16];
        int length = remote.WriteMessage(expected, ciphertext);
        byte[] prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)length);
        if (corruptCiphertext) ciphertext[^1] ^= 1;

        PoisoningPool pool = new(fragmentedBody ? 4 : 3);
        List<PooledBuffer> owners = [];
        try
        {
            IChannel writer = down.Reverse();
            owners.Add(await WriteOwnedAsync(writer, prefix.AsMemory(0, 1), pool));
            owners.Add(await WriteOwnedAsync(writer, prefix.AsMemory(1, 1), pool));
            if (fragmentedBody)
            {
                owners.Add(await WriteOwnedAsync(writer, ciphertext.AsMemory(0, 7), pool));
                owners.Add(await WriteOwnedAsync(writer, ciphertext.AsMemory(7), pool));
            }
            else
            {
                owners.Add(await WriteOwnedAsync(writer, ciphertext, pool));
            }

            // The plaintext consumer has not read yet: decrypting must release the
            // ciphertext before waiting for that consumer, including on failure.
            await pool.AllReturned.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(owners.All(owner => owner.RefCount == 0), Is.True);
            if (corruptCiphertext)
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                using ReadResult read = await up.Reverse().ReadOwnedAsync(expected.Length);
                Assert.That(read.OrThrow().ToArray(), Is.EqualTo(expected));
            }
        }
        finally
        {
            await down.CloseAsync();
            await up.CloseAsync();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task PlaintextIsReleasedBeforeWaitingForCiphertextConsumer()
    {
        (Transport local, Transport remote) = CreateTransports();
        TestChannel down = new();
        TestChannel up = new();
        Task pump = ExchangeData(local, down, up);
        byte[] expected = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
        PoisoningPool pool = new(1);
        try
        {
            PooledBuffer owner = await WriteOwnedAsync(up.Reverse(), expected, pool);
            await pool.AllReturned.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(owner.RefCount, Is.Zero);

            IChannel reader = down.Reverse();
            int length;
            using (ReadResult prefix = await reader.ReadOwnedAsync(2))
            {
                length = BinaryPrimitives.ReadUInt16BigEndian(prefix.OrThrow().FirstSpan);
            }
            using ReadResult ciphertext = await reader.ReadOwnedAsync(length);
            byte[] actual = new byte[expected.Length];
            int bytesRead = remote.ReadMessage(ciphertext.OrThrow().FirstSpan, actual);
            Assert.That(bytesRead, Is.EqualTo(expected.Length));
            Assert.That(actual, Is.EqualTo(expected));
        }
        finally
        {
            await down.CloseAsync();
            await up.CloseAsync();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<PooledBuffer> WriteOwnedAsync(IChannel writer, ReadOnlyMemory<byte> bytes, PoisoningPool pool)
    {
        PooledBuffer owner = PooledBuffer.Rent(bytes.Length, pool);
        bytes.CopyTo(owner.Memory);
        ValueTask<IOResult> write;
        try
        {
            write = writer.WriteAsync(owner, bytes.Length);
        }
        finally
        {
            owner.Dispose();
        }
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        return owner;
    }

    private static Task ExchangeData(Transport transport, IChannel down, IChannel up)
    {
        MethodInfo method = typeof(NoiseProtocol).GetMethod("ExchangeData", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Task)method.Invoke(null, [transport, down, up, null])!;
    }

    private static (Transport Local, Transport Remote) CreateTransports()
    {
        Protocol protocol = new(HandshakePattern.XX, CipherFunction.ChaChaPoly, HashFunction.Sha256);
        using HandshakeState initiator = protocol.Create(true, s: KeyPair.Generate().PrivateKey);
        using HandshakeState responder = protocol.Create(false, s: KeyPair.Generate().PrivateKey);
        byte[] message = new byte[Protocol.MaxMessageLength];
        byte[] payload = new byte[Protocol.MaxMessageLength];
        var first = initiator.WriteMessage(null, message);
        responder.ReadMessage(message.AsSpan(0, first.BytesWritten), payload);
        var second = responder.WriteMessage(null, message);
        initiator.ReadMessage(message.AsSpan(0, second.BytesWritten), payload);
        var third = initiator.WriteMessage(null, message);
        var last = responder.ReadMessage(message.AsSpan(0, third.BytesWritten), payload);
        return (third.Transport, last.Transport);
    }

    private sealed class PoisoningPool(int expectedReturns) : ArrayPool<byte>
    {
        private readonly TaskCompletionSource _allReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _returns;

        public Task AllReturned => _allReturned.Task;

        public override byte[] Rent(int minimumLength) => new byte[minimumLength];

        public override void Return(byte[] array, bool clearArray = false)
        {
            array.AsSpan().Fill(0xdd);
            if (Interlocked.Increment(ref _returns) == expectedReturns) _allReturned.TrySetResult();
        }
    }
}
