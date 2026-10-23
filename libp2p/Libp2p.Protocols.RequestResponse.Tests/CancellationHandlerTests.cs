// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Tests;

public class CancellationHandlerTests
{
    [Test]
    public void ExistingNullHandlerCallStillCompilesAndRejectsNull() =>
        Assert.That(() => new RequestResponseProtocol<StringValue, StringValue>("/test", null!), Throws.TypeOf<ArgumentNullException>());

    [Test]
    public async Task CancellationHandlerReceivesSingleAcquiredChannelLifetimeToken()
    {
        using var lifetime = new CancellationTokenSource();
        CancellationToken received = default;
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(
            (request, _, token) => { received = token; return Task.FromResult(request); }, "/test");
        var channel = new Channel();
        var listenerChannel = new CountingChannel(channel.Reverse, lifetime.Token);
        var context = Substitute.For<ISessionContext>();
        var listen = protocol.ListenAsync(listenerChannel, context);
        try
        {
            var response = await protocol.DialAsync(channel, context, new StringValue { Value = "value" })
                .WaitAsync(TimeSpan.FromSeconds(5));
            await listen.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(received, Is.EqualTo(lifetime.Token));
            Assert.That(listenerChannel.TokenReads, Is.EqualTo(1));
            Assert.That(response.Value, Is.EqualTo("value"));
        }
        finally { await channel.CloseAsync(); await listen.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class CountingChannel(IChannel inner, CancellationToken token) : IChannel
    {
        internal int TokenReads { get; private set; }
        public CancellationToken CancellationToken { get { TokenReads++; return token; } }
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default) => inner.ReadAsync(length, blockingMode, token);
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => inner.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
    }
}
