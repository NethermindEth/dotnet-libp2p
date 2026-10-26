// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Protocols.Quic.Tests;

#pragma warning disable CA1416 // Do not inform about platform compatibility
#pragma warning disable CA2252 // Do not inform about platform compatibility

public class ReceiveBufferTests
{
    private static readonly List<SslApplicationProtocol> ApplicationProtocols = [new("libp2p")];

    [TestCase(false)]
    [TestCase(true)]
    public async Task FragmentedWaitAllPacksStorageAndSurvivesLaterReads(bool locallyOpened)
    {
        await WithConnectionsAsync(async (local, remote, token) =>
        {
            ReceiveTestChannel channel = new();
            byte[] expected = ReceiveTestChannel.Payload(513);
            byte[] later = ReceiveTestChannel.Payload(2051, 29);
            ReadResult direct = default;
            ReadResult retained = default;
            ReadResult laterRead = default;
            try
            {
                await using (QuicPump pump = await QuicPump.StartAsync(local, remote, locallyOpened, new QuicProtocol(), channel, token))
                {
                    direct = await channel.ReadFragmentsAsync(data => pump.SendAsync(data, token), [0x5a], [], token);
                    retained = await channel.ReadFragmentsAsync(data => pump.SendAsync(data, token), expected,
                        Enumerable.Repeat(1, 512).ToArray(), token);
                    channel.AssertReceiveBuffersReused();
                    laterRead = await channel.ReadFragmentsAsync(data => pump.SendAsync(data, token), later,
                        [1, 17, later.Length - 19], token);
                    ReceiveTestChannel.AssertCompactAndStable(retained, expected);
                    pump.Remote.CompleteWrites();
                    await channel.Upper.WriteEofAsync(token);
                    await pump.Completion.WaitAsync(token);
                }
                channel.AssertReceiveBuffersReused();
                ReceiveTestChannel.AssertCompactAndStable(retained, expected);
                ReceiveTestChannel.AssertCompactAndStable(laterRead, later);
                Assert.That(direct.Data.ToArray(), Is.EqualTo(new byte[] { 0x5a }));
            }
            finally
            {
                direct.Dispose();
                retained.Dispose();
                laterRead.Dispose();
            }
        });
    }

