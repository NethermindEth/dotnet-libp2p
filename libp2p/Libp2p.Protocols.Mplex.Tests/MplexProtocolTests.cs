// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Mplex.Tests;

internal class MplexProtocolTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task BothPeersCanUseTheSameStreamId(bool listener)
    {
        await using MplexFixture fixture = new(listener);
        if (listener)
        {
            await fixture.OpenRemoteAsync(MplexFrame.MaxStreamId);
        }

        OpenedStream local = await fixture.OpenLocalAsync();
        WireFrame created = await fixture.ReadFrameAsync();
        Assert.That(created.Header.Flag, Is.EqualTo(MplexFlag.NewStream));
        ulong id = created.Header.StreamId;
        OpenedStream remote = await fixture.OpenRemoteAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(local.Options.ModeOverride, Is.EqualTo(UpgradeModeOverride.Dial));
            Assert.That(remote.Options.ModeOverride, Is.EqualTo(UpgradeModeOverride.Listen));
        });

        await fixture.SendFrameAsync(id, MplexFlag.MessageReceiver, [1, 2, 3]);
        await fixture.SendFrameAsync(id, MplexFlag.MessageInitiator, [4, 5, 6]);
        Assert.That(await fixture.ReadBytesAsync(local.Application, 3), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(await fixture.ReadBytesAsync(remote.Application, 3), Is.EqualTo(new byte[] { 4, 5, 6 }));

        Task<IOResult> localWrite = fixture.WriteAsync(local.Application, [7, 8]);
        Task<IOResult> remoteWrite = fixture.WriteAsync(remote.Application, [9, 10]);
        WireFrame first = await fixture.ReadFrameAsync();
        WireFrame second = await fixture.ReadFrameAsync();
        Assert.Multiple(() =>
        {
            Assert.That(new[] { first.Header.Flag, second.Header.Flag },
                Is.EquivalentTo(new[] { MplexFlag.MessageInitiator, MplexFlag.MessageReceiver }));
            Assert.That(first.Header.StreamId, Is.EqualTo(id));
            Assert.That(second.Header.StreamId, Is.EqualTo(id));
            Assert.That((first.Header.Flag == MplexFlag.MessageInitiator ? first : second).Body,
                Is.EqualTo(new byte[] { 7, 8 }));
            Assert.That((first.Header.Flag == MplexFlag.MessageReceiver ? first : second).Body,
                Is.EqualTo(new byte[] { 9, 10 }));
        });
        Assert.That(await localWrite.WaitAsync(fixture.Token), Is.EqualTo(IOResult.Ok));
        Assert.That(await remoteWrite.WaitAsync(fixture.Token), Is.EqualTo(IOResult.Ok));
    }

    [Test]
    public async Task RemoteHalfCloseDeliversQueuedDataBeforeEofAndAllowsAReply()
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenRemoteAsync(7);
        byte[] first = CreatePayload(8192, 11);
        byte[] second = CreatePayload(32768, 29);
        await fixture.SendFrameAsync(7, MplexFlag.MessageInitiator, first);
        await fixture.SendFrameAsync(7, MplexFlag.MessageInitiator, second);
        await fixture.SendFrameAsync(7, MplexFlag.CloseInitiator);

        Assert.That(await fixture.ReadBytesAsync(stream.Application, first.Length + second.Length),
            Is.EqualTo(first.Concat(second).ToArray()));
        Assert.That((await stream.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Ended));

        Task<IOResult> reply = fixture.WriteAsync(stream.Application, [31, 32]);
        WireFrame frame = await fixture.ReadFrameAsync();
        Assert.Multiple(() =>
        {
            Assert.That(frame.Header, Is.EqualTo(new MplexFrame(7, MplexFlag.MessageReceiver, 2)));
            Assert.That(frame.Body, Is.EqualTo(new byte[] { 31, 32 }));
        });
        Assert.That(await reply.WaitAsync(fixture.Token), Is.EqualTo(IOResult.Ok));
        Assert.That(await stream.Application.WriteEofAsync(fixture.Token), Is.EqualTo(IOResult.Ok));
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(7, MplexFlag.CloseReceiver, 0)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RemoteResetAbortsUnreadDataIncludingAfterLocalEof(bool localEof)
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenRemoteAsync(3);
        if (localEof)
        {
            await stream.Application.WriteEofAsync(fixture.Token);
            Assert.That((await fixture.ReadFrameAsync()).Header.Flag, Is.EqualTo(MplexFlag.CloseReceiver));
        }

        await fixture.SendFrameAsync(3, MplexFlag.MessageInitiator, [1, 2, 3]);
        await fixture.SendFrameAsync(3, MplexFlag.ResetInitiator);
        await fixture.AwaitApplicationCompletionAsync(stream.Application);
        Assert.That((await stream.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(await stream.Application.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 4 }), fixture.Token),
            Is.AnyOf(IOResult.Ended, IOResult.Aborted));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task LocalAbortSendsResetIncludingAfterLocalEof(bool localEof, bool pendingDelivery)
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenRemoteAsync(4);
        if (localEof)
        {
            await stream.Application.WriteEofAsync(fixture.Token);
            Assert.That((await fixture.ReadFrameAsync()).Header.Flag, Is.EqualTo(MplexFlag.CloseReceiver));
        }

        if (pendingDelivery)
        {
            await fixture.SendFrameAsync(4, MplexFlag.MessageInitiator, new byte[8192]);
            await fixture.ReadBytesAsync(stream.Application, 1);
        }

        await stream.Application.AbortAsync();
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(4, MplexFlag.ResetReceiver, 0)));
    }

    [Test]
    public async Task UnknownAndRetiredFramesAreConsumedWithoutLosingTheNextStream()
    {
        await using MplexFixture fixture = new();
        await fixture.SendFrameAsync(99, MplexFlag.MessageInitiator, [10, 11, 12]);
        await fixture.SendFrameAsync(99, MplexFlag.CloseInitiator);
        await fixture.SendFrameAsync(99, MplexFlag.ResetInitiator);

        OpenedStream retired = await fixture.OpenRemoteAsync(5);
        await fixture.SendFrameAsync(5, MplexFlag.ResetInitiator);
        await fixture.AwaitApplicationCompletionAsync(retired.Application);
        Assert.That((await retired.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Aborted));
        await fixture.SendFrameAsync(5, MplexFlag.MessageInitiator, [13, 14]);
        await fixture.SendFrameAsync(5, MplexFlag.CloseInitiator);
        await fixture.SendFrameAsync(5, MplexFlag.ResetInitiator);

        OpenedStream next = await fixture.OpenRemoteAsync(6);
        await fixture.SendFrameAsync(6, MplexFlag.MessageInitiator, [15, 16]);
        Assert.That(await fixture.ReadBytesAsync(next.Application, 2), Is.EqualTo(new byte[] { 15, 16 }));
        Assert.That(fixture.Completion.IsCompleted, Is.False);
    }

    [Test]
    public async Task RemoteStreamIdsCanArriveOutOfOrder()
    {
        await using MplexFixture fixture = new();
        OpenedStream first = await fixture.OpenRemoteAsync(37);
        OpenedStream second = await fixture.OpenRemoteAsync(2);
        OpenedStream third = await fixture.OpenRemoteAsync(29);
        await fixture.SendFrameAsync(2, MplexFlag.MessageInitiator, [2]);
        await fixture.SendFrameAsync(29, MplexFlag.MessageInitiator, [29]);
        await fixture.SendFrameAsync(37, MplexFlag.MessageInitiator, [37]);
        Assert.That(await fixture.ReadBytesAsync(first.Application, 1), Is.EqualTo(new byte[] { 37 }));
        Assert.That(await fixture.ReadBytesAsync(second.Application, 1), Is.EqualTo(new byte[] { 2 }));
        Assert.That(await fixture.ReadBytesAsync(third.Application, 1), Is.EqualTo(new byte[] { 29 }));
    }

    [Test]
    public async Task DuplicateActiveNewStreamTerminatesTheConnection()
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenRemoteAsync(8);
        await fixture.SendFrameAsync(8, MplexFlag.NewStream);
        await fixture.ObserveCompletionAsync();
        Assert.That((await stream.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(fixture.UpgradeCount, Is.EqualTo(1));
    }

    [TestCase("0700")]
    [TestCase("02818040")]
    public async Task MalformedFrameTerminatesTheConnectionAndAbortsActiveStreams(string hex)
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenRemoteAsync(1);
        await fixture.SendBytesAsync(Convert.FromHexString(hex));
        await fixture.ObserveCompletionAsync();
        Assert.That((await stream.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [TestCase("80")]
    [TestCase("0A0301")]
    public async Task TruncatedFrameTerminatesTheConnectionAndAbortsActiveStreams(string hex)
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenRemoteAsync(1);
        await fixture.SendBytesAsync(Convert.FromHexString(hex));
        await fixture.Remote.WriteEofAsync(fixture.Token);
        await fixture.ObserveCompletionAsync();
        Assert.That((await stream.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task DataAfterRemoteCloseResetsOnlyThatStream()
    {
        await using MplexFixture fixture = new();
        OpenedStream closed = await fixture.OpenRemoteAsync(9);
        await fixture.SendFrameAsync(9, MplexFlag.CloseInitiator);
        Assert.That((await closed.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Ended));
        await fixture.SendFrameAsync(9, MplexFlag.MessageInitiator, [1]);
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(9, MplexFlag.ResetReceiver, 0)));

        OpenedStream next = await fixture.OpenRemoteAsync(10);
        await fixture.SendFrameAsync(10, MplexFlag.MessageInitiator, [2]);
        Assert.That(await fixture.ReadBytesAsync(next.Application, 1), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public async Task LargeApplicationWriteIsSplitIntoBoundedOrderedFrames()
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenLocalAsync();
        ulong id = (await fixture.ReadFrameAsync()).Header.StreamId;
        byte[] payload = CreatePayload(MplexFrame.MaxPayloadLength + 12345, 17);
        Task<IOResult> writing = fixture.WriteAsync(stream.Application, payload);
        using MemoryStream received = new();
        int frameCount = 0;
        while (received.Length < payload.Length)
        {
            WireFrame frame = await fixture.ReadFrameAsync();
            Assert.Multiple(() =>
            {
                Assert.That(frame.Header.StreamId, Is.EqualTo(id));
                Assert.That(frame.Header.Flag, Is.EqualTo(MplexFlag.MessageInitiator));
                Assert.That(frame.Header.Length, Is.InRange(1, MplexProtocol.MaxOutgoingFrameLength));
            });
            received.Write(frame.Body);
            frameCount++;
        }

        Assert.That(await writing.WaitAsync(fixture.Token), Is.EqualTo(IOResult.Ok));
        Assert.That(received.ToArray(), Is.EqualTo(payload));
        Assert.That(frameCount, Is.GreaterThan(64));
    }

    [Test]
    public async Task FragmentedTransportWritesDeliverMoreThanOneMiBInOrder()
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenRemoteAsync(11);
        byte[] payload = CreatePayload(MplexFrame.MaxPayloadLength + 1031, 43);
        Task sending = fixture.Track(SendFragmentsAsync());
        Assert.That(await fixture.ReadBytesAsync(stream.Application, payload.Length), Is.EqualTo(payload));
        await sending.WaitAsync(fixture.Token);

        async Task SendFragmentsAsync()
        {
            const int chunkLength = 65537;
            for (int offset = 0; offset < payload.Length; offset += chunkLength)
            {
                int length = Math.Min(chunkLength, payload.Length - offset);
                byte[] frame = new MplexFrame(11, MplexFlag.MessageInitiator, length)
                    .Encode(new ReadOnlySequence<byte>(payload.AsMemory(offset, length)));
                await fixture.SendBytesAsync(frame.AsMemory(0, 1));
                await fixture.SendBytesAsync(frame.AsMemory(1, 2));
                await fixture.SendBytesAsync(frame.AsMemory(3));
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnreadStreamIsResetAtItsBufferLimitWhileAnotherStreamProgresses(bool byteLimit)
    {
        await using MplexFixture fixture = new();
        OpenedStream blocked = await fixture.OpenRemoteAsync(12);
        OpenedStream active = await fixture.OpenRemoteAsync(13);
        byte[] chunk = new byte[byteLimit ? MplexFrame.MaxPayloadLength : 1];
        int frameCount = byteLimit
            ? MplexProtocol.MaxBufferedBytesPerStream / chunk.Length + 1
            : MplexProtocol.MaxQueuedFrames + 2;
        for (int i = 0; i < frameCount; i++)
        {
            await fixture.SendFrameAsync(12, MplexFlag.MessageInitiator, chunk);
        }

        await fixture.SendFrameAsync(13, MplexFlag.MessageInitiator, [21, 22]);
        Assert.That(await fixture.ReadBytesAsync(active.Application, 2), Is.EqualTo(new byte[] { 21, 22 }));
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(12, MplexFlag.ResetReceiver, 0)));
        await fixture.AwaitApplicationCompletionAsync(blocked.Application);
        Assert.That((await blocked.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(fixture.Completion.IsCompleted, Is.False);
    }

    [Test]
    public async Task AggregateUnreadBytesAreBoundedAcrossStreams()
    {
        await using MplexFixture fixture = new();
        byte[] chunk = new byte[MplexFrame.MaxPayloadLength];
        int retained = 0;
        ulong id = 20;
        while (retained < MplexProtocol.MaxBufferedBytesPerConnection - chunk.Length)
        {
            await fixture.OpenRemoteAsync(id);
            for (int i = 0; i < 3; i++)
            {
                await fixture.SendFrameAsync(id, MplexFlag.MessageInitiator, chunk);
                retained += chunk.Length;
            }
            id++;
        }

        OpenedStream rejected = await fixture.OpenRemoteAsync(id);
        await fixture.SendFrameAsync(id, MplexFlag.MessageInitiator, chunk);
        await fixture.SendFrameAsync(id, MplexFlag.MessageInitiator, chunk);
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(id, MplexFlag.ResetReceiver, 0)));
        await fixture.AwaitApplicationCompletionAsync(rejected.Application);
        Assert.That((await rejected.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Aborted));

        OpenedStream active = await fixture.OpenRemoteAsync(id + 1);
        await fixture.SendFrameAsync(id + 1, MplexFlag.MessageInitiator, [25, 26]);
        Assert.That(await fixture.ReadBytesAsync(active.Application, 2), Is.EqualTo(new byte[] { 25, 26 }));
        Assert.That(fixture.Completion.IsCompleted, Is.False);
    }

    [Test]
    public async Task StreamAdmissionIsBoundedAndExistingStreamsRemainUsable()
    {
        await using MplexFixture fixture = new();
        OpenedStream first = await fixture.OpenRemoteAsync(0);
        for (ulong id = 1; id < MplexProtocol.MaxStreams; id++)
        {
            await fixture.OpenRemoteAsync(id);
        }

        await fixture.SendFrameAsync(MplexProtocol.MaxStreams, MplexFlag.NewStream);
        Assert.That((await fixture.ReadFrameAsync()).Header,
            Is.EqualTo(new MplexFrame(MplexProtocol.MaxStreams, MplexFlag.ResetReceiver, 0)));
        await fixture.SendFrameAsync(0, MplexFlag.MessageInitiator, [27, 28]);
        Assert.That(await fixture.ReadBytesAsync(first.Application, 2), Is.EqualTo(new byte[] { 27, 28 }));
        Assert.That(fixture.UpgradeCount, Is.EqualTo(MplexProtocol.MaxStreams));
    }

    [Test]
    public async Task FullApplicationCloseRetiresItsStreamAndReleasesCapacityWithoutResettingLateData()
    {
        await using MplexFixture fixture = new();
        OpenedStream first = await fixture.OpenRemoteAsync(0);
        for (ulong id = 1; id < MplexProtocol.MaxStreams; id++)
        {
            await fixture.OpenRemoteAsync(id);
        }

        await first.Application.CloseAsync();
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(0, MplexFlag.CloseReceiver, 0)));
        await fixture.SendFrameAsync(0, MplexFlag.MessageInitiator, [1, 2, 3]);

        OpenedStream? replacement = null;
        ulong replacementId = MplexProtocol.MaxStreams;
        while (replacement is null)
        {
            fixture.Token.ThrowIfCancellationRequested();
            replacement = await fixture.TryOpenRemoteAsync(replacementId);
            if (replacement is null) replacementId++;
        }

        await fixture.SendFrameAsync(replacementId, MplexFlag.MessageInitiator, [41, 42]);
        Assert.That(await fixture.ReadBytesAsync(replacement.Application, 2), Is.EqualTo(new byte[] { 41, 42 }));
        Assert.That(fixture.UpgradeCount, Is.EqualTo(MplexProtocol.MaxStreams + 1));
        Assert.That(fixture.Completion.IsCompleted, Is.False);
    }

    [Test]
    public async Task FullApplicationCloseFlushesAPartiallyWrittenReplyBeforeRetiring()
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenRemoteAsync(0);
        byte[] payload = CreatePayload(MplexFrame.MaxPayloadLength + 1031, 53);
        Task<IOResult> writing = fixture.WriteAsync(stream.Application, payload);
        ReadResult firstHeaderByte = await fixture.Remote.ReadAsync(1, token: fixture.Token);
        Assert.That(firstHeaderByte.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(firstHeaderByte.Data.ToArray(), Is.EqualTo(new[] { (byte)MplexFlag.MessageReceiver }));
        Assert.That(await writing.WaitAsync(fixture.Token), Is.EqualTo(IOResult.Ok));
        await stream.Application.CloseAsync();

        using MemoryStream received = new();
        int firstLength = await fixture.Remote.ReadVarintAsync(fixture.Token);
        Assert.That(firstLength, Is.InRange(1, MplexProtocol.MaxOutgoingFrameLength));
        received.Write(await fixture.ReadBytesAsync(fixture.Remote, firstLength));
        while (received.Length < payload.Length)
        {
            WireFrame frame = await fixture.ReadFrameAsync();
            Assert.Multiple(() =>
            {
                Assert.That(frame.Header.StreamId, Is.Zero);
                Assert.That(frame.Header.Flag, Is.EqualTo(MplexFlag.MessageReceiver));
                Assert.That(frame.Header.Length, Is.InRange(1, MplexProtocol.MaxOutgoingFrameLength));
            });
            received.Write(frame.Body);
        }

        Assert.That(received.ToArray(), Is.EqualTo(payload));
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(0, MplexFlag.CloseReceiver, 0)));
        await fixture.SendFrameAsync(0, MplexFlag.MessageInitiator, [4, 5, 6]);

        OpenedStream next = await fixture.OpenRemoteAsync(1);
        Task<IOResult> nextWrite = fixture.WriteAsync(next.Application, [43, 44]);
        WireFrame nextReply = await fixture.ReadFrameAsync();
        Assert.Multiple(() =>
        {
            Assert.That(nextReply.Header, Is.EqualTo(new MplexFrame(1, MplexFlag.MessageReceiver, 2)));
            Assert.That(nextReply.Body, Is.EqualTo(new byte[] { 43, 44 }));
        });
        Assert.That(await nextWrite.WaitAsync(fixture.Token), Is.EqualTo(IOResult.Ok));
        Assert.That(fixture.Completion.IsCompleted, Is.False);
    }

    [Test]
    public async Task SessionCancellationUnblocksAPartiallyConsumedApplicationWrite()
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenRemoteAsync(14);
        await fixture.SendFrameAsync(14, MplexFlag.MessageInitiator, new byte[8192]);
        Assert.That(await fixture.ReadBytesAsync(stream.Application, 1), Is.EqualTo(new byte[] { 0 }));

        fixture.CancelSession();
        await fixture.ObserveCompletionAsync();
        Assert.That((await stream.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task SessionCancellationUnblocksAPartiallyConsumedTransportWrite()
    {
        await using MplexFixture fixture = new();
        OpenedStream stream = await fixture.OpenLocalAsync();
        await fixture.ReadFrameAsync();
        Task<IOResult> writing = fixture.WriteAsync(stream.Application, new byte[8192]);
        Assert.That((await fixture.Remote.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Ok));

        fixture.CancelSession();
        await fixture.ObserveCompletionAsync();
        await writing.WaitAsync(fixture.Token);
    }

    [Test]
    public async Task CanceledQueuedRequestDoesNotOpenAStream()
    {
        await using MplexFixture fixture = new();
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Enqueue(new UpgradeOptions { CancellationToken = canceled.Token, CompletionSource = completion });
        await fixture.OpenLocalAsync();
        Assert.That((await fixture.ReadFrameAsync()).Header.Flag, Is.EqualTo(MplexFlag.NewStream));
        Exception? failure = await CaptureFailureAsync(completion.Task.WaitAsync(fixture.Token));
        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(completion.Task.IsCanceled, Is.True);
            Assert.That(fixture.UpgradeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task LiveRequestCancellationResetsOnlyItsStreamAndPreservesUpgradeOptions()
    {
        await using MplexFixture fixture = new();
        using CancellationTokenSource canceled = new();
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IProtocol selected = Substitute.For<IProtocol>();
        selected.Id.Returns("/mplex-test/1.0.0");
        object argument = new();
        OpenedStream stream = await fixture.OpenLocalAsync(new UpgradeOptions
        {
            SelectedProtocol = selected,
            CancellationToken = canceled.Token,
            CompletionSource = completion,
            Argument = argument,
        });
        WireFrame created = await fixture.ReadFrameAsync();
        Assert.Multiple(() =>
        {
            Assert.That(stream.Options.SelectedProtocol, Is.SameAs(selected));
            Assert.That(stream.Options.CompletionSource, Is.SameAs(completion));
            Assert.That(stream.Options.Argument, Is.SameAs(argument));
            Assert.That(stream.Options.CancellationToken, Is.EqualTo(canceled.Token));
            Assert.That(stream.Options.ModeOverride, Is.EqualTo(UpgradeModeOverride.Dial));
        });

        canceled.Cancel();
        Assert.That((await fixture.ReadFrameAsync()).Header,
            Is.EqualTo(new MplexFrame(created.Header.StreamId, MplexFlag.ResetInitiator, 0)));
        await fixture.AwaitApplicationCompletionAsync(stream.Application);
        Assert.That((await stream.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(await CaptureFailureAsync(completion.Task.WaitAsync(fixture.Token)), Is.InstanceOf<OperationCanceledException>());
        Assert.That(completion.Task.IsCanceled, Is.True);

        OpenedStream next = await fixture.OpenRemoteAsync(18);
        await fixture.SendFrameAsync(18, MplexFlag.MessageInitiator, [30]);
        Assert.That(await fixture.ReadBytesAsync(next.Application, 1), Is.EqualTo(new byte[] { 30 }));
    }

    [Test]
    public async Task SynchronousOutboundUpgradeFailureFaultsTheRequestAndKeepsTheConnectionUsable()
    {
        await using MplexFixture fixture = new();
        InvalidOperationException expected = new("Upgrade failed synchronously.");
        fixture.FailNextUpgrade(expected);
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Enqueue(new UpgradeOptions { CompletionSource = completion });
        Assert.That(await CaptureFailureAsync(completion.Task.WaitAsync(fixture.Token)), Is.SameAs(expected));

        WireFrame reset = await fixture.ReadFrameAsync();
        Assert.That(reset.Header.Flag, Is.EqualTo(MplexFlag.ResetInitiator));
        OpenedStream next = await fixture.OpenRemoteAsync(15);
        await fixture.SendFrameAsync(15, MplexFlag.MessageInitiator, [23]);
        Assert.That(await fixture.ReadBytesAsync(next.Application, 1), Is.EqualTo(new byte[] { 23 }));
    }

    [Test]
    public async Task SynchronousInboundUpgradeFailureResetsOnlyThatStream()
    {
        await using MplexFixture fixture = new();
        fixture.FailNextUpgrade(new InvalidOperationException("Upgrade failed synchronously."));
        await fixture.SendFrameAsync(16, MplexFlag.NewStream);
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(16, MplexFlag.ResetReceiver, 0)));
        OpenedStream next = await fixture.OpenRemoteAsync(17);
        await fixture.SendFrameAsync(17, MplexFlag.MessageInitiator, [24]);
        Assert.That(await fixture.ReadBytesAsync(next.Application, 1), Is.EqualTo(new byte[] { 24 }));
    }

    private static byte[] CreatePayload(int length, int seed)
    {
        byte[] payload = new byte[length];
        new Random(seed).NextBytes(payload);
        return payload;
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed record OpenedStream(UpgradeOptions Options, IChannel Application);
    private sealed record WireFrame(MplexFrame Header, byte[] Body);

    private sealed class MplexFixture : IAsyncDisposable
    {
        private readonly TestChannel _transport = new();
        private readonly CancellationTokenSource _sessionStop = new();
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
        private readonly BlockingCollection<UpgradeOptions> _requests = new();
        private readonly System.Threading.Channels.Channel<OpenedStream> _opened =
            System.Threading.Channels.Channel.CreateUnbounded<OpenedStream>();
        private readonly ConcurrentBag<IChannel> _applications = [];
        private readonly ConcurrentBag<Task> _tasks = [];
        private Exception? _upgradeFailure;
        private int _upgradeCount;

        public MplexFixture(bool listener = false)
        {
            IConnectionContext context = Substitute.For<IConnectionContext>();
            INewSessionContext session = Substitute.For<INewSessionContext>();
            State state = new() { RemoteAddress = TestPeers.Multiaddr(2) };
            context.State.Returns(state);
            context.Id.Returns("mplex-test");
            context.UpgradeToSession().Returns(session);
            context.DisconnectAsync().Returns(_ =>
            {
                CancelSession();
                return Task.CompletedTask;
            });
            session.State.Returns(state);
            session.Id.Returns("mplex-test");
            session.Token.Returns(_sessionStop.Token);
            session.DialRequests.Returns(_requests.GetConsumingEnumerable(_sessionStop.Token));
            session.When(value => value.Dispose()).Do(_ => CancelSession());
            session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(call => Upgrade(call.ArgAt<UpgradeOptions>(0)));
            context.Upgrade(Arg.Any<UpgradeOptions>()).Returns(call => Upgrade(call.ArgAt<UpgradeOptions>(0)));
            Remote = _transport.Reverse();
            MplexProtocol protocol = new();
            Completion = listener ? protocol.ListenAsync(_transport, context) : protocol.DialAsync(_transport, context);
        }

        public IChannel Remote { get; }
        public Task Completion { get; }
        public CancellationToken Token => _deadline.Token;
        public int UpgradeCount => Volatile.Read(ref _upgradeCount);

        public void Enqueue(UpgradeOptions options) => _requests.Add(options);
        public void FailNextUpgrade(Exception exception) => _upgradeFailure = exception;

        public async Task<OpenedStream> OpenLocalAsync(UpgradeOptions? options = null)
        {
            Enqueue(options ?? new UpgradeOptions());
            return await _opened.Reader.ReadAsync(Token);
        }

        public async Task<OpenedStream> OpenRemoteAsync(ulong id)
        {
            await SendFrameAsync(id, MplexFlag.NewStream);
            return await _opened.Reader.ReadAsync(Token);
        }

        public async Task<OpenedStream?> TryOpenRemoteAsync(ulong id)
        {
            await SendFrameAsync(id, MplexFlag.NewStream);
            using CancellationTokenSource completed = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Task<OpenedStream> opening = _opened.Reader.ReadAsync(completed.Token).AsTask();
            Task<WireFrame> reply = ReadFrameAsync(completed.Token);
            try
            {
                await Task.WhenAny(opening, reply).WaitAsync(Token);
                if (opening.IsCompletedSuccessfully)
                {
                    if (reply.IsCompleted)
                    {
                        Assert.Fail($"An admitted replacement received an unexpected frame: {(await reply).Header}.");
                    }
                    return await opening;
                }

                Assert.That((await reply).Header, Is.EqualTo(new MplexFrame(id, MplexFlag.ResetReceiver, 0)));
                return null;
            }
            finally
            {
                completed.Cancel();
                await CaptureFailureAsync(opening);
                await CaptureFailureAsync(reply);
            }
        }

        public async Task SendFrameAsync(ulong id, MplexFlag flag, byte[]? payload = null)
        {
            payload ??= [];
            await SendBytesAsync(new MplexFrame(id, flag, payload.Length).Encode(new ReadOnlySequence<byte>(payload)));
        }

        public async Task SendBytesAsync(ReadOnlyMemory<byte> bytes)
        {
            Assert.That(await Remote.WriteAsync(new ReadOnlySequence<byte>(bytes), Token), Is.EqualTo(IOResult.Ok));
        }

        public Task<WireFrame> ReadFrameAsync() => ReadFrameAsync(Token);

        private async Task<WireFrame> ReadFrameAsync(CancellationToken token)
        {
            MplexFrame header = await MplexFrame.ReadAsync(Remote, token);
            return new WireFrame(header, (await Remote.ReadAsync(header.Length, token: token).OrThrow()).ToArray());
        }

        public async Task<byte[]> ReadBytesAsync(IChannel channel, int length)
            => (await channel.ReadAsync(length, token: Token).OrThrow()).ToArray();

        public async Task AwaitApplicationCompletionAsync(IChannel channel)
        {
            TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            channel.GetAwaiter().OnCompleted(() => completed.TrySetResult());
            await completed.Task.WaitAsync(Token);
        }

        public Task<IOResult> WriteAsync(IChannel channel, byte[] payload)
            => Track(channel.WriteAsync(new ReadOnlySequence<byte>(payload), Token).AsTask());

        public TTask Track<TTask>(TTask task) where TTask : Task
        {
            _tasks.Add(task);
            return task;
        }

        public void CancelSession()
        {
            _sessionStop.Cancel();
            _requests.CompleteAdding();
        }

        public async Task ObserveCompletionAsync()
        {
            await Completion.WaitAsync(Token);
        }

        public async ValueTask DisposeAsync()
        {
            CancelSession();
            await _transport.AbortAsync();
            foreach (IChannel application in _applications)
            {
                await application.AbortAsync();
            }

            await Completion.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (Task task in _tasks)
            {
                await ObserveAtTeardownAsync(task);
            }

            _requests.Dispose();
            _sessionStop.Dispose();
            _deadline.Dispose();
        }

        private IChannel Upgrade(UpgradeOptions options)
        {
            Interlocked.Increment(ref _upgradeCount);
            if (Interlocked.Exchange(ref _upgradeFailure, null) is { } failure)
            {
                throw failure;
            }

            TestChannel bridge = new();
            IChannel application = bridge.Reverse();
            _applications.Add(application);
            _opened.Writer.TryWrite(new OpenedStream(options, application));
            return bridge;
        }

        private static async Task ObserveAtTeardownAsync(Task task)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) when (task.IsCompleted)
            {
            }
        }
    }
}
