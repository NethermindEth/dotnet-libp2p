// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.WebRtc;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using System.Buffers;
using System.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

public class WebRtcRelayMultiaddrTests
{
    [Test]
    public void RelayedWebRtcAddress_MatchesWebRtcTransport()
    {
        Multiaddress addr = "/ip4/127.0.0.1/tcp/4001/ws/p2p/12D3KooWGCs2ta5wWxwQ66xC5C34gXWPtd84rgja7guQ7wjqZJJF/p2p-circuit/webrtc/p2p/12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA";

        Assert.That(WebRtcProtocol.IsAddressMatch(addr), Is.True);
        Assert.That(WebRtcMultiaddr.ToCircuitAddress(addr).ToString(), Is.EqualTo("/ip4/127.0.0.1/tcp/4001/ws/p2p/12D3KooWGCs2ta5wWxwQ66xC5C34gXWPtd84rgja7guQ7wjqZJJF/p2p-circuit/p2p/12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA"));
        Assert.That(addr.GetPeerId(), Is.EqualTo(new PeerId("12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA")));
    }

    [Test]
    public void RelayedWebRtcAddress_WithWebRtcDirectRelayLeg_PreservesRelayTransportWhenConvertedToCircuit()
    {
        string relayPeerId = "12D3KooWGCs2ta5wWxwQ66xC5C34gXWPtd84rgja7guQ7wjqZJJF";
        string targetPeerId = "12D3KooWD3eckifWpRn9wQpMG9R9hX3sD158z7EqHWmweQAJU5SA";
        DtlsFingerprint fingerprint = new("sha-256", Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        Multiaddress relayLeg = WebRtcDirectMultiaddr.Build(new IPEndPoint(IPAddress.Loopback, 40150), fingerprint);
        Multiaddress addr = Multiaddress.Decode($"{relayLeg}/p2p/{relayPeerId}/p2p-circuit/webrtc/p2p/{targetPeerId}");

        Assert.That(WebRtcProtocol.IsAddressMatch(addr), Is.True);
        Assert.That(WebRtcMultiaddr.ToCircuitAddress(addr).ToString(), Does.Contain("/webrtc-direct/"));
        Assert.That(WebRtcMultiaddr.ToCircuitAddress(addr).ToString(), Does.Contain($"/p2p-circuit/p2p/{targetPeerId}"));
        Assert.That(WebRtcMultiaddr.ToCircuitAddress(addr).ToString(), Does.Not.Contain("/p2p-circuit/webrtc/"));
    }

    [Test]
    public void WebRtcSignalingCodec_EncodesPrivateToPrivateMessage()
    {
        byte[] encoded = WebRtcSignalingCodec.Encode(WebRtcSignalingMessageType.IceCandidate, "candidate");

        Assert.That(encoded, Is.EqualTo(new byte[]
        {
            0x08, 0x02,
            0x12, 0x09,
            (byte)'c', (byte)'a', (byte)'n', (byte)'d', (byte)'i', (byte)'d', (byte)'a', (byte)'t', (byte)'e'
        }));
    }

    [Test]
    public void WebRtcSignalingCodec_RejectsOversizedOutboundMessage()
    {
        string oversized = new('a', WebRtcSignalingCodec.MaxMessageSize);

        Assert.Throws<FormatException>(() => WebRtcSignalingCodec.Encode(WebRtcSignalingMessageType.SdpOffer, oversized));
    }

    [Test]
    public async Task WebRtcSignalingCodec_RejectsOversizedInboundMessageBeforeAllocatingPayload()
    {
        Channel channel = new();
        Task writeTask = Task.Run(async () => await channel.Reverse.WriteVarintAsync(WebRtcSignalingCodec.MaxMessageSize + 1));

        Assert.ThrowsAsync<FormatException>(async () => await WebRtcSignalingCodec.ReadAsync(channel));
        await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task WebRtcSignalingCodec_RejectsMalformedDataFieldLength()
    {
        Channel channel = new();
        byte[] malformed = [0x12, 0x05, 0x61];
        Task writeTask = Task.Run(async () => await channel.Reverse.WriteSizeAndDataAsync(malformed));

        Assert.ThrowsAsync<FormatException>(async () => await WebRtcSignalingCodec.ReadAsync(channel));
        await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task WebRtcSignalingCodec_RejectsMalformedUnknownLengthDelimitedField()
    {
        Channel channel = new();
        byte[] malformed = [0x7a, 0x05, 0x61];
        Task writeTask = Task.Run(async () => await channel.Reverse.WriteSizeAndDataAsync(malformed));

        Assert.ThrowsAsync<FormatException>(async () => await WebRtcSignalingCodec.ReadAsync(channel));
        await writeTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task WebRtcStreamChannel_ChunksPayloadsToWebRtcFrameLimit()
    {
        Channel raw = new();
        BrowserWebRtcStreamChannel writer = new(raw);
        BrowserWebRtcStreamChannel reader = new(raw.Reverse);
        byte[] payload = Enumerable.Range(0, BrowserWebRtcStreamChannel.MaxEncodedMessageSize * 2)
            .Select(i => (byte)i)
            .ToArray();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        Task<ReadResult> readTask = reader.ReadAsync(payload.Length, token: timeout.Token).AsTask();
        IOResult writeResult = await writer.WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token);
        ReadResult readResult = await readTask;

        Assert.That(writeResult, Is.EqualTo(IOResult.Ok));
        Assert.That(readResult.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(readResult.Data.ToArray(), Is.EqualTo(payload));

        await writer.CloseAsync();
        await reader.CloseAsync();
    }

    [Test]
    public void WebRtcProtocol_RejectsRemotePublicKeyThatDoesNotMatchAddressPeerId()
    {
        Identity actual = new();
        Identity expected = new();
        Multiaddress remoteAddr = Multiaddress.Decode($"/webrtc/p2p/{expected.PeerId}");

        Assert.That(
            () => WebRtcProtocol.ValidateRemotePublicKey(remoteAddr, actual.PublicKey),
            Throws.TypeOf<Libp2pException>());
    }
}
