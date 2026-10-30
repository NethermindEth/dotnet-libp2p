// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using NSubstitute;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcDirectProtocolTests
{
    private static readonly DtlsFingerprint Expected = new("sha-256", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [TestCase("sha-256")]
    [TestCase("SHA-256")]
    public void ValidateRemoteFingerprint_AcceptsMatchingCertificate(string algorithm)
    {
        SIPSorcery.Net.RTCDtlsFingerprint remote = new() { algorithm = algorithm, value = Expected.ToSdpString().Split(' ')[1] };
        Assert.That(() => WebRtcDirectProtocol.ValidateRemoteFingerprint(remote, Expected), Throws.Nothing);
    }

    [TestCase("null")]
    [TestCase("digest")]
    [TestCase("algorithm")]
    public void ValidateRemoteFingerprint_RejectsUnboundCertificate(string change)
    {
        SIPSorcery.Net.RTCDtlsFingerprint? remote = change == "null" ? null : new()
        {
            algorithm = change == "algorithm" ? "sha-384" : Expected.Algorithm,
            value = (change == "digest" ? Expected with { Value = new byte[32] } : Expected).ToSdpString().Split(' ')[1]
        };
        Assert.That(() => WebRtcDirectProtocol.ValidateRemoteFingerprint(remote, Expected),
            Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("Remote DTLS fingerprint does not match /certhash."));
    }

    [Test]
    public void Constructor_CreatesLocalDtlsCertificate()
    {
        Assert.That(() => new WebRtcDirectProtocol(), Throws.Nothing);
    }

    /// <summary>
    /// The cap has to live in the session loop, not in the muxer, because only the listen half has
    /// a muxer. On the dial half SIPSorcery allocates an inbound <c>RTCDataChannel</c> before this
    /// transport ever sees it, so without a cap here a peer we dialled could hold an unbounded
    /// number of live streams -- each one a data channel, a polling timer, a frame buffer and an
    /// <c>Upgrade</c> into the protocol stack. <see cref="InboundStreamLimit"/> does not cover it:
    /// it is opt-in per protocol and is applied after the stream has already been built and
    /// negotiated.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Session_CarriesInboundStreamsUpToTheCapAndClosesTheRest(CancellationToken token)
    {
        const int overCap = WebRtcDirectSctpMuxer.MaxConcurrentChannels + 4;
        WebRtcDirectProtocol protocol = new();
        SessionHarness harness = new();

        FakeWebRtcDataChannel[] inbound = [.. Enumerable.Range(0, overCap)
            .Select(i => new FakeWebRtcDataChannel { StreamId = (ushort)(i * 2 + 2) })];
        foreach (FakeWebRtcDataChannel channel in inbound)
        {
            harness.Offer(channel);
        }

        harness.StopOfferingStreams();
        await protocol.RunSessionAsync(harness.Connection, harness.OpenStream, harness.InboundStreams, token);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Upgraded, Is.EqualTo(WebRtcDirectSctpMuxer.MaxConcurrentChannels),
                "Every stream up to the cap must reach the protocol stack.");
            Assert.That(inbound.Take(WebRtcDirectSctpMuxer.MaxConcurrentChannels).Select(c => c.IsClosed),
                Is.All.False, "Admitted streams must stay open.");
            Assert.That(inbound.Skip(WebRtcDirectSctpMuxer.MaxConcurrentChannels).Select(c => c.IsClosed),
                Is.All.True, "A refused stream must be closed, not left for the peer to wonder about.");
        });

        harness.Release();
    }

    /// <summary>
    /// A stream this transport accepted is nobody else's to cancel: the dial side of a stream has a
    /// caller holding its <see cref="UpgradeOptions.CancellationToken"/>, an accepted one has
    /// nobody. Without a token on the options, <c>Peer.Upgrade</c> has no path to abort the channel
    /// (it registers on exactly that token), so a session that ends leaves every stream it accepted
    /// running until the data channel underneath happens to die.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Session_GivesEveryStreamItAcceptsACancellationPath(CancellationToken token)
    {
        WebRtcDirectProtocol protocol = new();
        SessionHarness harness = new();

        harness.Offer(new FakeWebRtcDataChannel { StreamId = 2 });
        harness.StopOfferingStreams();
        await protocol.RunSessionAsync(harness.Connection, harness.OpenStream, harness.InboundStreams, token);

        UpgradeOptions accepted = harness.AcceptedOptions.Single();
        Assert.Multiple(() =>
        {
            Assert.That(accepted.ModeOverride, Is.EqualTo(UpgradeModeOverride.Listen));
            Assert.That(accepted.CancellationToken.CanBeCanceled, Is.True,
                "An accepted stream must carry a token, or nothing can ever abort its channel.");
            Assert.That(accepted.CancellationToken.IsCancellationRequested, Is.True,
                "And that token must be cancelled when the session that accepted it ends.");
        });

        harness.Release();
    }

    /// <summary>
    /// The layer above the session loop, reduced to what the loop actually touches: a connection
    /// that upgrades to a session, a dial-request queue and a sequence of inbound streams. The
    /// <c>Upgrade</c> task never completes on its own, as in production, where it stands for the
    /// protocol stack that owns the stream for the life of the connection -- which is also what
    /// keeps a carried stream counted against the cap.
    /// </summary>
    private sealed class SessionHarness
    {
        private readonly BlockingCollection<UpgradeOptions> _requests = new();
        private readonly System.Threading.Channels.Channel<IWebRtcDataChannel> _inbound =
            System.Threading.Channels.Channel.CreateUnbounded<IWebRtcDataChannel>();
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<UpgradeOptions> _accepted = [];
        private int _upgraded;

        public SessionHarness()
        {
            INewConnectionContext connection = Substitute.For<INewConnectionContext>();
            INewSessionContext session = Substitute.For<INewSessionContext>();
            connection.State.Returns(new State());
            connection.UpgradeToSession().Returns(session);
            connection.Upgrade(Arg.Any<IChannel>(), Arg.Any<UpgradeOptions?>()).Returns(call =>
            {
                Interlocked.Increment(ref _upgraded);
                if (call.Arg<UpgradeOptions?>() is { } options)
                {
                    lock (_accepted)
                    {
                        _accepted.Add(options);
                    }
                }

                return _held.Task;
            });
            session.DialRequests.Returns(_requests.GetConsumingEnumerable());
            Connection = connection;
        }

        public INewConnectionContext Connection { get; }

        public IAsyncEnumerable<IWebRtcDataChannel> InboundStreams => _inbound.Reader.ReadAllAsync();

        public int Upgraded => Volatile.Read(ref _upgraded);

        public IReadOnlyList<UpgradeOptions> AcceptedOptions
        {
            get
            {
                lock (_accepted)
                {
                    return [.. _accepted];
                }
            }
        }

        public Func<CancellationToken, Task<IWebRtcDataChannel>> OpenStream =>
            _ => Task.FromException<IWebRtcDataChannel>(new NotSupportedException("This test opens no outbound streams."));

        public void Offer(IWebRtcDataChannel channel) => _inbound.Writer.TryWrite(channel);

        /// <summary>Ends the inbound sequence, which is what ends the session loop.</summary>
        public void StopOfferingStreams() => _inbound.Writer.TryComplete();

        public void Release()
        {
            _held.TrySetResult();
            _requests.CompleteAdding();
            _requests.Dispose();
        }
    }
}
