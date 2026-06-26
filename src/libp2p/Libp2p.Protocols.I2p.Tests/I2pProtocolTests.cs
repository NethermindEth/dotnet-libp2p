// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Reflection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.I2p;

namespace Nethermind.Libp2p.Protocols.I2p.Tests;

public class I2pProtocolTests
{
    [Test]
    public void IsAddressMatch_MatchesGarlicAddresses()
    {
        I2pMultiaddr.Register();
        Multiaddress address = Multiaddress.Decode($"/garlic64/{I2pTestDestinations.Garlic64()}");

        Assert.That(I2pProtocol.IsAddressMatch(address), Is.True);
    }

    [Test]
    public void GetDefaultAddresses_ReturnsSelectableI2pListenAddress()
    {
        PeerId peerId = new Identity().PeerId;

        Multiaddress[] addresses = I2pProtocol.GetDefaultAddresses(peerId);

        Assert.That(addresses, Has.Length.EqualTo(1));
        Assert.That(I2pProtocol.IsAddressMatch(addresses[0]), Is.True);
        Assert.That(addresses[0].GetPeerId(), Is.EqualTo(peerId));
    }

    [Test]
    public void Options_DefaultToModernStreamSessionSettings()
    {
        I2pOptions options = new();

        Assert.That(options.PrimarySessionStyle, Is.EqualTo("MASTER"));
        Assert.That(options.StreamSessionId, Is.Not.EqualTo(options.SessionId));
        Assert.That(options.DatagramSessionId, Is.Not.EqualTo(options.SessionId));
        Assert.That(options.UsePrimarySessionForStreams, Is.True);
        Assert.That(options.DatagramHost, Is.Null);
        Assert.That(options.DestinationSignatureType, Is.EqualTo("7"));
        Assert.That(options.SessionOptions["i2cp.leaseSetEncType"], Is.EqualTo("4,0"));
    }

    [Test]
    public void TransportOptions_DefaultToPlainStreamsUnlessExplicitlyConfigured()
    {
        I2pOptions callerOptions = new();
        callerOptions.SessionOptions.Clear();
        I2pOptions defaultOptions = InvokeCreateTransportOptions(callerOptions);
        I2pOptions explicitSharedOptions = new() { UsePrimarySessionForStreams = true };
        I2pOptions sharedTransportOptions = InvokeCreateTransportOptions(explicitSharedOptions);

        Assert.That(defaultOptions.UsePrimarySessionForStreams, Is.False);
        Assert.That(defaultOptions.SessionOptions, Is.Empty);
        Assert.That(sharedTransportOptions.UsePrimarySessionForStreams, Is.True);
    }

    [Test]
    public async Task GetSamClient_RejectsAfterDispose()
    {
        I2pProtocol protocol = new();
        await protocol.DisposeAsync();
        MethodInfo method = typeof(I2pProtocol).GetMethod("GetSamClient", BindingFlags.NonPublic | BindingFlags.Instance)!;

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(protocol, [new Identity().PeerId]))!;

        Assert.That(exception.InnerException, Is.TypeOf<ObjectDisposedException>());
    }

    private static I2pOptions InvokeCreateTransportOptions(I2pOptions options)
    {
        MethodInfo method = typeof(I2pProtocol).GetMethod("CreateTransportOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (I2pOptions)method.Invoke(null, [options])!;
    }
}
