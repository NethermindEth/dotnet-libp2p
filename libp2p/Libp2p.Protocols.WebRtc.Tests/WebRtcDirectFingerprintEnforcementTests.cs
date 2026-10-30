// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using NSubstitute;
using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
[Category("Integration")]
public class WebRtcDirectFingerprintEnforcementTests
{
    private const string Rejection = "Remote DTLS fingerprint does not match /certhash.";

    // These are required Linux CI controls over real signaling, DTLS, data channels and Noise.
    // The boundary observes the registered transport's Upgrade call, not a working libp2p session.
    [TestCase(false, "matching")]
    [TestCase(false, "null")]
    [TestCase(false, "digest")]
    [TestCase(true, "matching")]
    [TestCase(true, "null")]
    [TestCase(true, "digest")]
    public async Task RegisteredTransport_EnforcesRemoteFingerprintBeforeUpgrade(bool listener, string fingerprint)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        using CancellationTokenSource operations = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Observation dialer = new(listener ? "matching" : fingerprint, operations.Token, release.Task);
        Observation acceptor = new(listener ? fingerprint : "matching", operations.Token, release.Task);
        bool injectFingerprint = fingerprint != "matching";
        await using ServiceProvider dialerServices = Services(dialer, injectFingerprint);
        await using ServiceProvider listenerServices = Services(acceptor, injectFingerprint);
        await using ILocalPeer dialerPeer = dialerServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer listenerPeer = listenerServices.GetRequiredService<IPeerFactory>().Create();
        WebRtcDirectProtocol dialerProtocol = RegisteredTransport(dialerServices, injectFingerprint);
        WebRtcDirectProtocol listenerProtocol = RegisteredTransport(listenerServices, injectFingerprint);
        ITransportContext dialerContext = dialer.Context(dialerPeer);
        ITransportContext listenerContext = acceptor.Context(listenerPeer);

        // Publish both certificates through the production listener. The reader assertions use
        // these independently obtained expected fingerprints, rather than comparing a value to itself.
        Task dialerListen = dialerProtocol.ListenAsync(dialerContext, "/ip4/127.0.0.1/udp/0/webrtc-direct", operations.Token);
        Task listenerListen = listenerProtocol.ListenAsync(listenerContext, "/ip4/127.0.0.1/udp/0/webrtc-direct", operations.Token);
        Task? dial = null;
        Exception? dialFailure = null;
        Exception? listenerFailure = null;
        try
        {
            Multiaddress dialerAddress = await dialer.Ready.Task.WaitAsync(deadline.Token);
            Multiaddress listenerAddress = await acceptor.Ready.Task.WaitAsync(deadline.Token);
            dialer.Expected = WebRtcDirectMultiaddr.Parse(listenerAddress).ExpectedFingerprint;
            acceptor.Expected = WebRtcDirectMultiaddr.Parse(dialerAddress).ExpectedFingerprint;
            dial = dialerProtocol.DialAsync(dialerContext, listenerAddress, operations.Token);

            if (fingerprint == "matching")
            {
                Task bothUpgraded = Task.WhenAll(dialer.Upgraded.Task, acceptor.Upgraded.Task);
                Task completed = await Task.WhenAny(bothUpgraded, dial, acceptor.FailedOffer.Task).WaitAsync(deadline.Token);
                if (completed == dial) await dial;
                if (completed == acceptor.FailedOffer.Task) Assert.Fail((await acceptor.FailedOffer.Task).ToString());
                await bothUpgraded.WaitAsync(deadline.Token);
                AssertAuthenticated(dialer, listenerPeer.Identity);
                AssertAuthenticated(acceptor, dialerPeer.Identity);
            }
            else if (listener)
            {
                // Incoming-offer failures are caught by production code. Observe the actual
                // logged exception, then the peer connection closing in its finally block.
                Task completed = await Task.WhenAny(acceptor.FailedOffer.Task, acceptor.Upgraded.Task, dial).WaitAsync(deadline.Token);
                Assert.That(completed, Is.SameAs(acceptor.FailedOffer.Task), "Listener must reject the certificate before Upgrade or successful dial completion.");
                listenerFailure = await acceptor.FailedOffer.Task;
                await acceptor.Closed.Task.WaitAsync(deadline.Token);
            }
            else
            {
                Task completed = await Task.WhenAny(dial, dialer.Upgraded.Task).WaitAsync(deadline.Token);
                Assert.That(completed, Is.SameAs(dial), "Dialer must reject the certificate before Upgrade.");
                dialFailure = await Failure(dial, deadline.Token);
            }
        }
        finally
        {
            release.TrySetResult();
            await operations.CancelAsync();
            if (dial is not null)
            {
                dialFailure ??= await Failure(dial, CancellationToken.None);
            }
            await Task.WhenAll(dialerListen, listenerListen).WaitAsync(TimeSpan.FromSeconds(5));
            await dialer.FinishAsync();
            await acceptor.FinishAsync();
        }

