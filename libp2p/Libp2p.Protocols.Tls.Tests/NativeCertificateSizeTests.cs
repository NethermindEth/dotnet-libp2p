// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Quic;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using BcCertificate = Org.BouncyCastle.Tls.Certificate;
using BcRequest = Org.BouncyCastle.Tls.CertificateRequest;
using HashAlgorithm = Org.BouncyCastle.Tls.HashAlgorithm;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

/// <summary>
/// Characterizes the native baseline independently of the production TLS backend.
/// The sender emits exactly one valid libp2p certificate of an exact DER size.
/// </summary>
public class NativeCertificateSizeTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task ReportsNativeSingleCertificateSizeBoundary(bool receiverIsServer, bool allowTlsResume)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 seed = CertificateHelper.CertificateFromIdentity(key, TestPeers.Identity(2));
        // On Windows the certificate owns the actual process key, not 'key'.
        using ECDsa signingKey = seed.GetECDsaPrivateKey()!;
        using ECDsa localKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 local = CertificateHelper.CertificateFromIdentity(localKey, TestPeers.Identity(1));

        const int smallest = 1024;
        const int largest = 2 * 1024 * 1024;
        Assert.That(await AcceptsAsync(smallest), Is.True, "The valid one-certificate control must work.");
        Assert.That(await AcceptsAsync(largest), Is.False, "The bounded search must bracket the native limit.");
        int accepted = smallest;
        int rejected = largest;
        while (rejected - accepted > 1)
        {
            int size = accepted + (rejected - accepted) / 2;
            if (await AcceptsAsync(size)) accepted = size;
            else rejected = size;
        }

        // Record repeat observations: ticket serialization can add a second limit
        // whose boundary varies with session data. Do not present it as an ingress cap.
        bool acceptedRetest = await AcceptsAsync(accepted);
        bool rejectedRetest = await AcceptsAsync(rejected);
        TestContext.Out.WriteLine($"NATIVE_CERTIFICATE_BOUNDARY OS={Environment.OSVersion}; runtime={Environment.Version}; receiver={(receiverIsServer ? "server" : "client")}; allowTlsResume={allowTlsResume}; sampleAcceptedDer={accepted}; sampleRejectedDer={rejected}; acceptedRetest={acceptedRetest}; rejectedRetest={rejectedRetest}; certificateBodyAtSample={accepted + 9}; handshakeMessageAtSample={accepted + 13}");

        async Task<bool> AcceptsAsync(int derSize)
        {
            using X509Certificate2 remote = CreatePaddedCertificate(seed, signingKey, derSize);
            Assert.That(remote.RawData, Has.Length.EqualTo(derSize));
            Assert.That(CertificateHelper.ValidateCertificate(remote, TestPeers.PeerId(2).ToString()), Is.True);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            using TcpClient sender = new();
            Task connect = sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token).AsTask();
            using TcpClient receiver = await listener.AcceptTcpClientAsync(deadline.Token);
            await connect;
            using SslStream ssl = new(receiver.GetStream(), false);
            BcTlsCrypto crypto = new();
            BcCertificate chain = new([], [new CertificateEntry(crypto.CreateCertificate(remote.RawData), null)]);
            byte[] exportedKey = signingKey.ExportPkcs8PrivateKey();
            Org.BouncyCastle.Crypto.AsymmetricKeyParameter privateKey;
            try { privateKey = PrivateKeyFactory.CreateKey(exportedKey); }
            finally { CryptographicOperations.ZeroMemory(exportedKey); }
            byte[]? serializedCertificate = null;
            void Capture(BcCertificate certificate, TlsContext context)
            {
                using MemoryStream encoded = new();
                certificate.Encode(context, encoded, null);
                serializedCertificate = encoded.ToArray();
            }
            Org.BouncyCastle.Tls.TlsProtocol wire = receiverIsServer
                ? new RecordingClient(sender.GetStream(), Capture)
                : new RecordingServer(sender.GetStream(), Capture);
            Task send = Task.Run(() =>
            {
                if (wire is TlsClientProtocol client)
                    client.Connect(new WireClient(crypto, chain, privateKey));
                else
                    ((TlsServerProtocol)wire).Accept(new WireServer(crypto, chain, privateKey));
                wire.Stream.WriteByte(42);
            });
            bool validated = false;
            bool callbackInvoked = false;
            RemoteCertificateValidationCallback validate = (_, certificate, _, _) =>
            {
                callbackInvoked = true;
                validated = certificate is X509Certificate2 x509
                    && x509.RawData.SequenceEqual(remote.RawData)
                    && CertificateHelper.ValidateCertificate(x509, TestPeers.PeerId(2).ToString());
                return validated;
            };
            try
            {
                if (receiverIsServer)
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = local,
                        AllowTlsResume = allowTlsResume,
                        ApplicationProtocols = [new SslApplicationProtocol("libp2p")],
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = validate,
                    }, deadline.Token);
                else
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "libp2p",
                        AllowTlsResume = allowTlsResume,
                        ApplicationProtocols = [new SslApplicationProtocol("libp2p")],
                        ClientCertificates = [local],
                        LocalCertificateSelectionCallback = (_, _, _, _, _) => local,
                        EnabledSslProtocols = SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = validate,
                    }, deadline.Token);
                byte[] data = new byte[1];
                await ssl.ReadExactlyAsync(data, deadline.Token);
                await send.WaitAsync(deadline.Token);
                Assert.That(data[0], Is.EqualTo(42));
                Assert.That(validated, Is.True);
                Assert.That(ssl.IsMutuallyAuthenticated, Is.True);
                return true;
            }
            catch (AuthenticationException ex)
            {
                Assert.That(callbackInvoked, Is.False, "A size rejection must precede leaf validation.");
                TestContext.Out.WriteLine($"NATIVE_SIZE_REJECT receiver={(receiverIsServer ? "server" : "client")}; allowTlsResume={allowTlsResume}; der={derSize}; exception={ex.GetType().Name}; detail={ex.GetBaseException().Message}");
                return false;
            }
            finally
            {
                sender.Dispose();
                receiver.Dispose();
                try { await send.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (IOException) { /* The native receiver may abort while the sender writes. */ }
                Assert.That(serializedCertificate, Is.Not.Null);
                using MemoryStream encoded = new(serializedCertificate!);
                Assert.That(encoded.ReadByte(), Is.Zero, "Empty certificate_request_context.");
                Assert.That(TlsUtilities.ReadUint24(encoded), Is.EqualTo(derSize + 5));
                Assert.That(TlsUtilities.ReadOpaque24(encoded), Is.EqualTo(remote.RawData));
                Assert.That(TlsUtilities.ReadUint16(encoded), Is.Zero, "Empty entry extensions.");
                Assert.That(encoded.Position, Is.EqualTo(encoded.Length), "Exactly one serialized entry.");
            }
        }
    }

    private static X509Certificate2 CreatePaddedCertificate(X509Certificate2 seed, ECDsa key, int derSize)
    {
        int padding = derSize - seed.RawData.Length - 24;
        for (int attempt = 0; attempt < 64; attempt++)
        {
            System.Security.Cryptography.X509Certificates.CertificateRequest request = new(seed.SubjectName, key, HashAlgorithmName.SHA256);
            foreach (X509Extension extension in seed.Extensions) request.CertificateExtensions.Add(extension);
            // A noncritical private extension must not change libp2p authentication.
            request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.53594.999", new byte[padding], false));
            X509Certificate2 result = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            int difference = derSize - result.RawData.Length;
            if (difference == 0) return result;
            result.Dispose();
            padding += difference;
        }
        throw new InvalidOperationException("Could not produce an exact-size ECDSA certificate.");
    }

    private static TlsCredentials Credentials(TlsContext context, BcTlsCrypto crypto, BcCertificate certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter key) =>
        new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), crypto, key, certificate,
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));

    private sealed class RecordingClient(Stream stream, Action<BcCertificate, TlsContext> capture) : TlsClientProtocol(stream)
    {
        protected override void Send13CertificateMessage(BcCertificate certificate)
        {
            capture(certificate, Context);
            base.Send13CertificateMessage(certificate);
        }
    }

    private sealed class RecordingServer(Stream stream, Action<BcCertificate, TlsContext> capture) : TlsServerProtocol(stream)
    {
        protected override void Send13CertificateMessage(BcCertificate certificate)
        {
            capture(certificate, Context);
            base.Send13CertificateMessage(certificate);
        }
    }

    private sealed class WireServer(BcTlsCrypto crypto, BcCertificate certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter key) : DefaultTlsServer(crypto)
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13];
        protected override IList<ProtocolName> GetProtocolNames() => [ProtocolName.AsUtf8Encoding("libp2p")];
        public override TlsCredentials GetCredentials() => Credentials(m_context, crypto, certificate, key);
        public override BcRequest GetCertificateRequest() => new([], [SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa)], null, null);
        public override void NotifyClientCertificate(BcCertificate clientCertificate) { }
    }

    private sealed class WireClient(BcTlsCrypto crypto, BcCertificate certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter key) : DefaultTlsClient(crypto)
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13];
        protected override IList<ProtocolName> GetProtocolNames() => [ProtocolName.AsUtf8Encoding("libp2p")];
        public override TlsAuthentication GetAuthentication() => new Authentication(() => Credentials(m_context, crypto, certificate, key));
    }

    private sealed class Authentication(Func<TlsCredentials> credentials) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate) { }
        public TlsCredentials GetClientCredentials(BcRequest certificateRequest) => credentials();
    }
}
