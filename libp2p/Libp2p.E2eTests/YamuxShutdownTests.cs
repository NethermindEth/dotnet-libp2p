// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Yamux;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class YamuxShutdownTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RegisteredRequestResponseHandlerSurvivesGoAway(bool listener)
    {
        TaskCompletionSource<CancellationToken> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Pair pair = new(configure: builder => builder.AddRequestResponseProtocol<BytesValue, BytesValue>(
            async (request, _, token) =>
            {
                entered.TrySetResult(token);
                await release.Task.WaitAsync(token);
                return request;
            }, "/yamux-request-response-test/1.0.0", maxMessageSize: 1024 * 1024));
        await pair.Connect();
        int target = listener ? 1 : 0;
        int remote = 1 - target;
        byte[] bytes = new byte[600 * 1024];
        new Random(394).NextBytes(bytes);
        var request = new BytesValue { Value = ByteString.CopyFrom(bytes) };
        try
        {
            Task<BytesValue> response = pair.Sessions[remote]
                .DialAsync<RequestResponseProtocol<BytesValue, BytesValue>, BytesValue, BytesValue>(request, pair.Token);
            CancellationToken lifetime = await entered.Task.WaitAsync(pair.Token);
            Assert.That(lifetime.CanBeCanceled, Is.True);
            Boundary localWire = await pair.Muxers[target].Ready.Task.WaitAsync(pair.Token);
            Boundary remoteWire = await pair.Muxers[remote].Ready.Task.WaitAsync(pair.Token);
            await remoteWire.Send(3, 0, 0, 0, pair.Token);
            await remoteWire.Send(2, 1, 0, 394, pair.Token);
            await localWire.WaitFrame(2, 2, 394, pair.Token); // Receive-loop barrier after GoAway/OK.

            Assert.That(lifetime.IsCancellationRequested, Is.False);
            Assert.That(pair.Sessions[target].Closed.IsCancellationRequested, Is.False);
            Assert.That(response.IsCompleted, Is.False, "The handler has entered and is still waiting for release.");
            release.TrySetResult();
            Assert.That((await response.WaitAsync(pair.Token)).Value, Is.EqualTo(request.Value));
            Assert.ThrowsAsync<ChannelClosedException>(async () => await pair.Sessions[target]
                .DialAsync<RequestResponseProtocol<BytesValue, BytesValue>, BytesValue, BytesValue>(request, pair.Token)
                .WaitAsync(pair.Token));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RegisteredStackKeepsExistingApplicationTrafficAfterGoAway(bool listener)
    {
        await using Pair pair = new();
        await pair.Connect();
        int target = listener ? 1 : 0;
        int remote = 1 - target;
        int initialListeners = pair.Protocols[target].ListenerCalls;
        Request request = new();
        Task<int> dial = pair.Sessions[target].DialAsync<HoldProtocol, Request, int>(request, pair.Token);
        IChannel app = await request.Ready.Task.WaitAsync(pair.Token);
        Control peer = await pair.Protocols[remote].Accepted.Reader.ReadAsync(pair.Token);
        Boundary localWire = await pair.Muxers[target].Ready.Task.WaitAsync(pair.Token);
        Boundary remoteWire = await pair.Muxers[remote].Ready.Task.WaitAsync(pair.Token);
        Assert.That(pair.Muxers[target].Id, Is.EqualTo("/yamux/1.0.0"));
        Assert.That(pair.Muxers[target].IsListener, Is.EqualTo(listener));
        await localWire.WaitFrame(1, 1, 262144, pair.Token);
        await remoteWire.WaitFrame(1, 2, 262144, pair.Token);
        await remoteWire.Send(3, 0, 0, 0, pair.Token);
        await remoteWire.Send(2, 1, 0, 392, pair.Token);
        await localWire.WaitFrame(2, 2, 392, pair.Token); // Proves the production receive loop continued.

        byte[] bytes = new byte[600 * 1024];
        new Random(392).NextBytes(bytes);
        Task<byte[]> receive = ReadBytes(peer.Channel, bytes.Length, pair.Token);
        Assert.That(await app.WriteAsync(new ReadOnlySequence<byte>(bytes), pair.Token), Is.EqualTo(IOResult.Ok));
        Assert.That(await receive.WaitAsync(pair.Token), Is.EqualTo(bytes));
        receive = ReadBytes(app, bytes.Length, pair.Token);
        Assert.That(await peer.Channel.WriteAsync(new ReadOnlySequence<byte>(bytes), pair.Token), Is.EqualTo(IOResult.Ok));
        Assert.That(await receive.WaitAsync(pair.Token), Is.EqualTo(bytes));
        Assert.That(pair.Sessions[target].Closed.IsCancellationRequested, Is.False);

        for (int i = 0; i < 2; i++)
            Assert.ThrowsAsync<ChannelClosedException>(async () =>
                await pair.Sessions[target].DialAsync<HoldProtocol, Request, int>(new Request(), pair.Token).WaitAsync(pair.Token));
        Assert.ThrowsAsync<ChannelClosedException>(async () =>
            await pair.Sessions[remote].DialAsync<HoldProtocol, Request, int>(new Request(), pair.Token).WaitAsync(pair.Token));
        await localWire.WaitFrame(1, 8, 0, pair.Token);
        Assert.That(pair.Protocols[target].ListenerCalls, Is.EqualTo(initialListeners));

        Assert.That(await peer.Channel.WriteEofAsync(pair.Token), Is.EqualTo(IOResult.Ok));
        Assert.That((await app.ReadAsync(1, token: pair.Token)).Result, Is.EqualTo(IOResult.Ended));
        Assert.That(await app.WriteEofAsync(pair.Token), Is.EqualTo(IOResult.Ok));
        Assert.That((await peer.Channel.ReadAsync(1, token: pair.Token)).Result, Is.EqualTo(IOResult.Ended));
        request.Done.TrySetResult();
        peer.Done.TrySetResult();
        Assert.That(await dial.WaitAsync(pair.Token), Is.EqualTo(17));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RegisteredStackCountsInboundIndependently(bool listener)
    {
        await using Pair pair = new();
        await pair.Connect();
        int target = listener ? 1 : 0;
        int remote = 1 - target;
        int initialTarget = pair.Protocols[target].ListenerCalls;
        int initialRemote = pair.Protocols[remote].ListenerCalls;
        List<Request> requests = [];
        List<Control> controls = [];
        List<Task<int>> dials = [];
        try
        {
            // No application admission limit is registered. All 512 application streams stay open.
            for (int i = 0; i < 512; i++)
            {
                Request request = new();
                requests.Add(request);
                dials.Add(pair.Sessions[target].DialAsync<HoldProtocol, Request, int>(request, pair.Token));
                await request.Ready.Task.WaitAsync(pair.Token);
                controls.Add(await pair.Protocols[remote].Accepted.Reader.ReadAsync(pair.Token));
            }
            Request inbound = new();
            requests.Add(inbound);
            dials.Add(pair.Sessions[remote].DialAsync<HoldProtocol, Request, int>(inbound, pair.Token));
            await inbound.Ready.Task.WaitAsync(pair.Token);
            controls.Add(await pair.Protocols[target].Accepted.Reader.ReadAsync(pair.Token));
            Assert.That(pair.Protocols[target].ListenerCalls, Is.EqualTo(initialTarget + 1));
            Assert.That(pair.Protocols[remote].ListenerCalls, Is.EqualTo(initialRemote + 512));

            Assert.ThrowsAsync<ChannelClosedException>(async () =>
                await pair.Sessions[target].DialAsync<HoldProtocol, Request, int>(new Request(), pair.Token).WaitAsync(pair.Token));
            Boundary remoteWire = await pair.Muxers[remote].Ready.Task.WaitAsync(pair.Token);
            await remoteWire.WaitFrame(1, 8, 0, pair.Token);
            Assert.That(pair.Protocols[remote].ListenerCalls, Is.EqualTo(initialRemote + 512));
        }
        finally
        {
            foreach (Request request in requests) request.Done.TrySetResult();
            foreach (Control control in controls) control.Done.TrySetResult();
        }
        Assert.That(await Task.WhenAll(dials).WaitAsync(pair.Token), Is.All.EqualTo(17));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RegisteredReceiveWindowDoesNotGrantSendCredit(bool listener)
    {
        int target = listener ? 1 : 0;
        int remote = 1 - target;
        await using Pair pair = new(target);
        await pair.Connect();
        Boundary localWire = await pair.Muxers[target].Ready.Task.WaitAsync(pair.Token);
        Boundary remoteWire = await pair.Muxers[remote].Ready.Task.WaitAsync(pair.Token);
        remoteWire.SuppressCredit = true;
        Request request = new();
        Task<int> dial = pair.Sessions[target].DialAsync<HoldProtocol, Request, int>(request, pair.Token);
        IChannel app = await request.Ready.Task.WaitAsync(pair.Token);
        Control peer = await pair.Protocols[remote].Accepted.Reader.ReadAsync(pair.Token);
        int id = localWire.LastOpenedStream;
        int negotiationBytes = localWire.SentBytes(id);
        byte[] bytes = new byte[YamuxProtocol.ProtocolInitialWindowSize + 17];
        new Random(391).NextBytes(bytes);
        Task<byte[]> receive = ReadBytes(peer.Channel, bytes.Length, pair.Token);
        Assert.That(await app.WriteAsync(new ReadOnlySequence<byte>(bytes), pair.Token), Is.EqualTo(IOResult.Ok));
        Task waiting = pair.Muxers[target].Logger.CreditWaiting.Task;
        await Task.WhenAny(waiting, receive).WaitAsync(pair.Token);
        Assert.That(waiting.IsCompleted, Is.True, "The production pump must wait for credit before completing this transfer.");
        Assert.That(localWire.SentBytes(id), Is.EqualTo(YamuxProtocol.ProtocolInitialWindowSize));
        Assert.That(receive.IsCompleted, Is.False);
        await remoteWire.Send(3, 0, 0, 0, pair.Token);
        await remoteWire.Send(2, 1, 0, 391, pair.Token);
        await localWire.WaitFrame(2, 2, 391, pair.Token);
        int remaining = bytes.Length + negotiationBytes - YamuxProtocol.ProtocolInitialWindowSize;
        await remoteWire.Send(1, 0, id, remaining, pair.Token);
        Assert.That(await receive.WaitAsync(pair.Token), Is.EqualTo(bytes));
        Assert.That(localWire.SentBytes(id), Is.EqualTo(bytes.Length + negotiationBytes));
        request.Done.TrySetResult();
        peer.Done.TrySetResult();
        Assert.That(await dial.WaitAsync(pair.Token), Is.EqualTo(17));
    }

    private static async Task<byte[]> ReadBytes(IChannel channel, int count, CancellationToken token)
    {
        using MemoryStream bytes = new();
        while (bytes.Length < count)
        {
            ReadResult read = await channel.ReadAsync(count - (int)bytes.Length, ReadBlockingMode.WaitAny, token);
            Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
            bytes.Write(read.Data.ToArray());
        }
        return bytes.ToArray();
    }

    public sealed class Request
    {
        public TaskCompletionSource<IChannel> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed record Control(IChannel Channel, TaskCompletionSource Done);

    public sealed class HoldProtocol : ISessionProtocol<Request, int>
    {
        public string Id => "/yamux-shutdown-test/1.0.0";
        public global::System.Threading.Channels.Channel<Control> Accepted { get; } = global::System.Threading.Channels.Channel.CreateUnbounded<Control>();
        private int _listenerCalls;
        public int ListenerCalls => Volatile.Read(ref _listenerCalls);
        public async Task<int> DialAsync(IChannel channel, ISessionContext context, Request request)
        {
            request.Ready.TrySetResult(channel);
            await request.Done.Task.WaitAsync(context.UpgradeOptions?.CancellationToken ?? default);
            return 17;
        }
        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            Interlocked.Increment(ref _listenerCalls);
            TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Accepted.Writer.WriteAsync(new Control(channel, done));
            await done.Task.WaitAsync(channel.CancellationToken);
        }
    }

    // Keep the production TCP -> multistream -> Noise -> Yamux -> multistream registrations.
    // Omit automatic application streams so exactly 512 held streams exercise the transport cap.
    public sealed class Builder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider)
    {
        protected override ProtocolRef? Identify() => null;
        protected override ProtocolRef? IdentifyPush() => null;
        protected override ProtocolRef? Ping() => null;
    }

    public sealed class InspectedYamux : YamuxProtocol
    {
        public InspectedYamux(YamuxWindowSettings settings, MultiplexerSettings muxers) : this(settings, muxers, new CreditLogger()) { }
        private InspectedYamux(YamuxWindowSettings settings, MultiplexerSettings muxers, CreditLogger logger)
            : base(muxers, loggerFactory: logger, windowSettings: settings) => Logger = logger;
        public CreditLogger Logger { get; }
        public TaskCompletionSource<Boundary> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsListener { get; private set; }
        protected override Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener)
        {
            IsListener = isListener;
            Boundary boundary = new(channel);
            Ready.TrySetResult(boundary);
            return base.ConnectAsync(boundary, context, isListener);
        }
    }

    public sealed class CreditLogger : ILoggerFactory, ILogger
    {
        public TaskCompletionSource CreditWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string name) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).EndsWith(": Waiting for remote window credit", StringComparison.Ordinal))
                CreditWaiting.TrySetResult();
        }
    }

    public sealed class Boundary(IChannel inner) : IChannel
    {
        private readonly ConcurrentDictionary<int, int> _sent = new();
        public bool SuppressCredit { get; set; }
        public int LastOpenedStream { get; private set; }
        public int SentBytes(int id) => _sent.GetValueOrDefault(id);
        private readonly global::System.Threading.Channels.Channel<(byte Type, short Flags, int Length)> _written =
            global::System.Threading.Channels.Channel.CreateUnbounded<(byte, short, int)>();
        public async Task Send(byte type, short flags, int stream, int length, CancellationToken token)
        {
            byte[] bytes = new byte[12];
            bytes[1] = type;
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(2), flags);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), stream);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), length);
            Assert.That(await inner.WriteAsync(new ReadOnlySequence<byte>(bytes), token), Is.EqualTo(IOResult.Ok));
        }
        public async Task WaitFrame(byte type, short flags, int length, CancellationToken token)
        {
            while (await _written.Reader.ReadAsync(token) != (type, flags, length)) { }
        }
        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            byte[] header = bytes.Slice(0, 12).ToArray();
            int stream = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            short flags = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(2));
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8));
            if (header[1] == 1 && SuppressCredit)
            {
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), 0);
                bytes = new ReadOnlySequence<byte>(header);
            }
            if (stream != 0 && (flags & 1) != 0) LastOpenedStream = stream;
            if (header[1] == 0) _sent.AddOrUpdate(stream, length, (_, count) => count + length);
            IOResult result = await inner.WriteAsync(bytes, token);
            _written.Writer.TryWrite((header[1], BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(2)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8))));
            return result;
        }
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default) => inner.ReadAsync(length, blockingMode, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public ValueTask CloseReadAsync() => inner.CloseReadAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
    }

    private sealed class Pair(int? enlargedOnly = null, Func<IPeerFactoryBuilder, IPeerFactoryBuilder>? configure = null) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(45));
        public CancellationToken Token => _timeout.Token;
        private readonly List<ServiceProvider> _providers = [];
        private readonly List<ILocalPeer> _peers = [];
        public List<HoldProtocol> Protocols { get; } = [];
        public List<InspectedYamux> Muxers { get; } = [];
        public ISession[] Sessions { get; } = new ISession[2];
        public async Task Connect()
        {
            for (int i = 0; i < 2; i++)
            {
                ServiceProvider provider = new ServiceCollection()
                    .AddLibp2p<Builder>(builder => configure is null
                        ? builder.AddProtocol<HoldProtocol>()
                        : configure(builder.AddProtocol<HoldProtocol>()))
                    .AddSingleton<HoldProtocol>()
                    .AddSingleton(new YamuxWindowSettings { InitialWindowSize = enlargedOnly is null || enlargedOnly == i ? 524288 : 262144 })
                    .AddSingleton<InspectedYamux>()
                    .AddSingleton<YamuxProtocol>(sp => sp.GetRequiredService<InspectedYamux>())
                    .BuildServiceProvider();
                _providers.Add(provider);
                Protocols.Add(provider.GetRequiredService<HoldProtocol>());
                Muxers.Add(provider.GetRequiredService<InspectedYamux>());
                ILocalPeer peer = provider.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(i));
                _peers.Add(peer);
                await peer.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], Token);
            }
            TaskCompletionSource<ISession> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _peers[1].OnConnected += session => connected.TrySetResult(session);
            Sessions[0] = await _peers[0].DialAsync([.. _peers[1].ListenAddresses]);
            // Listener session creation is lazy: bootstrap one real application stream.
            Request bootstrap = new();
            Task<int> dial = Sessions[0].DialAsync<HoldProtocol, Request, int>(bootstrap, Token);
            await bootstrap.Ready.Task.WaitAsync(Token);
            Control accepted = await Protocols[1].Accepted.Reader.ReadAsync(Token);
            Sessions[1] = await connected.Task.WaitAsync(Token);
            bootstrap.Done.TrySetResult();
            accepted.Done.TrySetResult();
            await dial.WaitAsync(Token);
            // The bootstrap FINs must both pass before filling the cap. Await their stream teardown
            // by starting the next test only after the application channel has closed in both directions.
            await accepted.Channel;
        }
        public async ValueTask DisposeAsync()
        {
            _timeout.Cancel();
            foreach (ILocalPeer peer in _peers) await peer.DisposeAsync();
            foreach (ServiceProvider provider in _providers) await provider.DisposeAsync();
            _timeout.Dispose();
        }
    }
}
