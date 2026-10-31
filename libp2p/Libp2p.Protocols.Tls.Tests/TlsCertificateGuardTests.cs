// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Quic;
using Nethermind.Libp2p.Protocols.Tls;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using BcCertificate = Org.BouncyCastle.Tls.Certificate;
using BcProtocol = Org.BouncyCastle.Tls.TlsProtocol;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

public class TlsCertificateGuardTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void AuthenticationCallbackChecksCountBeforeLeafValidation(int count)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 certificate = CertificateHelper.CertificateFromIdentity(key, TestPeers.Identity(1));
        int validated = 0;
        using TlsHandshake handshake = new(certificate, false, _ => { validated++; return true; });
        handshake.Start();
        BcCertificate received = Certificates(certificate, count);
        // Isolate the callback from the wire and parser guards, which normally
        // reject duplicates first. Reflection here is test-only.
        MethodInfo callback = typeof(TlsHandshake).GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (count == 1)
        {
            callback.Invoke(handshake, [received]);
            Assert.That(validated, Is.EqualTo(1));
            Assert.That(handshake.RemoteCertificate!.RawData, Is.EqualTo(certificate.RawData));
        }
        else
        {
            TargetInvocationException? failure = Assert.Throws<TargetInvocationException>(() => callback.Invoke(handshake, [received]));
            Assert.That(failure!.InnerException, Is.InstanceOf<TlsFatalAlert>());
            Assert.That(validated, Is.Zero);
            Assert.That(handshake.RemoteCertificate, Is.Null);
        }
    }

    [TestCase(1)]
    [TestCase(2)]
    public void EncodingGuardRequiresExactSingleEntryConsumption(int count)
    {
        // Both entries and all nested lengths are valid. Only exact consumption
        // after the first entry distinguishes the duplicate list from its control.
        using MemoryStream input = new();
        TlsUtilities.WriteUint8(0, input);
        TlsUtilities.WriteUint24(count * 6, input);
        for (int i = 0; i < count; i++)
        {
            TlsUtilities.WriteOpaque24(new byte[] { 42 }, input);
            TlsUtilities.WriteUint16(0, input);
        }
        input.Position = 0;
        if (count == 1) TlsHandshake.CheckCertificateEncoding(input);
        else Assert.Throws<TlsFatalAlert>(() => TlsHandshake.CheckCertificateEncoding(input));
        Assert.That(input.Position, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ProductionPeerLimitIndependentlyRejectsDuplicateDerInBackendParser(bool server)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 certificate = CertificateHelper.CertificateFromIdentity(key, TestPeers.Identity(1));
        using TlsHandshake client = new(certificate, false, _ => true);
        using TlsHandshake listener = new(certificate, true, _ => true);
        client.Start();
        listener.Start();
        for (int i = 0; i < 8 && (!client.Complete || !listener.Complete); i++)
        {
            Transfer(client.Engine, listener.Engine);
            Transfer(listener.Engine, client.Engine);
        }
        Assert.That(client.Complete && listener.Complete, Is.True);
        BcProtocol engine = server ? listener.Engine : client.Engine;
        // Inspect the peers actually installed by production Start(), rather than
        // reproducing the option in a test peer. No production reflection is used.
        TlsPeer peer = (TlsPeer)typeof(BcProtocol).GetProperty("Peer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
        TlsContext context = (TlsContext)typeof(BcProtocol).GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
        BcCertificate.ParseOptions options = new() { MaxChainLength = peer.GetMaxCertificateChainLength() };
        Assert.That(peer.GetMaxHandshakeMessageSize(), Is.EqualTo(TlsHandshake.MaxHandshakeMessageSize));
        using MemoryStream single = new();
        Certificates(certificate, 1).Encode(context, single, null);
        single.Position = 0;
        Assert.That(BcCertificate.Parse(options, context, single, null).Length, Is.EqualTo(1));
        using MemoryStream duplicate = new();
        Certificates(certificate, 2).Encode(context, duplicate, null);
        duplicate.Position = 0;
        Assert.Throws<TlsFatalAlert>(() => BcCertificate.Parse(options, context, duplicate, null));
    }

    private static BcCertificate Certificates(X509Certificate2 certificate, int count)
    {
        BcTlsCrypto crypto = new();
        return new BcCertificate([], Enumerable.Range(0, count)
            .Select(_ => new CertificateEntry(crypto.CreateCertificate(certificate.RawData), null)).ToArray());
    }

    private static void Transfer(BcProtocol from, BcProtocol to)
    {
        if (from.GetAvailableOutputBytes() == 0) return;
        byte[] flight = new byte[from.GetAvailableOutputBytes()];
        Assert.That(from.ReadOutput(flight, 0, flight.Length), Is.EqualTo(flight.Length));
        to.OfferInput(flight);
    }
}
