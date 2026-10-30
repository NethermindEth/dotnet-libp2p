// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using NSubstitute;
using SIPSorcery.Net;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

/// <summary>
/// What each half of this transport does with the DTLS certificate its peer presents, driven
/// through the transport the libp2p builder actually registers: real ICE, real DTLS, real data
/// channels and a real Noise handshake, on CI with no browser and no second implementation.
/// <para>
/// The two halves enforce different things, which is why they are separate tests rather than one
/// parameterised over a <c>listener</c> flag. The dialer holds a <c>/certhash</c> from the
/// multiaddr and compares it against the certificate the peer actually presented. The listener
/// dialled nobody, so it has no claim to compare anything against; what binds the certificate it
/// saw to the peer's libp2p identity is the Noise prologue, and a listener whose view of that
/// fingerprint is wrong simply cannot finish the handshake. Asserting a comparison on the listen
/// side would be asserting a check that should not exist there.
/// </para>
/// <para>
/// Both fingerprint readers are injected through the internal constructor, for the same reason on
/// both halves: a real peer cannot be made to present a certificate that disagrees with what it
/// advertised, so the only way to cover the enforcement is to perturb the value the production code
/// reads. The positive control asserts that neither reader was called, so it cannot pass by
/// accident on a transport the test built rather than the builder.
/// </para>
/// </summary>
/// <remarks>
/// These cover behaviour that already existed, so passing proves nothing by itself; what gives them
/// teeth is mutation. Verified at the commit that introduced them, each restored byte-identical:
/// <list type="bullet">
/// <item>dropping the dialer's <c>ValidateRemoteFingerprint</c> call kills both
/// <see cref="Dialer_RefusesACertificateThatDoesNotMatchTheCerthash"/> cases.</item>
/// <item>building the listener's prologue from its own fingerprint twice, so it no longer depends
/// on the remote one, kills
/// <see cref="RegisteredTransport_AuthenticatesBothHalvesWithTheProductionReaders"/>.</item>
/// <item>having the listener read <c>session.RemoteFingerprint</c> directly instead of the value it
/// was handed kills both
/// <see cref="Listener_CannotFinishTheHandshakeWithTheWrongRemoteFingerprint"/> cases.</item>
/// </list>
/// Worth knowing which way round that falls: the listener case does NOT discriminate on its own,
/// because a handshake that is broken outright also fails to complete. It is the pair that pins the
/// binding -- the control shows the handshake completes on the true fingerprint, the corruption
/// case shows one flipped bit stops it -- and the second mutation above is what proves the prologue
/// is the thing carrying the value. Do not delete one and keep the other.
/// </remarks>
[TestFixture]
[Category("Integration")]
public class WebRtcDirectFingerprintEnforcementTests
{
    private const string CerthashRejection = "Remote DTLS fingerprint does not match /certhash.";
    private const string NothingPublished =
        "Established WebRTC-Direct session published no fingerprint, muxer or peer address.";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The dial half's enforcement: the <c>/certhash</c> is a claim made by whoever handed us the
    /// multiaddr, and this is the one place the certificate presented in DTLS is checked against it.
    /// It must happen before any connection exists, not after.
    /// </summary>
    [TestCase("null")]
    [TestCase("digest")]
    public async Task Dialer_RefusesACertificateThatDoesNotMatchTheCerthash(string fingerprint)
    {
        await using Peers peers = await Peers.StartAsync(dialerReader: fingerprint, listenerReader: "matching");

        Exception? failure = await peers.FailedDialAsync();

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.TypeOf<InvalidOperationException>().With.Message.EqualTo(CerthashRejection));
            AssertReadTheRealCertificate(peers.Dialer);
            Assert.That(peers.Dialer.Connections, Is.Zero, "A refused certificate must not produce a connection.");
            Assert.That(peers.Dialer.Session.Task.IsCompleted, Is.False, "A refused certificate must not reach a session.");
        });
    }

    /// <summary>
    /// The listen half's enforcement, which is a binding rather than a comparison. The fingerprint
    /// the listener saw in DTLS goes into the Noise prologue
    /// (<see cref="WebRtcNoisePrologue.Build"/>), so it is mixed into the handshake hash: a listener
    /// holding the wrong value fails to decrypt the dialer's reply and no connection is ever
    /// upgraded to a session. This is what replaces the <c>/certhash</c> check on this side.
    /// </summary>
    /// <remarks>
    /// The two cases fail at different points on purpose. A missing fingerprint is refused before
    /// the connection is even created; a corrupted one gets as far as the handshake and dies inside
    /// Noise, which is the case that proves the prologue is actually carrying the value.
    /// </remarks>
    [TestCase("null")]
    [TestCase("digest")]
    public async Task Listener_CannotFinishTheHandshakeWithTheWrongRemoteFingerprint(string fingerprint)
    {
        await using Peers peers = await Peers.StartAsync(dialerReader: "matching", listenerReader: fingerprint);

        Task dial = peers.Dial();
        Exception failure = await peers.Listener.Failed.Task.WaitAsync(Deadline);

        Assert.Multiple(() =>
        {
            Assert.That(peers.Listener.Session.Task.IsCompleted, Is.False,
                "A listener that cannot bind the certificate must not reach a session.");
            Assert.That(peers.Dialer.Session.Task.IsCompleted, Is.False,
                "Nor may the dialer, since the handshake never completed.");
            if (fingerprint == "null")
            {
                Assert.That(failure, Is.TypeOf<InvalidOperationException>().With.Message.EqualTo(NothingPublished));
                Assert.That(peers.Listener.Connections, Is.Zero,
                    "A session that published no fingerprint is refused before a connection exists.");
            }
            else
            {
                Assert.That(failure, Is.Not.TypeOf<InvalidOperationException>().Or.Message.Not.EqualTo(NothingPublished),
                    "A corrupted fingerprint must get as far as the handshake, not be caught by the null check.");
                AssertReadTheRealCertificate(peers.Listener);
            }
        });

        Assert.That(dial.IsCompletedSuccessfully, Is.False, "The dial must not report success.");
    }

    /// <summary>
    /// The control the other two rest on: with nothing injected, the transport the builder
    /// registered authenticates both halves and both reach a session. It also pins that the
    /// positive path uses the production readers, so neither rejection test can be passing because
    /// the test built its own transport.
    /// </summary>
    [Test]
    public async Task RegisteredTransport_AuthenticatesBothHalvesWithTheProductionReaders()
    {
        await using Peers peers = await Peers.StartAsync(dialerReader: "matching", listenerReader: "matching");

        Task dial = peers.Dial();
        Task bothSessions = Task.WhenAll(peers.Listener.Session.Task, peers.Dialer.Session.Task);
        if (await Task.WhenAny(bothSessions, dial).WaitAsync(Deadline) == dial)
        {
            await dial;
            Assert.Fail("The dial finished before either half upgraded to a session.");
        }

        await bothSessions;

        Assert.Multiple(() =>
        {
            Assert.That(peers.Dialer.Reads, Is.Zero, "The positive path must use the production fingerprint reader.");
            Assert.That(peers.Listener.SessionReads, Is.Zero, "The positive path must use the production session reader.");
            Assert.That(new Identity(peers.Dialer.State.RemotePublicKey!).PeerId, Is.EqualTo(peers.ListenerIdentity.PeerId),
                "The dialer must have authenticated the listener's libp2p identity.");
            Assert.That(new Identity(peers.Listener.State.RemotePublicKey!).PeerId, Is.EqualTo(peers.DialerIdentity.PeerId),
                "And the listener the dialer's.");
            Assert.That(peers.Listener.Failed.Task.IsCompleted, Is.False, "Nothing may have been logged as a failure.");
        });
    }

    /// <summary>
    /// Fault injection is only meaningful if the production code was reading the right value to
    /// begin with, so every rejection case asserts that the reader ran once and that what it saw
    /// matched the certificate the peer advertised in its own listen address.
    /// </summary>
    private static void AssertReadTheRealCertificate(Observation observation)
    {
        Assert.That(observation.Reads + observation.SessionReads, Is.EqualTo(1),
            "Production must read the remote fingerprint exactly once, at the enforcement boundary.");
        Assert.That(observation.Observed, Is.Not.Null, "The runtime must expose a DTLS fingerprint.");
        Assert.That(observation.Expected!.Value, Is.EqualTo(observation.Observed!.Value),
            "Real DTLS and the advertised certificate must agree before anything is perturbed.");
    }

    /// <summary>
    /// Both halves, each built by the libp2p builder and each listening, so that the certificate
    /// every peer advertises comes out of the production listener rather than out of the test. That
    /// is what makes the expected fingerprints independent of the values being asserted.
    /// </summary>
    private sealed class Peers : IAsyncDisposable
    {
        private readonly ServiceProvider _dialerServices;
        private readonly ServiceProvider _listenerServices;
        private readonly ILocalPeer _dialerPeer;
        private readonly ILocalPeer _listenerPeer;
        private readonly WebRtcDirectProtocol _dialerProtocol;
        private readonly CancellationTokenSource _operations = new();
        private Task _dialerListen = Task.CompletedTask;
        private Task _listenerListen = Task.CompletedTask;
        private Task? _dial;

        private Peers(
            ServiceProvider dialerServices,
            ServiceProvider listenerServices,
            ILocalPeer dialerPeer,
            ILocalPeer listenerPeer,
            WebRtcDirectProtocol dialerProtocol,
            Observation dialer,
            Observation listener)
        {
            _dialerServices = dialerServices;
            _listenerServices = listenerServices;
            _dialerPeer = dialerPeer;
            _listenerPeer = listenerPeer;
            _dialerProtocol = dialerProtocol;
            Dialer = dialer;
            Listener = listener;
        }

        public Observation Dialer { get; }

        public Observation Listener { get; }

        public Multiaddress ListenerAddress { get; private set; } = null!;

        public Identity DialerIdentity => _dialerPeer.Identity;

        public Identity ListenerIdentity => _listenerPeer.Identity;

        public static async Task<Peers> StartAsync(string dialerReader, string listenerReader)
        {
            Observation dialer = new(dialerReader);
            Observation listener = new(listenerReader);
            ServiceProvider dialerServices = Services(dialer);
            ServiceProvider listenerServices = Services(listener);
            ILocalPeer dialerPeer = dialerServices.GetRequiredService<IPeerFactory>().Create();
            ILocalPeer listenerPeer = listenerServices.GetRequiredService<IPeerFactory>().Create();
            WebRtcDirectProtocol dialerProtocol = RegisteredTransport(dialerServices, dialer);
            WebRtcDirectProtocol listenerProtocol = RegisteredTransport(listenerServices, listener);

            Peers peers = new(dialerServices, listenerServices, dialerPeer, listenerPeer, dialerProtocol, dialer, listener);

            // Both halves listen, so the certificate each one advertises is published by the
            // production listener. That is what makes the expected fingerprints independent of the
            // values under assertion -- nothing is compared against itself.
            peers._dialerListen = dialerProtocol.ListenAsync(
                dialer.Context(dialerPeer), "/ip4/127.0.0.1/udp/0/webrtc-direct", peers._operations.Token);
            peers._listenerListen = listenerProtocol.ListenAsync(
                listener.Context(listenerPeer), "/ip4/127.0.0.1/udp/0/webrtc-direct", peers._operations.Token);

            Multiaddress dialerAddress = await dialer.Ready.Task.WaitAsync(Deadline);
            peers.ListenerAddress = await listener.Ready.Task.WaitAsync(Deadline);
            dialer.Expected = WebRtcDirectMultiaddr.Parse(peers.ListenerAddress).ExpectedFingerprint;
            listener.Expected = WebRtcDirectMultiaddr.Parse(dialerAddress).ExpectedFingerprint;
            return peers;
        }

        public Task Dial() => _dial ??= _dialerProtocol.DialAsync(Dialer.Context(_dialerPeer), ListenerAddress, _operations.Token);

        /// <summary>Dials and returns the failure, which on the dial half is how a refusal surfaces.</summary>
        public async Task<Exception?> FailedDialAsync()
        {
            try
            {
                await Dial().WaitAsync(Deadline);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _operations.CancelAsync();
            await IgnoreAsync(_dial);
            await IgnoreAsync(_dialerListen);
            await IgnoreAsync(_listenerListen);
            await _dialerPeer.DisposeAsync();
            await _listenerPeer.DisposeAsync();
            await _dialerServices.DisposeAsync();
            await _listenerServices.DisposeAsync();
            _operations.Dispose();
        }

        /// <summary>
        /// Teardown observes every task it started -- a faulted one left unobserved would reach
        /// TaskScheduler.UnobservedTaskException and be attributed to whatever test ran next -- but
        /// asserts on none of them: the assertions have already run by the time this is reached.
        /// </summary>
        private static async Task IgnoreAsync(Task? task)
        {
            if (task is null)
            {
                return;
            }

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
            }
        }

        private static ServiceProvider Services(Observation observation)
        {
            IServiceCollection services = new ServiceCollection()
                .AddLibp2p(builder => builder.WithWebRtcDirect())
                .AddSingleton<ILoggerFactory>(new FailureLoggerFactory(observation));

            // A half with nothing to inject is left entirely to the builder and the public
            // constructor, which is what the positive control checks.
            if (observation.Injects)
            {
                services.AddSingleton(provider => new WebRtcDirectProtocol(
                    observation.Read, observation.ReadSession, provider.GetRequiredService<ILoggerFactory>()));
            }

            return services.BuildServiceProvider();
        }

        private static WebRtcDirectProtocol RegisteredTransport(ServiceProvider services, Observation observation)
        {
            IProtocolStackSettings stack = services.GetRequiredService<IProtocolStackSettings>();
            WebRtcDirectProtocol protocol = (WebRtcDirectProtocol)stack.TopProtocols!
                .Single(reference => reference.Protocol is WebRtcDirectProtocol).Protocol;

            if (observation.Injects)
            {
                Assert.That(protocol, Is.SameAs(services.GetRequiredService<WebRtcDirectProtocol>()));
            }
            else
            {
                Assert.That(services.GetService<WebRtcDirectProtocol>(), Is.Null,
                    "The builder must construct the transport without a test registration.");
            }

            Assert.That(stack.Protocols![stack.TopProtocols!.Single(reference => reference.Protocol == protocol)]
                .Select(reference => reference.Protocol).Single(), Is.InstanceOf<MultistreamProtocol>());
            return protocol;
        }
    }

    /// <summary>
    /// One half's view: the address it published, the fingerprint it read, whether it reached a
    /// session, and anything its logger was handed as a failure.
    /// </summary>
    private sealed class Observation(string fingerprint)
    {
        private readonly BlockingCollection<UpgradeOptions> _requests = new();
        private ITransportContext? _context;

        public TaskCompletionSource<Multiaddress> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when this half hands an authenticated connection to a session.</summary>
        public TaskCompletionSource Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The exception production code logged, which is how the listen half reports a refusal.</summary>
        public TaskCompletionSource<Exception> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public State State { get; } = new();

        /// <summary>Whether this half's fingerprint readers are replaced at all.</summary>
        public bool Injects => fingerprint != "matching";

        /// <summary>The certificate the peer advertised, taken from its published listen address.</summary>
        public DtlsFingerprint? Expected { get; set; }

        /// <summary>What production actually read, before this class perturbed it.</summary>
        public DtlsFingerprint? Observed { get; private set; }

        public int Reads;
        public int SessionReads;
        public int Connections;

        /// <summary>The dial half's reader: a certificate from a live <see cref="RTCPeerConnection"/>.</summary>
        public RTCDtlsFingerprint? Read(RTCPeerConnection pc)
        {
            Interlocked.Increment(ref Reads);
            RTCDtlsFingerprint? actual = pc.RemotePeerDtlsFingerprint;
            Observed = actual is null ? null : DtlsFingerprint.FromRtcFingerprint(actual);
            if (fingerprint == "null" || actual is null)
            {
                return null;
            }

            if (fingerprint != "digest")
            {
                return actual;
            }

            DtlsFingerprint corrupted = Corrupt(Observed!);
            return new RTCDtlsFingerprint { algorithm = actual.algorithm, value = corrupted.ToSdpString().Split(' ')[1] };
        }

        /// <summary>The listen half's reader: the certificate the DTLS server saw for this session.</summary>
        public DtlsFingerprint? ReadSession(WebRtcDirectSession session)
        {
            Interlocked.Increment(ref SessionReads);
            Observed = session.RemoteFingerprint;
            if (fingerprint == "null" || Observed is null)
            {
                return null;
            }

            return fingerprint == "digest" ? Corrupt(Observed) : Observed;
        }

        public ITransportContext Context(ILocalPeer peer)
        {
            if (_context is not null)
            {
                return _context;
            }

            ITransportContext context = Substitute.For<ITransportContext>();
            context.Peer.Returns(peer);
            context.When(c => c.ListenerReady(Arg.Any<Multiaddress>())).Do(call => Ready.TrySetResult(call.Arg<Multiaddress>()));
            context.CreateConnection().Returns(_ =>
            {
                Interlocked.Increment(ref Connections);
                INewConnectionContext connection = Substitute.For<INewConnectionContext>();
                INewSessionContext session = Substitute.For<INewSessionContext>();
                connection.Peer.Returns(peer);
                connection.State.Returns(State);
                connection.UpgradeToSession().Returns(_ =>
                {
                    Session.TrySetResult();
                    return session;
                });
                session.State.Returns(State);
                session.DialRequests.Returns(_requests.GetConsumingEnumerable());
                return connection;
            });

            return _context = context;
        }

        /// <summary>One bit of the digest, which is all it takes: the prologue covers every bit.</summary>
        private static DtlsFingerprint Corrupt(DtlsFingerprint fingerprint)
        {
            byte[] changed = [.. fingerprint.Value];
            changed[0] ^= 1;
            return fingerprint with { Value = changed };
        }
    }

    private sealed class FailureLoggerFactory(Observation observation) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new FailureLogger(observation);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    /// <summary>
    /// The listen half catches its own failures and logs them -- a listener may not let one
    /// peer's bad handshake escape into its accept loop -- so the log is where the refusal is
    /// observable. The exception itself is captured, not a message, so the assertion is on what
    /// actually went wrong.
    /// </summary>
    private sealed class FailureLogger(Observation observation) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                observation.Failed.TrySetResult(exception);
            }
        }
    }
}
