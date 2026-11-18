// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery.Dto;
using NUnit.Framework;

namespace Libp2p.Protocols.PubsubPeerDiscovery.E2eTests;

/// <summary>
/// An announcement is self-described: the sender writes both the key and the addresses. Discovery
/// may only accept one whose parts agree, since the alternative is letting any peer publish
/// addresses under another peer's id (#242).
/// </summary>
[TestFixture]
public class AnnouncementIdentityTests
{
    private static readonly Identity Announcer = TestPeers.Identity(1);
    private static readonly Identity Other = TestPeers.Identity(2);

    private static Peer Announcement(Identity? key, params string[] addrs)
    {
        Peer peer = new();
        if (key is not null)
        {
            peer.PublicKey = key.PublicKey.ToByteString();
        }
        peer.Addrs.AddRange(addrs.Select(a => ByteString.CopyFrom(Multiaddress.Decode(a).ToBytes())));
        return peer;
    }

    [Test]
    public void TheIdentityComesFromTheKeyRatherThanAnAddressSuffix()
    {
        Peer announcement = Announcement(Announcer,
            $"/ip4/192.0.2.10/tcp/4001/p2p/{Announcer.PeerId}",
            $"/ip4/192.0.2.10/udp/4001/quic-v1/p2p/{Announcer.PeerId}");

        PeerId? announced = PubsubPeerDiscoveryProtocol.AnnouncedPeer(announcement, out Multiaddress[] addresses);

        Assert.Multiple(() =>
        {
            Assert.That(announced, Is.EqualTo(Announcer.PeerId));
            Assert.That(addresses.Select(a => a.ToString()), Is.EqualTo(new[]
            {
                $"/ip4/192.0.2.10/tcp/4001/p2p/{Announcer.PeerId}",
                $"/ip4/192.0.2.10/udp/4001/quic-v1/p2p/{Announcer.PeerId}",
            }));
        });
    }

    /// <summary>The spoof this exists to stop: a real key, addresses claiming somebody else.</summary>
    [Test]
    public void AnAnnouncementClaimingAnotherPeerIsRejectedWhole()
    {
        Peer announcement = Announcement(Announcer,
            $"/ip4/192.0.2.10/tcp/4001/p2p/{Announcer.PeerId}",
            $"/ip4/198.51.100.7/tcp/4001/p2p/{Other.PeerId}");

        PeerId? announced = PubsubPeerDiscoveryProtocol.AnnouncedPeer(announcement, out Multiaddress[] addresses);

        Assert.Multiple(() =>
        {
            Assert.That(announced, Is.Null, "one announcement cannot carry two identities");
            Assert.That(addresses, Is.Empty, "and none of it may be kept");
        });
    }

    [Test]
    public void AnAnnouncementWithoutAUsableKeyProvesNothing()
    {
        Peer missing = Announcement(null, $"/ip4/192.0.2.10/tcp/4001/p2p/{Announcer.PeerId}");
        Peer garbage = new() { PublicKey = ByteString.CopyFrom(0xff, 0xff, 0xff, 0xff) };
        garbage.Addrs.Add(ByteString.CopyFrom(
            Multiaddress.Decode($"/ip4/192.0.2.10/tcp/4001/p2p/{Announcer.PeerId}").ToBytes()));

        Assert.Multiple(() =>
        {
            Assert.That(PubsubPeerDiscoveryProtocol.AnnouncedPeer(missing, out Multiaddress[] none), Is.Null,
                "an address suffix is a claim, not a proof");
            Assert.That(none, Is.Empty);
            Assert.That(PubsubPeerDiscoveryProtocol.AnnouncedPeer(garbage, out Multiaddress[] bad), Is.Null);
            Assert.That(bad, Is.Empty);
        });
    }

    /// <summary>
    /// A peer advertises every transport it listens on and this build does not know them all, so
    /// one address we cannot make sense of must not cost us the rest. That is tolerance, not trust:
    /// it differs from an address naming another peer, which is evidence and rejects the whole
    /// announcement. Note the decoder does not throw on these bytes — it yields an address with no
    /// protocols in it, which is just as undialable and has to be dropped the same way.
    /// </summary>
    [Test]
    public void AnUnusableAddressIsSkippedWithoutLosingTheOthers()
    {
        Peer announcement = Announcement(Announcer, $"/ip4/192.0.2.10/tcp/4001/p2p/{Announcer.PeerId}");
        announcement.Addrs.Add(ByteString.CopyFrom(0x99, 0x99, 0x01, 0x02));
        announcement.Addrs.Add(ByteString.CopyFrom(
            Multiaddress.Decode($"/ip4/192.0.2.11/tcp/4002/p2p/{Announcer.PeerId}").ToBytes()));

        PeerId? announced = PubsubPeerDiscoveryProtocol.AnnouncedPeer(announcement, out Multiaddress[] addresses);

        Assert.Multiple(() =>
        {
            Assert.That(announced, Is.EqualTo(Announcer.PeerId));
            Assert.That(addresses.Select(a => a.ToString()), Is.EqualTo(new[]
            {
                $"/ip4/192.0.2.10/tcp/4001/p2p/{Announcer.PeerId}",
                $"/ip4/192.0.2.11/tcp/4002/p2p/{Announcer.PeerId}",
            }));
        });
    }

    /// <summary>A bare address carries no claim to disagree with, so it is kept.</summary>
    [Test]
    public void AnAddressWithoutAPeerSuffixIsKept()
    {
        Peer announcement = Announcement(Announcer, "/ip4/192.0.2.10/tcp/4001");

        PeerId? announced = PubsubPeerDiscoveryProtocol.AnnouncedPeer(announcement, out Multiaddress[] addresses);

        Assert.Multiple(() =>
        {
            Assert.That(announced, Is.EqualTo(Announcer.PeerId));
            Assert.That(addresses.Select(a => a.ToString()), Is.EqualTo(new[] { "/ip4/192.0.2.10/tcp/4001" }));
        });
    }
}
