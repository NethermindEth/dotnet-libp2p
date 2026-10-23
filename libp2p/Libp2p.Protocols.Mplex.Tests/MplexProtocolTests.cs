// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
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
        await fixture.SendFrameAsync(8, MplexFlag.NewStream, expectDisconnect: true);
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
        await fixture.SendBytesAsync(Convert.FromHexString(hex), expectDisconnect: true);
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
        Assert.That(fixture.Log.Warnings, Is.Empty);
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

    [TestCase(64)]
    [TestCase(65)]
    public async Task StreamResetWhileOutgoingQueueIsFullKeepsTheConnectionAlive(int backloggedStreams)
    {
        await using MplexFixture fixture = new();
        await fixture.BacklogAsync(backloggedStreams);

        OpenedStream victim = await fixture.OpenRemoteAsync(100);
        OpenedStream survivor = await fixture.OpenRemoteAsync(101);
        await fixture.SendFrameAsync(100, MplexFlag.CloseInitiator);
        Assert.That((await victim.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Ended));
        await fixture.SendFrameAsync(100, MplexFlag.MessageInitiator, [1]);
        await fixture.AwaitApplicationCompletionAsync(victim.Application);
        await fixture.SendFrameAsync(101, MplexFlag.MessageInitiator, [9]);
        Assert.That(await fixture.ReadBytesAsync(survivor.Application, 1), Is.EqualTo(new byte[] { 9 }));
        Assert.That(fixture.Completion.IsCompleted, Is.False);
        await fixture.WaitUntilAsync(() => fixture.Connection.RetiredStreamCount == 1);
        Assert.That(fixture.Connection.PendingResetCount, Is.EqualTo(1));
        // The already-started frame must finish before the reset can be sent.
        Assert.That(await fixture.ReadBytesAsync(fixture.Remote, 1), Is.EqualTo(new byte[] { 42 }));
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(100, MplexFlag.ResetReceiver, 0)));
        for (int i = 1; i < backloggedStreams; i++)
            Assert.That((await fixture.ReadFrameAsync()).Header.Flag, Is.EqualTo(MplexFlag.MessageReceiver));
        await fixture.WaitUntilAsync(() => fixture.Connection.PendingResetCount == 0 && fixture.Connection.RetiredStreamCount == 0);
    }

    [TestCase(false, false, "close")]
    [TestCase(true, false, "close")]
    [TestCase(false, true, "close")]
    [TestCase(true, true, "close")]
    [TestCase(false, false, "abort")]
    [TestCase(true, false, "abort")]
    [TestCase(false, true, "abort")]
    [TestCase(true, true, "abort")]
    [TestCase(false, true, "cancel")]
    [TestCase(true, true, "cancel")]
    [TestCase(false, false, "buffer")]
    [TestCase(true, true, "buffer")]
    [TestCase(false, true, "bytes")]
    [TestCase(true, false, "bytes")]
    public async Task ResetCausesRemainIsolatedUnderDataSaturation(bool listener, bool local, string cause)
    {
        await using MplexFixture fixture = new(listener);
        if (listener) await fixture.OpenRemoteAsync(MplexFrame.MaxStreamId);
        using CancellationTokenSource cancellation = new();
        OpenedStream victim = local
            ? await fixture.OpenLocalAsync(new UpgradeOptions { CancellationToken = cancellation.Token })
            : await fixture.OpenRemoteAsync(500);
        ulong id = local ? (await fixture.ReadFrameAsync()).Header.StreamId : 500;
        MplexFlag message = local ? MplexFlag.MessageReceiver : MplexFlag.MessageInitiator;
        MplexFlag close = local ? MplexFlag.CloseReceiver : MplexFlag.CloseInitiator;
        MplexFlag reset = local ? MplexFlag.ResetInitiator : MplexFlag.ResetReceiver;
        // In local cases the backlogged remote stream with ID zero shares the numeric ID with the victim.
        OpenedStream[] backlog = await fixture.BacklogAsync(65);
        switch (cause)
        {
            case "close":
                await fixture.SendFrameAsync(id, close);
                Assert.That((await victim.Application.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Ended));
                await fixture.SendFrameAsync(id, message, [1]);
                break;
            case "abort":
                await victim.Application.AbortAsync();
                break;
            case "cancel":
                cancellation.Cancel();
                break;
            case "buffer":
                for (int i = 0; i < MplexProtocol.MaxQueuedFrames + 2; i++)
                    await fixture.SendFrameAsync(id, message, [1]);
                break;
            case "bytes":
                for (int i = 0; i < 5; i++)
                    await fixture.SendFrameAsync(id, message, new byte[MplexFrame.MaxPayloadLength]);
                break;
        }
        await fixture.AwaitApplicationCompletionAsync(victim.Application);
        await fixture.WaitUntilAsync(() => fixture.Connection.RetiredStreamCount == 1);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Connection.PendingResetCount, Is.EqualTo(1));
            Assert.That(fixture.Connection.BufferedByteCount, Is.Zero);
            Assert.That(fixture.Connection.QueuedFrameCount, Is.EqualTo(64));
        });
        // Tombstones discard every frame kind without touching a disposed stream or its buffers.
        await fixture.SendFrameAsync(id, message, [2, 3]);
        await fixture.SendFrameAsync(id, close);
        await fixture.SendFrameAsync(id, local ? MplexFlag.ResetReceiver : MplexFlag.ResetInitiator);
        await fixture.SendFrameAsync(0, MplexFlag.MessageInitiator, [7]);
        Assert.That(await fixture.ReadBytesAsync(backlog[0].Application, 1), Is.EqualTo(new byte[] { 7 }));
        Assert.That(await fixture.ReadBytesAsync(fixture.Remote, 1), Is.EqualTo(new byte[] { 42 }));
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(id, reset, 0)));
        await fixture.WaitUntilAsync(() => fixture.Connection.RetiredStreamCount == 0);
        Assert.That(fixture.Completion.IsCompleted, Is.False);
    }

    [Test]
    public async Task AllAcceptedResetsRetainReservationsUntilSentThenRestoreAdmission()
    {
        await using MplexFixture fixture = new();
        List<OpenedStream> streams = [.. await fixture.BacklogAsync(65)];
        for (ulong id = 65; id < MplexProtocol.MaxStreams; id++)
            streams.Add(await fixture.OpenRemoteAsync(id));
        foreach (OpenedStream stream in streams) await stream.Application.AbortAsync();
        await fixture.WaitUntilAsync(() => fixture.Connection.RetiredStreamCount == MplexProtocol.MaxStreams);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Connection.ReservedStreamCount, Is.EqualTo(MplexProtocol.MaxStreams));
            Assert.That(fixture.Connection.PendingResetCount, Is.EqualTo(MplexProtocol.MaxStreams));
            Assert.That(fixture.Connection.BufferedByteCount, Is.Zero);
        });
        TaskCompletionSource<object?> denied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Enqueue(new UpgradeOptions { CompletionSource = denied });
        Assert.That(await CaptureFailureAsync(denied.Task.WaitAsync(fixture.Token)), Is.TypeOf<InvalidOperationException>());
        await fixture.SendFrameAsync(0, MplexFlag.MessageInitiator, new byte[8192]);
        await fixture.SendFrameAsync(0, MplexFlag.ResetInitiator);
        for (ulong id = 600; id < 600 + MplexProtocol.MaxRejectedResets; id++)
            await fixture.SendFrameAsync(id, MplexFlag.NewStream);
        await fixture.WaitUntilAsync(() => fixture.Connection.PendingRejectionCount == MplexProtocol.MaxRejectedResets);
        Assert.That(fixture.Connection.PendingResetCount, Is.EqualTo(MplexProtocol.MaxStreams + MplexProtocol.MaxRejectedResets));
        Assert.That(fixture.UpgradeCount, Is.EqualTo(MplexProtocol.MaxStreams));

        Assert.That(await fixture.ReadBytesAsync(fixture.Remote, 1), Is.EqualTo(new byte[] { 42 }));
        HashSet<ulong> resetIds = [];
        for (int i = 0; i < MplexProtocol.MaxStreams + MplexProtocol.MaxRejectedResets; i++)
        {
            WireFrame reset = await fixture.ReadFrameAsync();
            Assert.That(reset.Header.Flag, Is.EqualTo(MplexFlag.ResetReceiver));
            Assert.That(resetIds.Add(reset.Header.StreamId), Is.True, "Each obligation is sent exactly once.");
        }
        Assert.That(resetIds, Is.EquivalentTo(Enumerable.Range(0, MplexProtocol.MaxStreams).Concat(Enumerable.Range(600, MplexProtocol.MaxRejectedResets)).Select(id => (ulong)id)));
        await fixture.WaitUntilAsync(() => fixture.Connection.ReservedStreamCount == 0 && fixture.Connection.PendingResetCount == 0);
        Assert.That(fixture.Connection.PendingRejectionCount, Is.Zero);
        OpenedStream replacement = await fixture.OpenRemoteAsync(700);
        await fixture.SendFrameAsync(700, MplexFlag.MessageInitiator, [8]);
        Assert.That(await fixture.ReadBytesAsync(replacement.Application, 1), Is.EqualTo(new byte[] { 8 }));
        await fixture.OpenLocalAsync();
        Assert.That((await fixture.ReadFrameAsync()).Header.Flag, Is.EqualTo(MplexFlag.NewStream));
    }

    [Test]
    public async Task ResetTransmissionBeforeCleanupKeepsTheReservationUntilCleanupFinishes()
    {
        await using MplexFixture fixture = new();
        AbortBarrier barrier = fixture.DelayNextAbort();
        OpenedStream stream = await fixture.OpenRemoteAsync(0);
        await stream.Application.AbortAsync();
        await barrier.Entered.Task.WaitAsync(fixture.Token);
        Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(0, MplexFlag.ResetReceiver, 0)));
        await fixture.WaitUntilAsync(() => fixture.Connection.PendingResetCount == 0);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Connection.ReservedStreamCount, Is.EqualTo(1));
            Assert.That(fixture.Connection.RetiredStreamCount, Is.Zero);
        });
        await fixture.SendFrameAsync(0, MplexFlag.MessageInitiator, [1]);
        barrier.Release.TrySetResult();
        await fixture.WaitUntilAsync(() => fixture.Connection.ReservedStreamCount == 0);
        Assert.That(fixture.Connection.BufferedByteCount, Is.Zero);
    }

    [Test]
    public async Task DuplicateOpenWhileResetIsPendingStillTerminatesTheConnection()
    {
        await using MplexFixture fixture = new();
        OpenedStream[] streams = await fixture.BacklogAsync(1);
        await streams[0].Application.AbortAsync();
        await fixture.WaitUntilAsync(() => fixture.Connection.RetiredStreamCount == 1);
        await fixture.SendFrameAsync(0, MplexFlag.NewStream, expectDisconnect: true);
        await fixture.ObserveCompletionAsync();
        Assert.That(fixture.Connection.ReservedStreamCount, Is.Zero);
        Assert.That(fixture.Connection.PendingResetCount, Is.Zero);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task RejectionBudgetPausesTheReaderButAcceptedResetCleanupStillFinishes(bool listener, bool failTransport)
    {
        await using MplexFixture fixture = new(listener);
        OpenedStream[] backlog = await fixture.BacklogAsync(65);
        for (ulong id = 65; id < MplexProtocol.MaxStreams; id++) await fixture.OpenRemoteAsync(id);
        for (ulong id = 600; id < 600 + MplexProtocol.MaxRejectedResets; id++)
            await fixture.SendFrameAsync(id, MplexFlag.NewStream);
        await fixture.WaitUntilAsync(() => fixture.Connection.PendingRejectionCount == MplexProtocol.MaxRejectedResets);
        await fixture.SendFrameAsync(664, MplexFlag.NewStream);
        await fixture.WaitUntilAsync(() => fixture.Log.Warnings.Count == 1);
        Dictionary<string, object?> warning = fixture.Log.Warnings.Single();
        Assert.Multiple(() =>
        {
            Assert.That(warning["Context"], Is.EqualTo("mplex-test"));
            Assert.That(warning["Stream"], Is.EqualTo(664UL));
            Assert.That(warning["Budget"], Is.EqualTo(MplexProtocol.MaxRejectedResets));
        });
        Task<IOResult> blockedInbound = fixture.WriteAsync(fixture.Remote,
            new MplexFrame(1, MplexFlag.MessageInitiator, 1).Encode(new ReadOnlySequence<byte>(new byte[] { 9 })));
        Assert.That(blockedInbound.IsCompleted, Is.False, "The reader is paused with all rejection permits owned.");
        await backlog[0].Application.AbortAsync();
        await fixture.WaitUntilAsync(() => fixture.Connection.RetiredStreamCount == 1);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Connection.ReservedStreamCount, Is.EqualTo(MplexProtocol.MaxStreams));
            Assert.That(fixture.Connection.PendingResetCount, Is.EqualTo(MplexProtocol.MaxRejectedResets + 1));
            Assert.That(fixture.Connection.PendingRejectionCount, Is.EqualTo(MplexProtocol.MaxRejectedResets));
        });
        if (failTransport)
        {
            await fixture.Remote.AbortAsync();
            await fixture.ObserveCompletionAsync();
            Assert.That(await blockedInbound.WaitAsync(fixture.Token), Is.AnyOf(IOResult.Ended, IOResult.Aborted));
            AssertReleased(fixture);
            return;
        }

        Assert.That(await fixture.ReadBytesAsync(fixture.Remote, 1), Is.EqualTo(new byte[] { 42 }));
        List<ulong> resets = [];
        int ordinary = 0;
        int burst = 0;
        while (resets.Count < 66 || ordinary < 64)
        {
            WireFrame frame = await fixture.ReadFrameAsync();
            if (frame.Header.Flag == MplexFlag.ResetReceiver)
            {
                resets.Add(frame.Header.StreamId);
                burst++;
                if (ordinary < 64) Assert.That(burst, Is.LessThanOrEqualTo(MplexProtocol.MaxResetBurst));
            }
            else
            {
                Assert.That(frame.Header.Flag, Is.EqualTo(MplexFlag.MessageReceiver));
                ordinary++;
                burst = 0;
            }
        }
        Assert.That(resets, Is.EqualTo(Enumerable.Range(600, 64).Select(id => (ulong)id).Append(0UL).Append(664UL)),
            "A newly permitted rejection cannot overtake the accepted reset already in the FIFO.");
        Assert.That(await blockedInbound.WaitAsync(fixture.Token), Is.EqualTo(IOResult.Ok));
        Assert.That(await fixture.ReadBytesAsync(backlog[1].Application, 1), Is.EqualTo(new byte[] { 9 }));
        await fixture.WaitUntilAsync(() => fixture.Connection.PendingResetCount == 0);
        Assert.That(fixture.Connection.PendingRejectionCount, Is.Zero);
        Assert.That(fixture.Log.Warnings, Has.Count.EqualTo(1));
        await fixture.OpenRemoteAsync(665);
        fixture.CancelSession();
        await fixture.ObserveCompletionAsync();
        AssertReleased(fixture);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ShutdownWithAResetInFlightAndTheReaderWaitingReleasesAllResources(bool failTransport)
    {
        await using MplexFixture fixture = new();
        for (ulong id = 0; id < MplexProtocol.MaxStreams; id++) await fixture.OpenRemoteAsync(id);
        await fixture.SendFrameAsync(600, MplexFlag.NewStream);
        // A reset has no payload: consume only its first header byte to stall the in-flight write.
        Assert.That((await fixture.Remote.ReadAsync(1, token: fixture.Token)).Result, Is.EqualTo(IOResult.Ok));
        for (ulong id = 601; id < 665; id++) await fixture.SendFrameAsync(id, MplexFlag.NewStream);
        await fixture.WaitUntilAsync(() => fixture.Log.Warnings.Count == 1);
        Assert.That(fixture.Connection.PendingRejectionCount, Is.EqualTo(MplexProtocol.MaxRejectedResets));
        if (failTransport) await fixture.Remote.AbortAsync();
        else fixture.CancelSession();
        await fixture.ObserveCompletionAsync();
        AssertReleased(fixture);
        Assert.That(fixture.Log.Warnings, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AcceptedResetAndOrdinaryFramesProgressDuringContinuingRejections()
    {
        await using MplexFixture fixture = new();
        AbortBarrier barrier = fixture.DelayNextAbort();
        OpenedStream victim = await fixture.OpenRemoteAsync(500);
        await fixture.BacklogAsync(65);
        for (ulong id = 65; id < MplexProtocol.MaxStreams; id++)
            if (id != 500) await fixture.OpenRemoteAsync(id);
        Assert.That(fixture.Connection.ReservedStreamCount, Is.EqualTo(MplexProtocol.MaxStreams));
        Task rejections = fixture.Track(RejectAsync());
        await fixture.WaitUntilAsync(() => fixture.Log.Warnings.Count == 1);
        await victim.Application.AbortAsync();
        await barrier.Entered.Task.WaitAsync(fixture.Token);
        Assert.That(fixture.Connection.PendingResetCount, Is.EqualTo(65));
        // Cleanup is held so admission stays full even after the victim's reset reaches the wire.
        Assert.That(await fixture.ReadBytesAsync(fixture.Remote, 1), Is.EqualTo(new byte[] { 42 }));
        List<ulong> resetIds = [];
        int ordinary = 0;
        int burst = 0;
        while (resetIds.Count < 129 || ordinary < 64)
        {
            WireFrame frame = await fixture.ReadFrameAsync();
            if (frame.Header.Flag == MplexFlag.ResetReceiver)
            {
                resetIds.Add(frame.Header.StreamId);
                burst++;
                if (ordinary < 64) Assert.That(burst, Is.LessThanOrEqualTo(MplexProtocol.MaxResetBurst));
            }
            else
            {
                Assert.That(frame.Header.Flag, Is.EqualTo(MplexFlag.MessageReceiver));
                ordinary++;
                burst = 0;
            }
        }
        await rejections.WaitAsync(fixture.Token);
        Assert.Multiple(() =>
        {
            Assert.That(resetIds.Take(65), Is.EqualTo(Enumerable.Range(600, 64).Select(id => (ulong)id).Append(500UL)));
            Assert.That(resetIds.Skip(65), Is.EqualTo(Enumerable.Range(664, 64).Select(id => (ulong)id)));
            Assert.That(fixture.Log.Warnings, Has.Count.EqualTo(1));
            Assert.That(fixture.Connection.ReservedStreamCount, Is.EqualTo(MplexProtocol.MaxStreams));
        });
        barrier.Release.TrySetResult();
        await fixture.WaitUntilAsync(() => fixture.Connection.ReservedStreamCount == MplexProtocol.MaxStreams - 1 && fixture.Connection.PendingResetCount == 0);
        await fixture.OpenRemoteAsync(800);

        async Task RejectAsync()
        {
            for (ulong id = 600; id < 728; id++) await fixture.SendFrameAsync(id, MplexFlag.NewStream);
        }
    }

    [Test]
    public async Task ResetBurstsSkipCanceledOrdinaryFramesWithoutStarvingEligibleData()
    {
        await using MplexFixture fixture = new();
        OpenedStream[] backlog = await fixture.BacklogAsync(65);
        // Cancel the first queued ordinary frame; it must not consume the fairness turn or reach the wire.
        await backlog[1].Application.AbortAsync();
        for (ulong id = 100; id < 124; id++)
        {
            OpenedStream stream = await fixture.OpenRemoteAsync(id);
            await stream.Application.AbortAsync();
        }
        await fixture.WaitUntilAsync(() => fixture.Connection.RetiredStreamCount == 25);
        Assert.That(await fixture.ReadBytesAsync(fixture.Remote, 1), Is.EqualTo(new byte[] { 42 }));
        int resets = 0;
        int messages = 0;
        int burst = 0;
        List<ulong> ordinaryIds = [];
        while (resets < 25 || messages < 63)
        {
            WireFrame frame = await fixture.ReadFrameAsync();
            if (frame.Header.Flag == MplexFlag.ResetReceiver)
            {
                burst++;
                resets++;
                if (messages < 63) Assert.That(burst, Is.LessThanOrEqualTo(MplexProtocol.MaxResetBurst));
            }
            else
            {
                Assert.That(frame.Header.Flag, Is.EqualTo(MplexFlag.MessageReceiver));
                if (resets <= 24) Assert.That(burst, Is.EqualTo(MplexProtocol.MaxResetBurst));
                burst = 0;
                messages++;
                ordinaryIds.Add(frame.Header.StreamId);
            }
        }
        Assert.That(ordinaryIds, Is.EqualTo(Enumerable.Range(2, 63).Select(id => (ulong)id)));
        Assert.That(fixture.Completion.IsCompleted, Is.False);
    }

    [Test]
    public async Task IdleWriterWakesForEitherQueue()
    {
        await using MplexFixture fixture = new();
        for (ulong id = 0; id < 10; id++)
        {
            OpenedStream stream = await fixture.OpenRemoteAsync(id);
            _ = fixture.WriteAsync(stream.Application, [3]);
            Assert.That((await fixture.ReadFrameAsync()).Body, Is.EqualTo(new byte[] { 3 }));
            await stream.Application.AbortAsync();
            Assert.That((await fixture.ReadFrameAsync()).Header, Is.EqualTo(new MplexFrame(id, MplexFlag.ResetReceiver, 0)));
            await fixture.WaitUntilAsync(() => fixture.Connection.ReservedStreamCount == 0);
        }
    }

    private static void AssertReleased(MplexFixture fixture)
    {
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Connection.ReservedStreamCount, Is.Zero);
            Assert.That(fixture.Connection.PendingResetCount, Is.Zero);
            Assert.That(fixture.Connection.PendingRejectionCount, Is.Zero);
            Assert.That(fixture.Connection.QueuedFrameCount, Is.Zero);
            Assert.That(fixture.Connection.BufferedByteCount, Is.Zero);
        });
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
        private AbortBarrier? _nextAbortBarrier;
        private readonly List<AbortBarrier> _abortBarriers = [];
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
            Connection = new MplexProtocol.Connection(_transport, context, Log);
            Completion = Connection.RunAsync(listener);
        }

        public RecordingLogger Log { get; } = new();
        public MplexProtocol.Connection Connection { get; }
        public IChannel Remote { get; }
        public Task Completion { get; }
        public CancellationToken Token => _deadline.Token;
        public int UpgradeCount => Volatile.Read(ref _upgradeCount);

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(1, Token);
        }

        public async Task<OpenedStream[]> BacklogAsync(int count)
        {
            OpenedStream[] streams = new OpenedStream[count];
            for (int i = 0; i < count; i++)
            {
                streams[i] = await OpenRemoteAsync((ulong)i);
                _ = WriteAsync(streams[i].Application, [42]);
                if (i == 0)
                {
                    MplexFrame header = await MplexFrame.ReadAsync(Remote, Token);
                    Assert.That(header, Is.EqualTo(new MplexFrame(0, MplexFlag.MessageReceiver, 1)));
                }
                else
                {
                    // Application write completion precedes enqueue; observe the actual queue before proceeding.
                    await WaitUntilAsync(() => Connection.QueuedFrameCount == i);
                }
            }
            return streams;
        }

        public AbortBarrier DelayNextAbort()
        {
            AbortBarrier barrier = new();
            _abortBarriers.Add(barrier);
            _nextAbortBarrier = barrier;
            return barrier;
        }

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

        public async Task SendFrameAsync(ulong id, MplexFlag flag, byte[]? payload = null, bool expectDisconnect = false)
        {
            payload ??= [];
            await SendBytesAsync(new MplexFrame(id, flag, payload.Length).Encode(new ReadOnlySequence<byte>(payload)), expectDisconnect);
        }

        public async Task SendBytesAsync(ReadOnlyMemory<byte> bytes, bool expectDisconnect = false)
        {
            IOResult result = await Remote.WriteAsync(new ReadOnlySequence<byte>(bytes), Token);
            // Protocol rejection can abort the transport before the consumed write is acknowledged.
            if (expectDisconnect) Assert.That(result, Is.AnyOf(IOResult.Ok, IOResult.Ended, IOResult.Aborted));
            else Assert.That(result, Is.EqualTo(IOResult.Ok));
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
            foreach (AbortBarrier barrier in _abortBarriers) barrier.Release.TrySetResult();
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
            // Claim the barrier before publishing: another stream may upgrade as soon as the test resumes.
            AbortBarrier? barrier = Interlocked.Exchange(ref _nextAbortBarrier, null);
            IChannel protocol = barrier is null ? bridge : new DelayedAbortChannel(bridge, barrier);
            _opened.Writer.TryWrite(new OpenedStream(options, application));
            return protocol;
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

    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<Dictionary<string, object?>> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Enqueue(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary());
        }
    }

    private sealed class AbortBarrier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DelayedAbortChannel(IChannel inner, AbortBarrier barrier) : IChannel
    {
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
            => inner.ReadAsync(length, blockingMode, token);
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => inner.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseReadAsync() => inner.CloseReadAsync();
        public ValueTask CloseAsync() => inner.CloseAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
        public async ValueTask AbortAsync()
        {
            barrier.Entered.TrySetResult();
            await barrier.Release.Task;
            await inner.AbortAsync();
        }
    }
}