        Assert.That(deadline.IsCancellationRequested, Is.False, "Transport deadline expired.");
        Observation target = listener ? acceptor : dialer;
        if (fingerprint == "matching")
        {
            Assert.That(dialer.Reads, Is.Zero, "Positive controls must use the production fingerprint reader.");
            Assert.That(acceptor.Reads, Is.Zero, "Positive controls must use the production fingerprint reader.");
            Assert.That(dialFailure, Is.Null);
            Assert.That(acceptor.FailedOffer.Task.IsCompleted, Is.False);
            Assert.That(dialer.Connections, Is.EqualTo(1));
            Assert.That(acceptor.Connections, Is.EqualTo(1));
        }
        else
        {
            AssertRead(target);
            Exception? failure = listener ? listenerFailure : dialFailure;
            Assert.That(failure, Is.TypeOf<InvalidOperationException>().With.Message.EqualTo(Rejection));
            Assert.That(target.Connections, Is.Zero, "Rejected certificates must not create a connection.");
            Assert.That(target.Upgraded.Task.IsCompleted, Is.False, "Rejected certificates must not reach Upgrade.");
        }
    }

    private static ServiceProvider Services(Observation observation, bool injectFingerprint)
    {
        IServiceCollection services = new ServiceCollection()
            .AddLibp2p(builder => builder.WithWebRtcDirect())
            .AddSingleton<ILoggerFactory>(new OfferLoggerFactory(observation));
        // Matching controls leave transport construction to the normal builder/public constructor.
        // Only rejection controls replace the runtime read to inject a null or changed fingerprint.
        if (injectFingerprint)
        {
            services.AddSingleton(provider => new WebRtcDirectProtocol(observation.Read, provider.GetRequiredService<ILoggerFactory>()));
        }
        return services.BuildServiceProvider();
    }

    private static WebRtcDirectProtocol RegisteredTransport(ServiceProvider services, bool injectFingerprint)
    {
        IProtocolStackSettings stack = services.GetRequiredService<IProtocolStackSettings>();
        WebRtcDirectProtocol protocol = (WebRtcDirectProtocol)stack.TopProtocols!
            .Single(reference => reference.Protocol is WebRtcDirectProtocol).Protocol;
        if (injectFingerprint)
        {
            Assert.That(protocol, Is.SameAs(services.GetRequiredService<WebRtcDirectProtocol>()));
        }
        else
        {
            Assert.That(services.GetService<WebRtcDirectProtocol>(), Is.Null, "The builder must construct the transport without a test registration.");
        }
        Assert.That(stack.Protocols![stack.TopProtocols!.Single(reference => reference.Protocol == protocol)]
            .Select(reference => reference.Protocol).Single(), Is.InstanceOf<MultistreamProtocol>());
        return protocol;
    }

    private static void AssertRead(Observation observation)
    {
        Assert.That(observation.Reads, Is.EqualTo(1), "Production must read the remote fingerprint at the enforcement boundary.");
        Assert.That(observation.Actual, Is.Not.Null, "The runtime must expose a DTLS fingerprint.");
        Assert.That(observation.Expected!.Matches(observation.Actual!), Is.True,
            "Real DTLS and the advertised certificate must match before fault injection.");
    }

    private static void AssertAuthenticated(Observation observation, Identity expected)
    {
        Assert.That(observation.Upgraded.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(observation.State.RemotePublicKey, Is.Not.Null, "Upgrade must follow Noise authentication.");
        Assert.That(new Identity(observation.State.RemotePublicKey!).PeerId, Is.EqualTo(expected.PeerId));
    }

    private static async Task<Exception?> Failure(Task task, CancellationToken token)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return null;
        }
        catch (Exception ex) when (task.IsCompleted)
        {
            return ex;
        }
    }

    private sealed class Observation(string fingerprint, CancellationToken token, Task release)
    {
        public TaskCompletionSource<Multiaddress> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Upgraded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception> FailedOffer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public State State { get; } = new();
        public DtlsFingerprint? Expected { get; set; }
        public RTCDtlsFingerprint? Actual { get; private set; }
        private Task? _channelClosed;
        public int Reads;
        public int Connections;

        public RTCDtlsFingerprint? Read(RTCPeerConnection pc)
        {
            Interlocked.Increment(ref Reads);
            pc.onconnectionstatechange += state =>
            {
                if (state == RTCPeerConnectionState.closed) Closed.TrySetResult();
            };
            Actual = pc.RemotePeerDtlsFingerprint;
            if (fingerprint == "null") return null;
            if (fingerprint != "digest" || Actual is null) return Actual;
            DtlsFingerprint actual = DtlsFingerprint.FromRtcFingerprint(Actual);
            byte[] changed = [.. actual.Value];
            changed[0] ^= 1;
            return new RTCDtlsFingerprint { algorithm = Actual.algorithm, value = (actual with { Value = changed }).ToSdpString().Split(' ')[1] };
        }

        public ITransportContext Context(ILocalPeer peer)
        {
            ITransportContext context = Substitute.For<ITransportContext>();
            context.Peer.Returns(peer);
            context.When(c => c.ListenerReady(Arg.Any<Multiaddress>())).Do(call => Ready.TrySetResult(call.Arg<Multiaddress>()));
            context.CreateConnection().Returns(_ =>
            {
                Interlocked.Increment(ref Connections);
                INewConnectionContext connection = Substitute.For<INewConnectionContext>();
                connection.Peer.Returns(peer);
                connection.Token.Returns(token);
                connection.State.Returns(State);
                connection.Upgrade(Arg.Any<IChannel>(), Arg.Any<UpgradeOptions?>()).Returns(call =>
                {
                    _channelClosed = WaitForChannelCloseAsync(call.Arg<IChannel>());
                    Upgraded.TrySetResult();
                    return release;
                });
                connection.When(c => c.Dispose()).Do(_ => Disposed.TrySetResult());
                return connection;
            });
            return context;
        }

        public async Task FinishAsync()
        {
            // Closure follows the guard rejection/upgrade and its cleanup, so absence of an
            // upgrade is asserted after real progress, never inferred from waiting a while.
            if (Volatile.Read(ref Reads) != 0) await Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (Volatile.Read(ref Connections) != 0) await Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (_channelClosed is not null) await _channelClosed.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private static async Task WaitForChannelCloseAsync(IChannel channel) => await channel;
    }

    private sealed class OfferLoggerFactory(Observation observation) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new OfferLogger(observation);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class OfferLogger(Observation observation) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) observation.FailedOffer.TrySetResult(exception);
        }
    }
}
