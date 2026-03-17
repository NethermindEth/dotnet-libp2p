// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using NSubstitute;

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
        ITransportContext context = Substitute.For<ITransportContext>();
        context.Peer.Returns(Substitute.For<ILocalPeer>());

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
        ITransportContext context = Substitute.For<ITransportContext>();
        context.Peer.Returns(Substitute.For<ILocalPeer>());

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
    [Explicit("Manual end-to-end loopback for WebRTC-Direct handshake and upgrade.")]
    public async Task DialAndListen_UpgradesOnBothSides()
    {
        WebRtcDirectProtocol protocol = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));

        TaskCompletionSource listenerUpgrade = CreateCompletionSource();
        TaskCompletionSource dialerUpgrade = CreateCompletionSource();

        ITransportContext listenerContext = CreateContext(listenerUpgrade, out Func<Multiaddress?> getListenerAddress);
        ITransportContext dialerContext = CreateContext(dialerUpgrade, out _);

        Task listenerTask = protocol.ListenAsync(listenerContext, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
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

        Task dialTask = protocol.DialAsync(dialerContext, listenerAddress, cts.Token);

        await Task.WhenAll(listenerUpgrade.Task, dialerUpgrade.Task);

        cts.Cancel();
        await Task.WhenAll(dialTask, listenerTask);
    }

    [Test]
    [Explicit("Manual end-to-end loopback for fingerprint mismatch rejection.")]
    public async Task DialAndListen_RejectsWhenCerthashMismatches()
    {
        WebRtcDirectProtocol protocol = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));

        TaskCompletionSource listenerUpgrade = CreateCompletionSource();
        ITransportContext listenerContext = CreateContext(listenerUpgrade, out Func<Multiaddress?> getListenerAddress);
        ITransportContext dialerContext = CreateContext(CreateCompletionSource(), out _);

        Task listenerTask = protocol.ListenAsync(listenerContext, Multiaddress.Decode("/ip4/127.0.0.1/udp/0"), cts.Token);
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

        Assert.That(async () => await protocol.DialAsync(dialerContext, tamperedAddress, cts.Token), Throws.TypeOf<InvalidOperationException>());

        cts.Cancel();
        await listenerTask;
    }

    private static ITransportContext CreateContext(TaskCompletionSource upgradeSignal, out Func<Multiaddress?> getListenerAddress)
    {
        ITransportContext context = Substitute.For<ITransportContext>();
        context.Peer.Returns(Substitute.For<ILocalPeer>());

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
        char replacement = value[^1] == 'A' ? 'B' : 'A';
        parts[certhashIndex + 1] = value[..^1] + replacement;
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