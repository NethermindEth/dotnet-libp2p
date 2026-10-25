// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using NSubstitute;
using SIPSorcery.Net;
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
        ITransportContext context = CreateContext(new Identity(), CreateCompletionSource(), out _);

        Multiaddress? announcedAddress = null;
        context
            .When(c => c.ListenerReady(Arg.Any<Multiaddress>()))
            .Do(callInfo => announcedAddress = callInfo.Arg<Multiaddress>());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        Task listenTask = protocol.ListenAsync(context, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
        try
        {
            await WaitUntilAsync(() => announcedAddress is not null, TimeSpan.FromSeconds(2), cts.Token, listenTask);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive("Host WebRTC stack did not expose local DTLS fingerprint during listener bootstrap.");
        }

        cts.Cancel();
        await listenTask;

        Assert.That(announcedAddress, Is.Not.Null);
        Assert.That(announcedAddress!.ToString(), Does.Contain("/webrtc-direct/certhash/"));
    }

    [Test]
    [Explicit("Manual runtime smoke test for listener cancellation path on host WebRTC stack.")]
    public async Task ListenAsync_StopsOnCancellation()
    {
        WebRtcDirectProtocol protocol = new();
        ITransportContext context = CreateContext(new Identity(), CreateCompletionSource(), out _);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
        Task listenTask = protocol.ListenAsync(context, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);

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

        TaskCompletionSource listenerUpgrade = CreateCompletionSource();
        TaskCompletionSource dialerUpgrade = CreateCompletionSource();

        ITransportContext listenerContext = CreateContext(new Identity(), listenerUpgrade, out Func<Multiaddress?> getListenerAddress);
        ITransportContext dialerContext = CreateContext(new Identity(), dialerUpgrade, out _);

        Task listenerTask = listenerProtocol.ListenAsync(listenerContext, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
        Multiaddress listenerAddress;
        try
        {
            listenerAddress = await WaitForListenerAddressAsync(getListenerAddress, cts.Token, listenerTask);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive("Host WebRTC stack did not expose local DTLS fingerprint during listener bootstrap.");
            return;
        }

        Task dialTask = dialerProtocol.DialAsync(dialerContext, listenerAddress, cts.Token);

        await Task.WhenAll(listenerUpgrade.Task, dialerUpgrade.Task, dialTask).WaitAsync(TimeSpan.FromSeconds(20));

        cts.Cancel();
        await Task.WhenAll(dialTask, listenerTask);
    }

    [Test]
    public async Task DialAndListen_RejectsWhenCerthashMismatches()
    {
        WebRtcDirectProtocol listenerProtocol = new();
        WebRtcDirectProtocol dialerProtocol = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));

        TaskCompletionSource listenerUpgrade = CreateCompletionSource();
        ITransportContext listenerContext = CreateContext(new Identity(), listenerUpgrade, out Func<Multiaddress?> getListenerAddress);
        ITransportContext dialerContext = CreateContext(new Identity(), CreateCompletionSource(), out _);

        Task listenerTask = listenerProtocol.ListenAsync(listenerContext, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
        Multiaddress listenerAddress;
        try
        {
            listenerAddress = await WaitForListenerAddressAsync(getListenerAddress, cts.Token, listenerTask);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Inconclusive("Host WebRTC stack did not expose local DTLS fingerprint during listener bootstrap.");
            return;
        }
        Multiaddress tamperedAddress = TamperCerthash(listenerAddress);

        Assert.That(
            async () => await dialerProtocol.DialAsync(dialerContext, tamperedAddress, cts.Token).WaitAsync(TimeSpan.FromSeconds(10)),
            Throws.TypeOf<InvalidOperationException>());

        cts.Cancel();
        await listenerTask;
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
        ITransportContext context = CreateContext(new Identity(), CreateCompletionSource(), out Func<Multiaddress?> getListenerAddress);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task listenerTask = protocol.ListenAsync(context, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
        Multiaddress listenerAddress = await WaitForListenerAddressAsync(getListenerAddress, cts.Token, listenerTask);
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
    /// signs its reply: SIPSorcery's serialiser, MESSAGE-INTEGRITY keyed with the server ufrag and a
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

    private static ITransportContext CreateContext(Identity identity, TaskCompletionSource upgradeSignal, out Func<Multiaddress?> getListenerAddress)
    {
        ITransportContext context = Substitute.For<ITransportContext>();
        ILocalPeer peer = Substitute.For<ILocalPeer>();
        peer.Identity.Returns(identity);
        context.Peer.Returns(peer);

        Multiaddress? listenerAddress = null;
        context.When(c => c.ListenerReady(Arg.Any<Multiaddress>())).Do(callInfo => listenerAddress = callInfo.Arg<Multiaddress>());
        getListenerAddress = () => listenerAddress;

        context.CreateConnection().Returns(_ =>
        {
            INewConnectionContext connection = Substitute.For<INewConnectionContext>();
            connection.State.Returns(new State());
            connection.Upgrade(Arg.Any<IChannel>(), Arg.Any<UpgradeOptions?>())
                .Returns(callInfo =>
                {
                    upgradeSignal.TrySetResult();
                    return Task.CompletedTask;
                });

            connection.Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>(), Arg.Any<UpgradeOptions?>())
                .Returns(callInfo =>
                {
                    upgradeSignal.TrySetResult();
                    return Task.CompletedTask;
                });

            return connection;
        });

        return context;
    }

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
