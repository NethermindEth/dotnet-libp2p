// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using System.Formats.Asn1;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.Quic.Tests;

#pragma warning disable CA1416 // Do not inform about platform compatibility
#pragma warning disable CA2252 // Do not inform about platform compatibility

public class ProtocolTests
{
    private static readonly List<SslApplicationProtocol> ApplicationProtocols = [new("libp2p")];
    private static readonly Oid PubkeyExtensionOid = new("1.3.6.1.4.1.53594.1.1");
    private static readonly byte[] SignaturePrefix = "libp2p-tls-handshake:"u8.ToArray();

    [Test]
    public async Task Test_CreateProtocol()
    {
        CancellationTokenSource cts = new();
        QuicProtocol proto = new();

        LocalPeer peer = new(new Identity(), new PeerStore(), new ProtocolStackSettings(), null);
        ITransportContext context = new Nethermind.Libp2p.Core.TransportContext(peer, new ProtocolRef(proto), true, null);
        _ = new QuicProtocol().ListenAsync(context, "/ip4/127.0.0.1/udp/0", cts.Token);
        await Task.Delay(1000);
        cts.Cancel();
    }

    [Test]
    public async Task Test_CriticalLibp2pCertificateExtensionAccepted()
    {
        if (!QuicListener.IsSupported)
        {
            Assert.Inconclusive("QUIC is not supported in this environment.");
        }

        Identity serverIdentity = new();
        Identity clientIdentity = new();
        using ECDsa serverSessionKey = CreateCertificateKey();
        using ECDsa clientSessionKey = CreateCertificateKey();
        using X509Certificate2 serverCertificate = CreateCertificateWithCriticalLibp2pExtension(serverSessionKey, serverIdentity);
        using X509Certificate2 clientCertificate = CreateCertificateWithCriticalLibp2pExtension(clientSessionKey, clientIdentity);

        bool serverValidatedClientCertificate = false;
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await using QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = ApplicationProtocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 1,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = ApplicationProtocols,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        serverValidatedClientCertificate = certificate is X509Certificate2 x509
                            && CertificateHelper.ValidateCertificate(x509, peerId: null);
                        return serverValidatedClientCertificate;
                    },
                    ServerCertificate = serverCertificate,
                },
            }),
        }, cts.Token);

        Task<QuicConnection> acceptTask = listener.AcceptConnectionAsync(cts.Token).AsTask();

        await using QuicConnection clientConnection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 1,
            RemoteEndPoint = listener.LocalEndPoint,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols = ApplicationProtocols,
                ClientCertificates = [clientCertificate],
                LocalCertificateSelectionCallback = (_, _, certificates, _, _) => certificates[0],
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is X509Certificate2 x509
                    && CertificateHelper.ValidateCertificate(x509, serverIdentity.PeerId.ToString()),
            },
        }, cts.Token);

        await using QuicConnection serverConnection = await acceptTask;

        Assert.That(clientConnection.RemoteCertificate, Is.Not.Null);
        Assert.That(serverValidatedClientCertificate, Is.True);
    }

    private static X509Certificate2 CreateCertificateWithCriticalLibp2pExtension(ECDsa certKey, Identity identity)
    {
        byte[] signature = identity.Sign(ContentToSignFromTlsPublicKey(certKey.ExportSubjectPublicKeyInfo()));
        AsnWriter asnWriter = new(AsnEncodingRules.DER);
        asnWriter.PushSequence();
        asnWriter.WriteOctetString(identity.PublicKey.ToByteArray());
        asnWriter.WriteOctetString(signature);
        asnWriter.PopSequence();

        byte[] pubkeyExtension = asnWriter.Encode();

        Span<byte> bytes = stackalloc byte[20];
        Random.Shared.NextBytes(bytes);

        CertificateRequest certRequest = new($"SERIALNUMBER={Convert.ToHexString(bytes)}", certKey, HashAlgorithmName.SHA256);
        certRequest.CertificateExtensions.Add(new X509Extension(PubkeyExtensionOid, pubkeyExtension, critical: true));

        return certRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(100));
    }

    private static byte[] ContentToSignFromTlsPublicKey(byte[] keyInfo) => [.. SignaturePrefix, .. keyInfo];

    private static ECDsa CreateCertificateKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ECDsa.Create(ECCurve.NamedCurves.nistP256);
        }

        CngKeyCreationParameters cngParams = new()
        {
            ExportPolicy = CngExportPolicies.AllowPlaintextExport,
            KeyUsage = CngKeyUsages.AllUsages,
        };
        CngKey cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, $"libp2p-test-{Guid.NewGuid():N}", cngParams);
        return new ECDsaCng(cngKey);
    }
}
