// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nethermind.Libp2p.Protocols.I2p.Tests;

public class I2pDatagramSessionTests
{
    [Test]
    public void ParseForwarded_ExtractsSourceOptionsAndPayload()
    {
        byte[] packet = Encoding.ASCII.GetBytes("sourceDestination FROM_PORT=1234 TO_PORT=5678\nhello");

        I2pDatagram datagram = I2pDatagram.ParseForwarded(packet);

        Assert.That(datagram.SourceDestination, Is.EqualTo("sourceDestination"));
        Assert.That(datagram.Options["FROM_PORT"], Is.EqualTo("1234"));
        Assert.That(datagram.Options["TO_PORT"], Is.EqualTo("5678"));
        Assert.That(datagram.Payload, Is.EqualTo(Encoding.ASCII.GetBytes("hello")));
    }

    [Test]
    public async Task SendAsync_SendsSamUdpPacket()
    {
        using UdpClient samUdp = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient sessionUdp = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint samEndpoint = (IPEndPoint)samUdp.Client.LocalEndPoint!;
        await using I2pDatagramSession session = new(
            "udp-session",
            I2pTestDestinations.Garlic64(),
            sessionUdp,
            samEndpoint,
            I2pOptions.DefaultMaxDatagramPayloadSize);
        string destination = I2pTestDestinations.Garlic64();

        await session.SendAsync(destination, Encoding.ASCII.GetBytes("payload"), CancellationToken.None);

        UdpReceiveResult result = await samUdp.ReceiveAsync(CancellationToken.None);

        Assert.That(Encoding.ASCII.GetString(result.Buffer), Is.EqualTo($"3.0 udp-session {destination}\npayload"));
    }

    [Test]
    public async Task ReceiveAsync_ParsesForwardedDatagram()
    {
        using UdpClient sessionUdp = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint senderEndpoint = (IPEndPoint)sender.Client.LocalEndPoint!;
        await using I2pDatagramSession session = new(
            "udp-session",
            I2pTestDestinations.Garlic64(),
            sessionUdp,
            senderEndpoint,
            I2pOptions.DefaultMaxDatagramPayloadSize);

        byte[] packet = Encoding.ASCII.GetBytes("sourceDestination\npayload");
        await sender.Client.SendToAsync(packet, SocketFlags.None, session.LocalEndpoint, CancellationToken.None);

        I2pDatagram datagram = await session.ReceiveAsync(CancellationToken.None);

        Assert.That(datagram.SourceDestination, Is.EqualTo("sourceDestination"));
        Assert.That(datagram.Payload, Is.EqualTo(Encoding.ASCII.GetBytes("payload")));
    }

    [Test]
    public async Task ReceiveAsync_RejectsNonSamEndpoint()
    {
        using UdpClient sessionUdp = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0));
        await using I2pDatagramSession session = new(
            "udp-session",
            I2pTestDestinations.Garlic64(),
            sessionUdp,
            new IPEndPoint(IPAddress.Loopback, 7655),
            I2pOptions.DefaultMaxDatagramPayloadSize);

        byte[] packet = Encoding.ASCII.GetBytes("sourceDestination\npayload");
        await sender.Client.SendToAsync(packet, SocketFlags.None, session.LocalEndpoint, CancellationToken.None);

        I2pException exception = Assert.ThrowsAsync<I2pException>(async () => await session.ReceiveAsync(CancellationToken.None))!;

        Assert.That(exception.Message, Does.Contain("unexpected endpoint"));
    }

    [Test]
    public void BuildOutboundPacket_RejectsB32Destination()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            I2pDatagramSession.BuildOutboundPacket("udp-session", $"{I2pTestDestinations.Garlic32()}.b32.i2p", [1]))!;

        Assert.That(exception.Message, Does.Contain("base64"));
    }

    [Test]
    public void BuildOutboundPacket_RejectsStandardBase64Destination()
    {
        string standardBase64Destination = Convert.ToBase64String(Enumerable.Repeat((byte)0xff, 387).ToArray());

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            I2pDatagramSession.BuildOutboundPacket("udp-session", standardBase64Destination, [1]))!;

        Assert.That(exception.Message, Does.Contain("I2P base64 alphabet"));
    }

    [Test]
    public void BuildOutboundPacket_RejectsHeaderInjection()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            I2pDatagramSession.BuildOutboundPacket("udp-session", $"{I2pTestDestinations.Garlic64()}\nX=Y", [1]))!;

        Assert.That(exception.Message, Does.Contain("whitespace"));
    }
}
