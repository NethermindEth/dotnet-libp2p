// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.AutoNat.Dto;
using NSubstitute;
using NUnit.Framework;
using ResponseStatus = Nethermind.Libp2p.Protocols.AutoNat.Dto.DialResponse.Types.ResponseStatus;

namespace Nethermind.Libp2p.Protocols.AutoNat.Tests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class AutoNatV2ServerProtocolTests
{
    private const ulong Nonce = 0x1234_5678_9abc_def0;
    private static readonly PeerId Client = TestPeers.PeerId(1);
    private static readonly Multiaddress Observed = $"/ip4/203.0.113.7/tcp/4001/p2p/{Client}";

    [Test]
    public async Task DialsBackFirstPublicAddress_SkippingPrivateAndMalformed()
    {
        IAutoNatDialBack dialBack = DialBack(DialStatus.Ok);
        Multiaddress publicAddr = "/ip4/203.0.113.7/tcp/4001";
        byte[] garbage = [0xff, 0xff, 0xff];

        DialResponse response = await RequestAsync(new AutoNatV2ServerProtocol(dialBack), Observed,
            [Addr("/ip4/192.168.1.5/tcp/4001"), Addr("/ip4/0.0.0.0/tcp/4001"), ByteString.CopyFrom(garbage), Addr(publicAddr)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(ResponseStatus.Ok));
            Assert.That(response.AddrIdx, Is.EqualTo(3));
            Assert.That(response.DialStatus, Is.EqualTo(DialStatus.Ok));
        }
        await dialBack.Received(1).DialBackAsync(Client, Arg.Is<Multiaddress>(a => a.ToString() == publicAddr.ToString()), Nonce, Arg.Any<CancellationToken>());
    }

