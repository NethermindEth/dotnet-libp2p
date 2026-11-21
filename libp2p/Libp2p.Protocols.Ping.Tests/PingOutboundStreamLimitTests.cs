// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using System.Runtime.CompilerServices;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Ping.Tests;

/// <summary>
/// "The dialing peer MUST NOT keep more than one outbound stream for the ping protocol per peer"
/// — https://github.com/libp2p/specs/blob/master/ping/ping.md (#250). Overlapping pings on one
/// session therefore run one at a time. They wait rather than failing, because LivenessProbe turns
/// any exception from a probe into a session disconnect.
/// </summary>
[TestFixture]
[CancelAfter(30_000)]
public class PingOutboundStreamLimitTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Two distinct, valid peer ids. RemotePeerId is derived from the address, so the suffix is what
    // the per-peer gate keys on.
    private static readonly string[] PeerIds =
    [
        "12D3KooWAfPDpPRRRBrmqy9is2zjU5srQ4hKuZitiGmh4NTTpS2d",
        "12D3KooWAfPDpPRRRBrmqy9is2zjU5srQ4hKuZitiGmh4NTTpS2d",
        "12D3KooWJwYWjPLsTKiZ7eMjDagCZh9Fqt1UERLKoPb5QQNByrAF",
    ];

    private static ISessionContext ContextFor(int peer)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = $"/ip4/127.0.0.1/tcp/{4000 + peer}/p2p/{PeerIds[peer]}" });
        return context;
    }

    /// <summary>
    /// The refusal has to be distinguishable: LivenessProbe disconnects the session on anything
    /// else, so a plain exception here would turn our own concurrency into a lost connection.
    /// </summary>
    [Test]
    public async Task ASecondPingOnOneSessionIsRefusedAsContentionNotFailure()
    {
        PingProtocol protocol = new();
        ISessionContext context = ContextFor(1);
        GatedEcho held = new();

        Task first = protocol.DialAsync(held, context);
        await held.Reached.Task.WaitAsync(Timeout);

        ProtocolBusyException? busy = Assert.ThrowsAsync<ProtocolBusyException>(
            async () => await protocol.DialAsync(new GatedEcho { Open = true }, context));

        held.Release.TrySetResult();
        await first.WaitAsync(Timeout);

        Assert.That(busy!.Message, Does.Contain("already in flight"));
    }

    [Test]
    public async Task TheNextPingOnThatSessionSucceedsOnceTheFirstFinishes()
    {
        PingProtocol protocol = new();
        ISessionContext context = ContextFor(1);
        GatedEcho held = new();

        Task first = protocol.DialAsync(held, context);
        await held.Reached.Task.WaitAsync(Timeout);
        held.Release.TrySetResult();
        await first.WaitAsync(Timeout);

        await protocol.DialAsync(new GatedEcho { Open = true }, context).WaitAsync(Timeout);
    }

    /// <summary>A failed ping must not leave the session's gate held.</summary>
    [Test]
    public async Task AFailedPingReleasesTheSessionForTheNextOne()
    {
        PingProtocol protocol = new();
        ISessionContext context = ContextFor(1);

        Assert.ThrowsAsync<IOException>(async () => await protocol.DialAsync(new FailingChannel(), context));

        await protocol.DialAsync(new GatedEcho { Open = true }, context).WaitAsync(Timeout);
    }

    [Test]
    public async Task OneSessionInFlightDoesNotBlockAnother()
    {
        PingProtocol protocol = new();
        GatedEcho held = new();

        Task first = protocol.DialAsync(held, ContextFor(1));
        await held.Reached.Task.WaitAsync(Timeout);

        await protocol.DialAsync(new GatedEcho { Open = true }, ContextFor(2)).WaitAsync(Timeout);

        held.Release.TrySetResult();
        await first.WaitAsync(Timeout);
    }

    /// <summary>Echoes the payload, optionally only after the test lets it.</summary>
    private sealed class GatedEcho : IChannel
    {
        private byte[] _payload = [];
        public bool Open { get; init; }
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            _payload = bytes.ToArray();
            return ValueTask.FromResult(IOResult.Ok);
        }

        public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            if (!Open)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(Timeout, token);
            }

            return new ReadResult { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>(_payload) };
        }

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => ValueTask.FromResult(IOResult.Ok);
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public TaskAwaiter GetAwaiter() => Task.CompletedTask.GetAwaiter();
    }

    private sealed class FailingChannel : IChannel
    {
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
            => throw new IOException("write failed");
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
            => throw new IOException("read failed");
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => ValueTask.FromResult(IOResult.Ok);
        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public ValueTask AbortAsync() => ValueTask.CompletedTask;
        public TaskAwaiter GetAwaiter() => Task.CompletedTask.GetAwaiter();
    }
}
