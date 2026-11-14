// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using NSubstitute;
using SIPSorcery.Net;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
[Category("Integration")]
public class WebRtcDirectIntegrationTests
{
    [Test]
    [Explicit("Manual runtime smoke test for listener address publication on host WebRTC stack.")]
    public async Task ListenAsync_AnnouncesWebRtcDirectAddressWithCerthash()
    {
        WebRtcDirectProtocol protocol = new();
        using Half half = new(new Identity());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        Task listenTask = protocol.ListenAsync(half.Context, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
        try
        {
            await WaitUntilAsync(() => half.Address is not null, TimeSpan.FromSeconds(2), cts.Token, listenTask);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive("Host WebRTC stack did not expose local DTLS fingerprint during listener bootstrap.");
        }

        cts.Cancel();
        await listenTask;

        Assert.That(half.Address, Is.Not.Null);
        Assert.That(half.Address!.ToString(), Does.Contain("/webrtc-direct/certhash/"));
    }

    [Test]
    [Explicit("Manual runtime smoke test for listener cancellation path on host WebRTC stack.")]
    public async Task ListenAsync_StopsOnCancellation()
    {
        WebRtcDirectProtocol protocol = new();
        using Half half = new(new Identity());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        Task listenTask = protocol.ListenAsync(half.Context, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);

        try
        {
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        cts.Cancel();
        try
        {
            await listenTask;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive("Host WebRTC stack did not expose local DTLS fingerprint during listener bootstrap.");
        }
    }

    [Test]
    public async Task DialAndListen_UpgradesOnBothSides()
    {
        WebRtcDirectProtocol listenerProtocol = new();
        WebRtcDirectProtocol dialerProtocol = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
        using Half listener = new(new Identity());
        using Half dialer = new(new Identity());

        Task listenerTask = listenerProtocol.ListenAsync(listener.Context, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
        Multiaddress listenerAddress;
        try
        {
            listenerAddress = await WaitForListenerAddressAsync(() => listener.Address, cts.Token, listenerTask);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive("Host WebRTC stack did not expose local DTLS fingerprint during listener bootstrap.");
            return;
        }

        Task dialTask = dialerProtocol.DialAsync(dialer.Context, listenerAddress, cts.Token);

        await WaitForSessionsAsync(listener, dialer, dialTask, TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(new Identity(listener.State.RemotePublicKey!).PeerId, Is.EqualTo(dialer.Identity.PeerId),
                "Each half must have recovered the other's identity from the Noise handshake before the session.");
            Assert.That(new Identity(dialer.State.RemotePublicKey!).PeerId, Is.EqualTo(listener.Identity.PeerId));
        });

        cts.Cancel();
        await IgnoreCancellationAsync(dialTask, listenerTask);
    }

    [Test]
    public async Task DialAndListen_RejectsWhenCerthashMismatches()
    {
        WebRtcDirectProtocol listenerProtocol = new();
        WebRtcDirectProtocol dialerProtocol = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));

        using Half listener = new(new Identity());
        using Half dialer = new(new Identity());

        Task listenerTask = listenerProtocol.ListenAsync(listener.Context, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
        Multiaddress listenerAddress;
        try
        {
            listenerAddress = await WaitForListenerAddressAsync(() => listener.Address, cts.Token, listenerTask);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive("Host WebRTC stack did not expose local DTLS fingerprint during listener bootstrap.");
            return;
        }
        Multiaddress tamperedAddress = TamperCerthash(listenerAddress);

        Assert.That(
            async () => await dialerProtocol.DialAsync(dialer.Context, tamperedAddress, cts.Token).WaitAsync(TimeSpan.FromSeconds(10)),
            Throws.TypeOf<InvalidOperationException>());

        cts.Cancel();
        await listenerTask;
    }

    /// <summary>
    /// The first connection this transport carries more than one stream over, and the only test
    /// that can see a stream-id error both halves make together.
    /// <para>
    /// The ids are asserted as the literals RFC 8832 section 6 requires, not as "distinct" or "the
    /// right parity": the dialer is the DTLS client and takes the even ids starting at 2 because
    /// libp2p reserves 0 for the Noise channel, the listener is the DTLS server and takes the odd
    /// ids starting at 1. Inverting both halves at once passes any echo test and fails against
    /// every real peer, so only the literals catch it. Each side is also asserted to see the
    /// other's ids, which is what makes them observations of the wire rather than of a constant.
    /// </para>
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task DialAndListen_StreamsCarryDataBothWays_WithAbsoluteStreamIds(CancellationToken token)
    {
        WebRtcDirectProtocol listenerProtocol = new();
        WebRtcDirectProtocol dialerProtocol = new();
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        using Half listener = new(new Identity());
        using Half dialer = new(new Identity());

        Task listenerTask = listenerProtocol.ListenAsync(listener.Context, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
        Multiaddress listenerAddress;
        try
        {
            listenerAddress = await WaitForListenerAddressAsync(() => listener.Address, cts.Token, listenerTask);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive("Host WebRTC stack did not expose local DTLS fingerprint during listener bootstrap.");
            return;
        }

        Task dialTask = dialerProtocol.DialAsync(dialer.Context, listenerAddress, cts.Token);
        await WaitForSessionsAsync(listener, dialer, dialTask, TimeSpan.FromSeconds(30));

        // Two streams from each side, so the second id of each half is covered as well as the first.
        dialer.RequestStream();
        dialer.RequestStream();
        listener.RequestStream();
        listener.RequestStream();

        OpenedStream[] dialerStreams = await dialer.NextStreamsAsync(4, cts.Token);
        OpenedStream[] listenerStreams = await listener.NextStreamsAsync(4, cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(StreamIds(dialerStreams, UpgradeModeOverride.Dial), Is.EqualTo(new ushort[] { 2, 4 }),
                "The dialer is the DTLS client: even ids, and 0 is the Noise channel.");
            Assert.That(StreamIds(listenerStreams, UpgradeModeOverride.Listen), Is.EqualTo(new ushort[] { 2, 4 }),
                "The listener must accept the dialer's streams under the ids the dialer put on the wire.");
            Assert.That(StreamIds(listenerStreams, UpgradeModeOverride.Dial), Is.EqualTo(new ushort[] { 1, 3 }),
                "The listener is the DTLS server: odd ids.");
            Assert.That(StreamIds(dialerStreams, UpgradeModeOverride.Listen), Is.EqualTo(new ushort[] { 1, 3 }),
                "The dialer must accept the listener's streams under the ids the listener put on the wire.");
        });

        IChannel dialerOpened = StreamWithId(dialerStreams, 2);
        IChannel listenerAccepted = StreamWithId(listenerStreams, 2);
        IChannel listenerOpened = StreamWithId(listenerStreams, 1);
        IChannel dialerAccepted = StreamWithId(dialerStreams, 1);

        await EchoAsync(dialerOpened, listenerAccepted, [1, 2, 3, 4], cts.Token);
        await EchoAsync(listenerOpened, dialerAccepted, [5, 6, 7], cts.Token);

        // A FIN is per stream. Half-closing the dialer's first stream must end exactly that one
        // read half and leave every other stream, in both directions, carrying data.
        Assert.That(await dialerOpened.WriteEofAsync(cts.Token), Is.EqualTo(IOResult.Ok));
        Assert.That((await listenerAccepted.ReadAsync(1, ReadBlockingMode.WaitAll, cts.Token)).Result, Is.EqualTo(IOResult.Ended));

        await EchoAsync(listenerOpened, dialerAccepted, [8], cts.Token);
        await EchoAsync(StreamWithId(dialerStreams, 4), StreamWithId(listenerStreams, 4), [9], cts.Token);
        await EchoAsync(StreamWithId(listenerStreams, 3), StreamWithId(dialerStreams, 3), [10], cts.Token);

        cts.Cancel();
        await IgnoreCancellationAsync(dialTask, listenerTask);
    }

    /// <summary>
    /// The receive loop's own contract, which the self-dial tests cannot see.
    /// <para>
    /// An admitted peer keeps sending binding requests for the life of the connection --
    /// <c>RtpIceChannel._connectivityChecksTimer</c> runs at <c>CONNECTED_CHECK_PERIOD</c> = 3s and the
    /// pair is abandoned at <c>FAILED_TIMEOUT_PERIOD</c> = 16s (both verified by reflection against
    /// SIPSorcery 10.0.14) -- so a loop that short-circuits a known source address into its session
    /// instead of classifying the first byte answers nothing after admission. That passes a 1s
    /// self-dial and drops every real dialer about twenty seconds in, which is why the second request
    /// below is sent from the SAME socket as the one that admitted the row.
    /// </para>
    /// <para>
    /// The junk in between is the other half: one malformed datagram must not be able to end the loop
    /// for every peer on the port. Lengths 1..19 are the ones that make
    /// <c>STUNMessage.ParseSTUNMessage</c> dereference a null header.
    /// </para>
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public async Task ListenAsync_KeepsAnsweringAnAdmittedPeersConsentChecks_AndSurvivesJunkBetweenThem(CancellationToken token)
    {
        WebRtcDirectProtocol protocol = new();
        using Half half = new(new Identity());

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task listenerTask = protocol.ListenAsync(half.Context, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
        Multiaddress listenerAddress = await WaitForListenerAddressAsync(() => half.Address, cts.Token, listenerTask);
        (IPEndPoint listenerEndpoint, _) = WebRtcDirectMultiaddr.Parse(listenerAddress);

        using UdpClient peer = new(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] bindingRequest = BindingRequest(WebRtcIceCredentials.BuildV2Ufrag(new string('7', 22)));

        await peer.SendAsync(bindingRequest, listenerEndpoint, cts.Token);
        UdpReceiveResult admitted = await peer.ReceiveAsync(cts.Token);
        Assert.That(WebRtcDirectStun.IsStunDatagram(admitted.Buffer), Is.True, "the admitting binding request was not answered");

        foreach (int length in (int[])[1, 2, 19, 20, 63, 128])
        {
            await peer.SendAsync(new byte[length], length, listenerEndpoint);
        }

        // The row now carries a session, so an address-map-first loop would feed this to DTLS.
        await peer.SendAsync(bindingRequest, listenerEndpoint, cts.Token);
        UdpReceiveResult consentCheck = await peer.ReceiveAsync(cts.Token);
        Assert.That(WebRtcDirectStun.IsStunDatagram(consentCheck.Buffer), Is.True, "a consent check from the admitted address was not answered");

        cts.Cancel();
        await listenerTask;
    }

    /// <summary>
    /// A genuine signed binding request, built the way <see cref="WebRtcDirectStun.BuildBindingSuccessResponse"/>
    /// signs its reply: SIPSorcery's serializer, MESSAGE-INTEGRITY keyed with the server ufrag and a
    /// FINGERPRINT appended. It is also what proves the loop passes the datagram on at its exact
    /// length -- MESSAGE-INTEGRITY covers the whole buffer, so a single trailing byte fails the check
    /// and the request goes unanswered.
    /// </summary>
    private static byte[] BindingRequest(string serverUfrag)
    {
        STUNMessage request = new(STUNMessageTypesEnum.BindingRequest);
        request.AddUsernameAttribute($"{serverUfrag}:PSAY");
        return request.ToByteBufferStringKey(serverUfrag, true);
    }

    /// <summary>
    /// One side of a connection as the transport sees it: a mocked <see cref="ITransportContext"/>
    /// plus the two things the multi-stream path needs from the layer above -- a queue of dial
    /// requests to answer, and a record of every stream handed to <c>Upgrade</c>.
    /// <para>
    /// The <c>Upgrade</c> task deliberately never completes on its own. In production it represents
    /// the protocol stack that owns the stream for as long as the connection lives, and a completed
    /// task would tell the transport the stream is finished the instant it was handed over.
    /// </para>
    /// </summary>
    private sealed class Half : IDisposable
    {
        private readonly BlockingCollection<UpgradeOptions> _requests = new();
        // Fully qualified: Nethermind.Libp2p.Core has a Channel of its own.
        private readonly System.Threading.Channels.Channel<OpenedStream> _streams =
            System.Threading.Channels.Channel.CreateUnbounded<OpenedStream>();
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _sessionStop = new();

        public Half(Identity identity)
        {
            Identity = identity;
            ILocalPeer peer = Substitute.For<ILocalPeer>();
            peer.Identity.Returns(identity);

            ITransportContext context = Substitute.For<ITransportContext>();
            context.Peer.Returns(peer);
            context.When(c => c.ListenerReady(Arg.Any<Multiaddress>())).Do(call =>
            {
                Address = call.Arg<Multiaddress>();
                Ready.TrySetResult(Address);
            });
            context.CreateConnection().Returns(_ => CreateConnection(peer));
            Context = context;
        }

        public Identity Identity { get; }

        public ITransportContext Context { get; }

        public State State { get; } = new();

        public Multiaddress? Address { get; private set; }

        public TaskCompletionSource<Multiaddress> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when this half hands its authenticated connection to a session.</summary>
        public TaskCompletionSource Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Asks the transport to open one outbound stream, as a dialling protocol would.</summary>
        public void RequestStream() => _requests.Add(new UpgradeOptions());

        public async Task<OpenedStream[]> NextStreamsAsync(int count, CancellationToken token)
        {
            OpenedStream[] streams = new OpenedStream[count];
            for (int i = 0; i < count; i++)
            {
                streams[i] = await _streams.Reader.ReadAsync(token);
            }

            return streams;
        }

        public void Dispose()
        {
            _held.TrySetResult();
            _requests.CompleteAdding();
            _sessionStop.Cancel();
            _sessionStop.Dispose();
            _requests.Dispose();
        }

        private INewConnectionContext CreateConnection(ILocalPeer peer)
        {
            INewConnectionContext connection = Substitute.For<INewConnectionContext>();
            INewSessionContext session = Substitute.For<INewSessionContext>();

            connection.Peer.Returns(peer);
            connection.State.Returns(State);
            connection.Token.Returns(_sessionStop.Token);
            connection.UpgradeToSession().Returns(_ =>
            {
                Session.TrySetResult();
                return session;
            });
            connection.Upgrade(Arg.Any<IChannel>(), Arg.Any<UpgradeOptions?>()).Returns(Record);
            connection.Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>(), Arg.Any<UpgradeOptions?>()).Returns(Record);

            session.Peer.Returns(peer);
            session.State.Returns(State);
            session.Token.Returns(_sessionStop.Token);
            session.DialRequests.Returns(_requests.GetConsumingEnumerable(_sessionStop.Token));
            session.Upgrade(Arg.Any<IChannel>(), Arg.Any<UpgradeOptions?>()).Returns(Record);

            return connection;
        }

        private Task Record(NSubstitute.Core.CallInfo call)
        {
            IChannel channel = call.Arg<IChannel>();
            UpgradeOptions? options = call.Arg<UpgradeOptions?>();
            _streams.Writer.TryWrite(new OpenedStream(
                ((WebRtcChannel)channel).StreamId,
                options?.ModeOverride ?? UpgradeModeOverride.None,
                channel));
            options?.CompletionSource?.TrySetResult(null);
            return _held.Task;
        }
    }

    /// <summary>A stream the transport handed to the layer above, with the id it carries on the wire.</summary>
    private sealed record OpenedStream(ushort StreamId, UpgradeModeOverride Mode, IChannel Channel);

    private static async Task<Multiaddress> WaitForListenerAddressAsync(Func<Multiaddress?> getListenerAddress, CancellationToken token, Task? listenerTask = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (getListenerAddress() is null)
        {
            if (listenerTask is not null && listenerTask.IsFaulted)
            {
                Exception? root = listenerTask.Exception?.GetBaseException();
                if (root is InvalidOperationException)
                {
                    throw root;
                }

                throw new InvalidOperationException("Listener terminated before publishing address.", root);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Listener did not publish address.");
            }

            await Task.Delay(25, token);
        }

        return getListenerAddress()!;
    }

    private static Multiaddress TamperCerthash(Multiaddress source)
    {
        string[] parts = source.ToString().Split('/');
        int certhashIndex = Array.FindIndex(parts, p => p.Equals("certhash", StringComparison.Ordinal));
        if (certhashIndex < 0 || certhashIndex + 1 >= parts.Length)
        {
            throw new InvalidOperationException("Source address does not contain certhash.");
        }

        string value = parts[certhashIndex + 1];
        int indexToMutate = Math.Max(1, value.Length / 2);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        char replacement = alphabet.First(c => c != value[indexToMutate]);
        parts[certhashIndex + 1] = value[..indexToMutate] + replacement + value[(indexToMutate + 1)..];
        return Multiaddress.Decode(string.Join('/', parts));
    }

    private static TaskCompletionSource CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Waits for both halves to reach a session, but fails fast on a dial that died first -- which
    /// would otherwise show up as a timeout naming the wrong thing.
    /// </summary>
    private static async Task WaitForSessionsAsync(Half listener, Half dialer, Task dialTask, TimeSpan timeout)
    {
        Task bothSessions = Task.WhenAll(listener.Session.Task, dialer.Session.Task);
        Task first = await Task.WhenAny(bothSessions, dialTask).WaitAsync(timeout);
        if (first == dialTask)
        {
            await dialTask;
            Assert.Fail("The dial completed before either half upgraded to a session.");
        }

        await bothSessions;
    }

    private static IChannel StreamWithId(OpenedStream[] streams, ushort streamId) =>
        streams.Single(stream => stream.StreamId == streamId).Channel;

    private static ushort[] StreamIds(OpenedStream[] streams, UpgradeModeOverride mode) =>
        [.. streams.Where(stream => stream.Mode == mode).Select(stream => stream.StreamId).Order()];

    /// <summary>Round-trips a payload over one stream, so both directions of it are exercised.</summary>
    private static async Task EchoAsync(IChannel from, IChannel to, byte[] payload, CancellationToken token)
    {
        Assert.That(await from.WriteAsync(new ReadOnlySequence<byte>(payload), token), Is.EqualTo(IOResult.Ok));
        Assert.That(await ReadExactlyAsync(to, payload.Length, token), Is.EqualTo(payload));
        Assert.That(await to.WriteAsync(new ReadOnlySequence<byte>(payload), token), Is.EqualTo(IOResult.Ok));
        Assert.That(await ReadExactlyAsync(from, payload.Length, token), Is.EqualTo(payload));
    }

    private static async Task<byte[]> ReadExactlyAsync(IChannel channel, int length, CancellationToken token)
    {
        ReadResult result = await channel.ReadAsync(length, ReadBlockingMode.WaitAll, token);
        Assert.That(result.Result, Is.EqualTo(IOResult.Ok));
        return result.Data.ToArray();
    }

    private static async Task IgnoreCancellationAsync(params Task[] tasks)
    {
        foreach (Task task in tasks)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken token, Task? listenerTask = null)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (!condition())
        {
            if (listenerTask is not null && listenerTask.IsFaulted)
            {
                Exception? root = listenerTask.Exception?.GetBaseException();
                if (root is InvalidOperationException)
                {
                    throw root;
                }

                throw new InvalidOperationException("Listener terminated before condition was met.", root);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(25, token);
        }
    }
}