    [TestCase("/ip4/203.0.113.7/ip4/203.0.113.7/tcp/4001")]
    [TestCase("/ip6/2001:db8::1/ip6/2001:db8::1/tcp/4001")]
    public async Task SkipsAddressWithRepeatedIp(string repeated)
    {
        IAutoNatDialBack dialBack = DialBack(DialStatus.Ok);
        Multiaddress publicAddr = "/ip4/203.0.113.7/tcp/4001";

        DialResponse response = await RequestAsync(new AutoNatV2ServerProtocol(dialBack), Observed, [Addr(repeated), Addr(publicAddr)]);

        Assert.That(response.AddrIdx, Is.EqualTo(1));
        await dialBack.Received(1).DialBackAsync(Client, Arg.Is<Multiaddress>(a => a.ToString() == publicAddr.ToString()), Nonce, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportsFailedDialBack()
    {
        DialResponse response = await RequestAsync(new AutoNatV2ServerProtocol(DialBack(DialStatus.EDialError)), Observed, [Addr("/ip4/203.0.113.7/tcp/4001")]);

        Assert.That(response.DialStatus, Is.EqualTo(DialStatus.EDialError));
    }

    [TestCase("/ip4/10.0.0.1/tcp/4001")]
    [TestCase("/ip4/127.0.0.1/tcp/4001")]
    [TestCase("/ip4/100.64.0.1/tcp/4001")]
    [TestCase("/ip6/fe80::1/tcp/4001")]
    [TestCase("/dns4/example.com/tcp/4001")]
    [TestCase("/ip4/203.0.113.7/tcp/4001/p2p/OTHER")]
    public async Task RefusesAddressesItWillNotDial(string addr)
    {
        IAutoNatDialBack dialBack = DialBack(DialStatus.Ok);

        DialResponse response = await RequestAsync(new AutoNatV2ServerProtocol(dialBack), Observed, [Addr(addr.Replace("OTHER", TestPeers.PeerId(2).ToString()))]);

        Assert.That(response.Status, Is.EqualTo(ResponseStatus.EDialRefused));
        await dialBack.DidNotReceiveWithAnyArgs().DialBackAsync(default!, default!, default, default);
    }

    [Test]
    public async Task RefusesAddressWithoutTransport()
    {
        IAutoNatDialBack dialBack = DialBack(DialStatus.Ok);
        dialBack.CanDial(Arg.Any<Multiaddress>()).Returns(false);

        DialResponse response = await RequestAsync(new AutoNatV2ServerProtocol(dialBack), Observed, [Addr("/ip4/203.0.113.7/tcp/4001")]);

        Assert.That(response.Status, Is.EqualTo(ResponseStatus.EDialRefused));
    }

    [Test]
    public async Task DialsPrivateAddress_WhenAllowed()
    {
        AutoNatV2ServerSettings settings = new() { AllowPrivateAddresses = true };

        DialResponse response = await RequestAsync(new AutoNatV2ServerProtocol(DialBack(DialStatus.Ok), settings),
            $"/ip4/192.168.1.5/tcp/4001/p2p/{Client}", [Addr("/ip4/192.168.1.5/tcp/4001")]);

        Assert.That(response.Status, Is.EqualTo(ResponseStatus.Ok));
    }

    [Test]
    public async Task OtherIp_RequiresDialDataBeforeDialing()
    {
        IAutoNatDialBack dialBack = DialBack(DialStatus.Ok);
        AutoNatV2ServerProtocol server = new(dialBack, new AutoNatV2ServerSettings { MaxDialDataDelay = TimeSpan.Zero });
        (IChannel client, Task listen) = Start(server, Observed);

        await client.WriteSizeAndProtobufAsync(Request(Addr("/ip4/192.168.1.5/tcp/1"), Addr("/ip4/198.51.100.9/tcp/4001")));
        Message dataRequest = await client.ReadPrefixedProtobufAsync(Message.Parser);
        Assert.That(dataRequest.MsgCase, Is.EqualTo(Message.MsgOneofCase.DialDataRequest));
        Assert.That(dataRequest.DialDataRequest.AddrIdx, Is.EqualTo(1));
        Assert.That(dataRequest.DialDataRequest.NumBytes, Is.InRange(30_000UL, 100_000UL));

        Message chunk = new() { DialDataResponse = new DialDataResponse { Data = ByteString.CopyFrom(new byte[4096]) } };
        for (ulong sent = 0; sent < dataRequest.DialDataRequest.NumBytes; sent += 4096)
        {
            await dialBack.DidNotReceiveWithAnyArgs().DialBackAsync(default!, default!, default, default);
            await client.WriteSizeAndProtobufAsync(chunk);
        }

        Message response = await client.ReadPrefixedProtobufAsync(Message.Parser);
        await listen;
        Assert.That(response.DialResponse.Status, Is.EqualTo(ResponseStatus.Ok));
        Assert.That(response.DialResponse.AddrIdx, Is.EqualTo(1));
        await dialBack.Received(1).DialBackAsync(Client, Arg.Any<Multiaddress>(), Nonce, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OtherIp_TinyDialDataChunk_AbortsWithoutDialing()
    {
        IAutoNatDialBack dialBack = DialBack(DialStatus.Ok);
        (IChannel client, Task listen) = Start(new AutoNatV2ServerProtocol(dialBack), Observed);

        await client.WriteSizeAndProtobufAsync(Request(Addr("/ip4/198.51.100.9/tcp/4001")));
        await client.ReadPrefixedProtobufAsync(Message.Parser);
        await client.WriteSizeAndProtobufAsync(new Message { DialDataResponse = new DialDataResponse { Data = ByteString.CopyFrom(new byte[10]) } });

        Assert.That(async () => await listen, Throws.TypeOf<InvalidDataException>());
        await dialBack.DidNotReceiveWithAnyArgs().DialBackAsync(default!, default!, default, default);
    }

    [Test]
    public async Task OtherIp_RejectsOverDialDataLimit()
    {
        AutoNatV2ServerProtocol server = new(DialBack(DialStatus.Ok), new AutoNatV2ServerSettings { DialDataRequestsPerMinute = 0 });

        DialResponse response = await RequestAsync(server, Observed, [Addr("/ip4/198.51.100.9/tcp/4001")]);

        Assert.That(response.Status, Is.EqualTo(ResponseStatus.ERequestRejected));
    }

    [Test]
    public async Task RejectsOverPerPeerLimit_UntilWindowPasses()
    {
        ManualTimeProvider clock = new();
        AutoNatV2ServerProtocol server = new(DialBack(DialStatus.Ok), new AutoNatV2ServerSettings { RequestsPerPeerPerMinute = 1 }, timeProvider: clock);
        ByteString addr = Addr("/ip4/203.0.113.7/tcp/4001");

        Assert.That((await RequestAsync(server, Observed, [addr])).Status, Is.EqualTo(ResponseStatus.Ok));
        Assert.That((await RequestAsync(server, Observed, [addr])).Status, Is.EqualTo(ResponseStatus.ERequestRejected));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.That((await RequestAsync(server, Observed, [addr])).Status, Is.EqualTo(ResponseStatus.Ok));
    }

    [Test]
    public async Task RejectsConcurrentRequestOverLimit()
    {
        TaskCompletionSource dialing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<DialStatus> dial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IAutoNatDialBack dialBack = DialBack(DialStatus.Ok);
        dialBack.DialBackAsync(default!, default!, default, default).ReturnsForAnyArgs(_ => dialing.TrySetResult() ? dial.Task : Task.FromResult(DialStatus.Ok));
        AutoNatV2ServerProtocol server = new(dialBack, new AutoNatV2ServerSettings { MaxConcurrentRequestsPerPeer = 1 });
        ByteString addr = Addr("/ip4/203.0.113.7/tcp/4001");

        (IChannel first, Task firstListen) = Start(server, Observed);
        await first.WriteSizeAndProtobufAsync(Request(addr));
        await dialing.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That((await RequestAsync(server, Observed, [addr])).Status, Is.EqualTo(ResponseStatus.ERequestRejected));
        dial.SetResult(DialStatus.Ok);
        Assert.That((await first.ReadPrefixedProtobufAsync(Message.Parser)).DialResponse.Status, Is.EqualTo(ResponseStatus.Ok));
        await firstListen;
        Assert.That((await RequestAsync(server, Observed, [addr])).Status, Is.EqualTo(ResponseStatus.Ok));
    }

    [Test]
    public async Task TimesOutSilentClient_AndReleasesItsSlot()
    {
        ManualTimeProvider clock = new();
        AutoNatV2ServerProtocol server = new(DialBack(DialStatus.Ok), new AutoNatV2ServerSettings { MaxConcurrentRequestsPerPeer = 1 }, timeProvider: clock);

        (_, Task listen) = Start(server, Observed);
        clock.Advance(TimeSpan.FromSeconds(15));

        Assert.That(async () => await listen, Throws.Exception);
        Assert.That((await RequestAsync(server, Observed, [Addr("/ip4/203.0.113.7/tcp/4001")])).Status, Is.EqualTo(ResponseStatus.Ok));
    }

    [Test]
    public async Task StreamCancellation_EndsExchange()
    {
        using CancellationTokenSource stream = new();
        // The manual clock never reaches the 15 s deadline, so only the stream token can end the exchange.
        (_, Task listen) = Start(new AutoNatV2ServerProtocol(DialBack(DialStatus.Ok), timeProvider: new ManualTimeProvider()), Observed, stream.Token);

        await stream.CancelAsync();

        Assert.That(async () => await listen.WaitAsync(TimeSpan.FromSeconds(10)), Throws.InstanceOf<ChannelClosedException>());
    }

    [Test]
    public async Task DialBackException_RespondsInternalError()
    {
        IAutoNatDialBack dialBack = DialBack(DialStatus.Ok);
        dialBack.DialBackAsync(default!, default!, default, default).ReturnsForAnyArgs(Task.FromException<DialStatus>(new InvalidOperationException()));

        DialResponse response = await RequestAsync(new AutoNatV2ServerProtocol(dialBack), Observed, [Addr("/ip4/203.0.113.7/tcp/4001")]);

        Assert.That(response.Status, Is.EqualTo(ResponseStatus.EInternalError));
    }

    private static IAutoNatDialBack DialBack(DialStatus status)
    {
        IAutoNatDialBack dialBack = Substitute.For<IAutoNatDialBack>();
        dialBack.CanDial(Arg.Any<Multiaddress>()).Returns(true);
        dialBack.DialBackAsync(default!, default!, default, default).ReturnsForAnyArgs(status);
        return dialBack;
    }

    private static ByteString Addr(Multiaddress addr) => ByteString.CopyFrom(addr.ToBytes());

    private static Message Request(params ByteString[] addrs) => new() { DialRequest = new DialRequest { Addrs = { addrs }, Nonce = Nonce } };

    private static (IChannel Client, Task Listen) Start(AutoNatV2ServerProtocol server, Multiaddress observed, CancellationToken streamToken = default)
    {
        TestChannel channel = new();
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = observed });
        context.UpgradeOptions.Returns(new UpgradeOptions { CancellationToken = streamToken });
        return (channel.Reverse(), ListenAndCloseAsync(server, channel, context));
    }

    // End the stream when the listener returns, as the peer does, so a client still reading or writing fails instead of hanging.
    private static async Task ListenAndCloseAsync(AutoNatV2ServerProtocol server, TestChannel channel, ISessionContext context)
    {
        try
        {
            await server.ListenAsync(channel, context);
        }
        catch
        {
            await channel.AbortAsync();
            throw;
        }
        await channel.CloseReadAsync();
    }

    private static async Task<DialResponse> RequestAsync(AutoNatV2ServerProtocol server, Multiaddress observed, ByteString[] addrs)
    {
        (IChannel client, Task listen) = Start(server, observed);
        // Not awaited: a rejecting server answers without reading the request, and writes wait for the reader.
        _ = client.WriteSizeAndProtobufAsync(Request(addrs)).AsTask();
        Message response = await client.ReadPrefixedProtobufAsync(Message.Parser);
        await listen;
        Assert.That(response.MsgCase, Is.EqualTo(Message.MsgOneofCase.DialResponse));
        return response.DialResponse;
    }
}
