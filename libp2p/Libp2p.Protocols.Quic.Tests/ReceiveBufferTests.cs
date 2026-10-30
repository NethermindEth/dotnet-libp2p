// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Quic.Tests;

#pragma warning disable CA1416 // Do not inform about platform compatibility
#pragma warning disable CA2252 // Do not inform about platform compatibility

public class ReceiveBufferTests
{
    private static readonly List<SslApplicationProtocol> ApplicationProtocols = [new("libp2p")];

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReceiveSetupFailureClosesTheChannelWithoutReturningAnUnrentedBuffer(bool locallyOpened)
    {
        await WithConnectionsAsync(async (local, remote, token) =>
        {
            OutOfMemoryException failure = new("Injected receive-buffer allocation failure");
            TaskCompletionSource rentAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ArrayPool<byte> pool = Substitute.For<ArrayPool<byte>>();
            pool.Rent(Arg.Any<int>()).Returns(_ =>
            {
                rentAttempted.TrySetResult();
                throw failure;
            });
            ILogger logger = Substitute.For<ILogger>();
            using ILoggerFactory loggerFactory = Substitute.For<ILoggerFactory>();
            loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
            ReceiveTestChannel channel = new();
            await using (QuicPump pump = await QuicPump.StartAsync(local, remote, locallyOpened, new QuicProtocol(loggerFactory, pool), channel, token))
            {
                await rentAttempted.Task.WaitAsync(token);
                await pump.Completion.WaitAsync(TimeSpan.FromSeconds(3), token);
                Assert.That((await channel.Upper.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Ended));
                Assert.That(pump.Lifetime.IsCancellationRequested, Is.False);
                pool.Received(1).Rent(Arg.Any<int>());
                pool.DidNotReceive().Return(Arg.Any<byte[]>(), Arg.Any<bool>());
                Assert.That(logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
                    .Select(call => call.GetArguments()[3]), Does.Contain(failure));
            }
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FragmentedWaitAllRetainsOnlyReceivedBytes(bool locallyOpened)
    {
        await WithConnectionsAsync(async (local, remote, token) =>
        {
            ReceiveTestChannel channel = new();
            byte[] expected = ReceiveTestChannel.Payload(513);
            ReadOnlySequence<byte> received;
            await using (QuicPump pump = await QuicPump.StartAsync(local, remote, locallyOpened, new QuicProtocol(), channel, token))
            {
                received = await channel.ReadFragmentsAsync(data => pump.SendAsync(data, token), expected,
                    Enumerable.Repeat(1, 512).ToArray(), token);
                pump.Remote.CompleteWrites();
                await channel.Upper.WriteEofAsync(token);
                await pump.Completion.WaitAsync(token);
            }
            channel.AssertCompactAndStable(expected);
            Assert.That(received.ToArray(), Is.EqualTo(expected));
        });
    }

    [TestCase("eof")]
    [TestCase("rejection")]
    [TestCase("cancellation")]
    [TestCase("failure")]
    public async Task ScratchBufferIsReturnedExactlyOnce(string ending)
    {
        await WithConnectionsAsync(async (local, remote, token) =>
        {
            TrackingReceivePool pool = new();
            ReceiveTestChannel channel = new()
            {
                Rejection = ending == "rejection" ? IOResult.Ended : null,
                WriteFailure = ending == "failure" ? new InvalidOperationException("Injected write failure") : null,
            };
            await using (QuicPump pump = await QuicPump.StartAsync(local, remote, false, new QuicProtocol(null, pool), channel, token))
            {
                await pool.WaitForRentAsync(token);
                switch (ending)
                {
                    case "eof": pump.Remote.CompleteWrites(); break;
                    case "cancellation": pump.Lifetime.Cancel(); break;
                    default:
                        await pump.SendAsync(new byte[] { 42 }, token);
                        await channel.WriteStarted.Task.WaitAsync(token);
                        break;
                }
                await pool.WaitForReturnAsync(token);
            }
            pool.AssertReturned(1);
            channel.AssertCompactAndStable(ending is "rejection" or "failure" ? [42] : [], pool);
        });
    }

    [Test]
    public async Task ConcurrentPumpsPreserveMixedAndOversizedFragments()
    {
        await WithConnectionsAsync(async (local, remote, token) =>
        {
            TrackingReceivePool pool = new();
            QuicProtocol protocol = new(null, pool);
            ReceiveTestChannel firstChannel = new();
            ReceiveTestChannel secondChannel = new();
            int[] lengths = [1, 17, 2 * 1024 + 31, 3];
            byte[] firstExpected = ReceiveTestChannel.Payload(lengths.Sum() + 1, 11);
            byte[] secondExpected = ReceiveTestChannel.Payload(lengths.Sum() + 1, 29);
            await using (QuicPump first = await QuicPump.StartAsync(local, remote, false, protocol, firstChannel, token))
            await using (QuicPump second = await QuicPump.StartAsync(local, remote, true, protocol, secondChannel, token))
            {
                await Task.WhenAll(
                    firstChannel.ReadFragmentsAsync(data => first.SendAsync(data, token), firstExpected, lengths, token),
                    secondChannel.ReadFragmentsAsync(data => second.SendAsync(data, token), secondExpected, lengths, token));
                first.Remote.CompleteWrites();
                second.Remote.CompleteWrites();
                await firstChannel.Upper.WriteEofAsync(token);
                await secondChannel.Upper.WriteEofAsync(token);
                await Task.WhenAll(first.Completion, second.Completion).WaitAsync(token);
                await pool.WaitForReturnAsync(token);
                await pool.WaitForReturnAsync(token);
            }
            pool.AssertReturned(2);
            Assert.That(pool.Rentals[0].Buffer, Is.Not.SameAs(pool.Rentals[1].Buffer));
            firstChannel.AssertCompactAndStable(firstExpected, pool);
            secondChannel.AssertCompactAndStable(secondExpected, pool);
        });
    }

    private static async Task WithConnectionsAsync(Func<QuicConnection, QuicConnection, CancellationToken, Task> test)
    {
        if (!QuicListener.IsSupported) Assert.Inconclusive("QUIC is not supported in this environment.");
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