    [Test]
    public async Task ConcurrentPumpsPreserveMixedAndOversizedFragments()
    {
        await WithConnectionsAsync(async (local, remote, token) =>
        {
            QuicProtocol protocol = new();
            ReceiveTestChannel firstChannel = new();
            ReceiveTestChannel secondChannel = new();
            int[] lengths = [1, 17, 2 * 1024 + 31, 3];
            byte[] firstExpected = ReceiveTestChannel.Payload(lengths.Sum() + 1, 11);
            byte[] secondExpected = ReceiveTestChannel.Payload(lengths.Sum() + 1, 29);
            ReadResult[] retained = [];
            Task<ReadResult>[] reading = [];
            try
            {
                await using (QuicPump first = await QuicPump.StartAsync(local, remote, false, protocol, firstChannel, token))
                await using (QuicPump second = await QuicPump.StartAsync(local, remote, true, protocol, secondChannel, token))
                {
                    reading =
                    [
                        firstChannel.ReadFragmentsAsync(data => first.SendAsync(data, token), firstExpected, lengths, token),
                        secondChannel.ReadFragmentsAsync(data => second.SendAsync(data, token), secondExpected, lengths, token)
                    ];
                    retained = await Task.WhenAll(reading);
                    first.Remote.CompleteWrites();
                    second.Remote.CompleteWrites();
                    await firstChannel.Upper.WriteEofAsync(token);
                    await secondChannel.Upper.WriteEofAsync(token);
                    await Task.WhenAll(first.Completion, second.Completion).WaitAsync(token);
                }
                firstChannel.AssertReceiveBuffersReused();
                secondChannel.AssertReceiveBuffersReused();
                ReceiveTestChannel.AssertCompactAndStable(retained[0], firstExpected);
                ReceiveTestChannel.AssertCompactAndStable(retained[1], secondExpected);
            }
            finally
            {
                foreach (Task<ReadResult> task in reading)
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                }
            }
        });
    }

    private static async Task WithConnectionsAsync(Func<QuicConnection, QuicConnection, CancellationToken, Task> test)
    {
        if (!QuicListener.IsSupported)
        {
            if (OperatingSystem.IsWindows()) Assert.Fail("The Windows QUIC test runner must support QUIC.");
            Assert.Inconclusive("QUIC is not supported in this environment.");
        }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        using ECDsa serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Identity serverIdentity = new();
        Identity clientIdentity = new();
        using CertificateHelper.CertificateLease serverCertificate = CertificateHelper.CreateCertificateLease(serverKey, serverIdentity);
        using CertificateHelper.CertificateLease clientCertificate = CertificateHelper.CreateCertificateLease(clientKey, clientIdentity);
        await using QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = ApplicationProtocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 1,
                MaxInboundBidirectionalStreams = 4,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = ApplicationProtocols,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ClientCertificateRequired = true,
                    ServerCertificate = serverCertificate.Certificate,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is X509Certificate2 x509
                        && CertificateHelper.ValidateCertificate(x509, clientIdentity.PeerId.ToString()),
                },
            }),
        }, deadline.Token);
        Task<QuicConnection> accepting = listener.AcceptConnectionAsync(deadline.Token).AsTask();
        bool acceptedOwned = false;
        try
        {
            await using QuicConnection client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 1,
                MaxInboundBidirectionalStreams = 4,
                RemoteEndPoint = listener.LocalEndPoint,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = ApplicationProtocols,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ClientCertificates = [clientCertificate.Certificate],
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate.Certificate,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is X509Certificate2 x509
                        && CertificateHelper.ValidateCertificate(x509, serverIdentity.PeerId.ToString()),
                },
            }, deadline.Token);
            await using QuicConnection server = await accepting;
            acceptedOwned = true;
            await test(server, client, deadline.Token);
        }
        finally
        {
            if (!acceptedOwned)
            {
                deadline.Cancel();
                try { await using QuicConnection unused = await accepting.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is OperationCanceledException or QuicException) { }
            }
        }
    }

    private sealed class QuicPump(QuicStream remote, ReceiveTestChannel channel, CancellationTokenSource lifetime, Task completion) : IAsyncDisposable
    {
        public QuicStream Remote { get; } = remote;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public Task Completion { get; } = completion;

        public static async Task<QuicPump> StartAsync(QuicConnection local, QuicConnection remote, bool locallyOpened,
            QuicProtocol protocol, ReceiveTestChannel channel, CancellationToken token)
        {
            QuicConnection opener = locallyOpened ? local : remote;
            QuicConnection accepter = locallyOpened ? remote : local;
            QuicStream opened = await opener.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
            QuicStream? accepted = null;
            try
            {
                // Announce the stream and consume its setup byte before starting the receive pump.
                await opened.WriteAsync(new byte[] { 0x7e }, token);
                accepted = await accepter.AcceptInboundStreamAsync(token);
                byte[] setup = new byte[1];
                await accepted.ReadExactlyAsync(setup, token);
                Assert.That(setup[0], Is.EqualTo(0x7e));
                CancellationTokenSource lifetime = new();
                QuicStream incoming = locallyOpened ? opened : accepted;
                QuicStream sender = locallyOpened ? accepted : opened;
                return new QuicPump(sender, channel, lifetime, protocol.ExchangeData(incoming, channel, lifetime.Token));
            }
            catch
            {
                if (accepted is not null) await accepted.DisposeAsync();
                await opened.DisposeAsync();
                throw;
            }
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token) => Remote.WriteAsync(data, token).AsTask();

        public async ValueTask DisposeAsync()
        {
            Lifetime.Cancel();
            await channel.CloseAsync();
            try { await Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                await Remote.DisposeAsync();
                Lifetime.Dispose();
            }
        }
    }
}
