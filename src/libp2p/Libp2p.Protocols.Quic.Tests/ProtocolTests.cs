// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.TestsBase;
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

    [TestCase(KeyType.Ed25519)]
    [TestCase(KeyType.Secp256K1)]
    public async Task QuicTransport_ConnectsAuthenticatedPeersAndExchangesData(KeyType keyType)
    {
        if (!QuicListener.IsSupported)
        {
            Assert.Inconclusive("QUIC is not supported in this environment.");
        }

        AuthenticationLoggerFactory listenerLogs = new();
        using ServiceProvider listenerServices = new ServiceCollection()
            .AddLibp2p(builder => builder.WithQuic().AddProtocol<IncrementNumberTestProtocol>().AddProtocol<HalfCloseTestProtocol>())
            .AddSingleton<ILoggerFactory>(listenerLogs)
            .BuildServiceProvider();
        using ServiceProvider dialerServices = new ServiceCollection()
            .AddLibp2p(builder => builder.WithQuic().AddProtocol<IncrementNumberTestProtocol>().AddProtocol<HalfCloseTestProtocol>())
            .AddSingleton<ILoggerFactory>(new TestContextLoggerFactory())
            .BuildServiceProvider();
        await using ILocalPeer listener = listenerServices.GetRequiredService<IPeerFactory>().Create(new Identity(keyType: keyType));
        await using ILocalPeer dialer = dialerServices.GetRequiredService<IPeerFactory>().Create(new Identity(keyType: keyType));
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));

        await listener.StartListenAsync(["/ip4/127.0.0.1/udp/0/quic-v1"], cts.Token);
        Assert.That(listener.ListenAddresses, Has.Count.EqualTo(1));

        IPEndPoint endpoint = new(IPAddress.Loopback, int.Parse(listener.ListenAddresses.Single().Get<UDP>().ToString()));
        try
        {
            QuicConnection rejectedClient = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 1,
                RemoteEndPoint = endpoint,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = ApplicationProtocols,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            }, cts.Token);
            await rejectedClient.DisposeAsync();
        }
        catch (AuthenticationException)
        {
        }
        catch (QuicException)
        {
        }
        await listenerLogs.Rejected.Task.WaitAsync(cts.Token);

        ISession session = await dialer.DialAsync([.. listener.ListenAddresses], cts.Token);
        Assert.That(session.RemoteAddress.GetPeerId(), Is.EqualTo(listener.Identity.PeerId));
        Assert.That(await session.DialAsync<IncrementNumberTestProtocol, int, int>(41, cts.Token), Is.EqualTo(42));
        Assert.That(await session.DialAsync<HalfCloseTestProtocol, int, int>(41, cts.Token), Is.EqualTo(42));
    }

    private sealed class HalfCloseTestProtocol : ISessionProtocol<int, int>
    {
        public string Id => "/test/quic-half-close/1.0.0";

        public async Task<int> DialAsync(IChannel downChannel, ISessionContext context, int request)
        {
            await downChannel.WriteVarintAsync(request);
            await downChannel.WriteEofAsync();
            return await downChannel.ReadVarintAsync(context.UpgradeOptions?.CancellationToken ?? default);
        }

        public async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            int request = await downChannel.ReadVarintAsync();
            ReadResult result = await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny);
            Assert.That(result.Result, Is.EqualTo(IOResult.Ended));
            await downChannel.WriteVarintAsync(request + 1);
        }
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
        using CertificateKey serverSessionKey = new();
        using CertificateKey clientSessionKey = new();
        using X509Certificate2 serverCertificate = CreateCertificateWithCriticalLibp2pExtension(serverSessionKey.Key, serverIdentity);
        using X509Certificate2 clientCertificate = CreateCertificateWithCriticalLibp2pExtension(clientSessionKey.Key, clientIdentity);

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
        using CngKey cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, $"libp2p-test-{Guid.NewGuid():N}", cngParams);
        return new ECDsaCng(cngKey);
    }

    private sealed class CertificateKey : IDisposable
    {
        public ECDsa Key { get; } = CreateCertificateKey();

        public void Dispose()
        {
            try
            {
                if (Key is ECDsaCng cngKey)
                {
                    using CngKey key = cngKey.Key;
                    key.Delete();
                }
            }
            finally
            {
                Key.Dispose();
            }
        }
    }

    private sealed class AuthenticationLoggerFactory : ILoggerFactory
    {
        public TaskCompletionSource Rejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => new AuthenticationLogger(Rejected);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class AuthenticationLogger(TaskCompletionSource rejected) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Debug && formatter(state, exception) is
                    "QUIC client authentication failed" or "QUIC client connection failed")
                {
                    rejected.TrySetResult();
                }
            }
        }
    }
}
