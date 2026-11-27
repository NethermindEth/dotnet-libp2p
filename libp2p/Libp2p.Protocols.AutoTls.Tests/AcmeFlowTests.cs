// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Certes.Pkcs;
using Microsoft.Extensions.Options;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.AutoTls.Internal;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Nethermind.Libp2p.Protocols.AutoTls.Tests;

public class AcmeFlowTests
{
    private const string Domain = "*.example.com";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestCase(false, 0, false)]
    [TestCase(false, 7, true)]
    [TestCase(true, 7, false)]
    [TestCase(true, 299, false)]
    public async Task CompletesOrderAfterServerRequestedWait(bool finalized, int retryAfter, bool date)
    {
        ManualTimeProvider clock = new();
        using CancellationTokenSource issuance = new();
        IKey key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        using ECDsa signer = ECDsa.Create();
        signer.ImportFromPem(key.ToPem());
        CertificateRequest request = new($"CN={Domain}", signer, HashAlgorithmName.SHA256);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1));
        TimeSpan retryDelay = TimeSpan.FromSeconds(retryAfter);
        RetryConditionHeaderValue? header = date
            ? new RetryConditionHeaderValue(clock.GetUtcNow() + retryDelay)
            : retryAfter > 0 ? new RetryConditionHeaderValue(retryDelay) : null;
        OrderStub order = new(finalized ? OrderStatus.Ready : OrderStatus.Pending,
            finalized ? OrderStatus.Processing : OrderStatus.Valid)
        {
            Chain = new CertificateChain(certificate.ExportCertificatePem())
        };

        Task<CertificateChain> completion = AcmeFlow.CompleteOrderAsync(
            order, new CsrInfo { CommonName = Domain }, key, issuance, clock, () => header);
        TimeSpan expectedDelay = TimeSpan.FromSeconds(Math.Max(retryAfter, 1));
        Assert.That(clock.ActiveTimersFor(expectedDelay), Is.EqualTo(1));
        int reads = order.Reads;
        order.ResourceStatus = finalized ? OrderStatus.Valid : OrderStatus.Ready;
        clock.Advance(expectedDelay - TimeSpan.FromMilliseconds(1));
        Assert.That(order.Reads, Is.EqualTo(reads));
        Assert.That(order.Downloads, Is.Zero);
        Assert.That(order.Finalizations, Is.EqualTo(finalized ? 1 : 0));

        clock.Advance(TimeSpan.FromMilliseconds(1));
        CertificateChain chain = await completion.WaitAsync(TestTimeout);

        byte[] pfx = chain.ToPfx(key).Build("test", string.Empty);
        using X509Certificate2 imported = X509CertificateLoader.LoadPkcs12(
            pfx, string.Empty, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(order.Finalizations, Is.EqualTo(1));
            Assert.That(order.Downloads, Is.EqualTo(1));
            Assert.That(imported.RawData, Is.EqualTo(certificate.RawData));
            Assert.That(imported.HasPrivateKey, Is.True);
            Assert.That(clock.ActiveTimers, Is.Zero);
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void PendingOrProcessingOrderStopsOnDeadlineOrShutdown(bool finalized, bool shutdown)
    {
        ManualTimeProvider clock = new();
        using CancellationTokenSource issuance = new();
        OrderStub order = new(finalized ? OrderStatus.Ready : OrderStatus.Pending, OrderStatus.Processing);
        RetryConditionHeaderValue header = new(TimeSpan.FromMinutes(15));
        Task<CertificateChain> completion = AcmeFlow.CompleteOrderAsync(
            order, new CsrInfo { CommonName = Domain }, KeyFactory.NewKey(KeyAlgorithm.ES256),
            issuance, clock, () => header);
        int reads = order.Reads;

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.That(completion.IsCompleted, Is.False);
        if (shutdown)
        {
            issuance.Cancel();
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.CatchAsync<OperationCanceledException>(async () => await completion.WaitAsync(TestTimeout));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(order.Reads, Is.EqualTo(reads));
            Assert.That(order.Finalizations, Is.EqualTo(finalized ? 1 : 0));
            Assert.That(order.Downloads, Is.Zero);
            Assert.That(clock.ActiveTimers, Is.Zero);
        }
    }

    [Test]
    public async Task FinalizationDoesNotResetOrderDeadline()
    {
        ManualTimeProvider clock = new();
        using CancellationTokenSource issuance = new();
        OrderStub order = new(OrderStatus.Pending, OrderStatus.Processing);
        RetryConditionHeaderValue header = new(TimeSpan.FromMinutes(4));
        Task<CertificateChain> completion = AcmeFlow.CompleteOrderAsync(
            order, new CsrInfo { CommonName = Domain }, KeyFactory.NewKey(KeyAlgorithm.ES256),
            issuance, clock, () => header);

        order.ResourceStatus = OrderStatus.Ready;
        clock.Advance(TimeSpan.FromMinutes(4));
        await order.Finalized.Task.WaitAsync(TestTimeout);
        Assert.That(completion.IsCompleted, Is.False);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.CatchAsync<OperationCanceledException>(async () => await completion.WaitAsync(TestTimeout));
        Assert.That(order.Downloads, Is.Zero);
    }

    [TestCase(OrderStatus.Invalid, OrderStatus.Valid, 0)]
    [TestCase(OrderStatus.Valid, OrderStatus.Valid, 0)]
    [TestCase(OrderStatus.Ready, OrderStatus.Invalid, 1)]
    public void RejectsUnexpectedStatusWithoutDownloading(OrderStatus initial, OrderStatus result, int finalizations)
    {
        using CancellationTokenSource issuance = new();
        OrderStub order = new(initial, result);

        Assert.ThrowsAsync<AutoTlsException>(async () => await AcmeFlow.CompleteOrderAsync(
            order, new CsrInfo { CommonName = Domain }, KeyFactory.NewKey(KeyAlgorithm.ES256),
            issuance, new ManualTimeProvider(), static () => null).WaitAsync(TestTimeout));

        Assert.That(order.Finalizations, Is.EqualTo(finalizations));
        Assert.That(order.Downloads, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationInterruptsAcmeHttpRequest(bool responseBody)
    {
        using CancellationTokenSource issuance = new();
        BlockingHandler transport = new(responseBody);
        using HttpClient http = new(new AcmeFlow.AcmeCancellationHandler(transport, issuance.Token));
        Task<string> request = http.GetStringAsync("https://example.com/acme/order");

        await transport.Blocked.Task.WaitAsync(TestTimeout);
        issuance.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await request.WaitAsync(TestTimeout));
    }

    [Test]
    public async Task SavesRegisteredAccountKeyBeforeHonoringCancellation()
    {
        DirectoryInfo directory = System.IO.Directory.CreateTempSubdirectory();
        try
        {
            IOptions<AutoTlsOptions> options = Options.Create(new AutoTlsOptions
            {
                AcmeDirectoryUrl = "https://example.com/directory",
                CertificateStorePath = directory.FullName
            });
            FileCertificateStore store = new(options);
            using CancellationTokenSource shutdown = new();
            AccountRegistrationHandler transport = new(shutdown.Cancel);
            using HttpClient http = new(new AcmeFlow.AcmeCancellationHandler(transport, shutdown.Token));
            AcmeFlow flow = new(options, store, new P2pForgeClient(http, options));

            OperationCanceledException? canceled = Assert.CatchAsync<OperationCanceledException>(async () =>
                await flow.GetOrCreateAccountAsync(http, shutdown.Token).WaitAsync(TestTimeout));
            Assert.That(canceled?.CancellationToken, Is.EqualTo(shutdown.Token));
            string? savedKey = store.TryLoadAccountKey();
            Assert.That(savedKey, Is.Not.Null);

            using HttpClient retryHttp = new(transport, disposeHandler: false);
            AcmeFlow retry = new(options, new FileCertificateStore(options), new P2pForgeClient(retryHttp, options));
            AcmeContext account = await retry.GetOrCreateAccountAsync(retryHttp, CancellationToken.None).WaitAsync(TestTimeout);
            Assert.That(account.AccountKey.ToPem(), Is.EqualTo(savedKey));
            Assert.That(transport.Registrations, Is.EqualTo(1));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class AccountRegistrationHandler(Action cancel) : HttpMessageHandler
    {
        public int Registrations { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.Method.Method, request.RequestUri?.AbsolutePath)
            {
                case ("GET", "/directory"):
                    return JsonResponse(HttpStatusCode.OK, """
                        {
                            "newNonce": "https://example.com/acme/new-nonce",
                            "newAccount": "https://example.com/acme/new-account"
                        }
                        """);
                case ("HEAD", "/acme/new-nonce"):
                    HttpResponseMessage nonce = new(HttpStatusCode.OK);
                    nonce.Headers.Add("Replay-Nonce", Base64Url.EncodeToString("initial nonce"u8));
                    return nonce;
                case ("POST", "/acme/new-account"):
                    HttpResponseMessage account = JsonResponse(HttpStatusCode.Created, """{"status": "valid"}""");
                    account.Headers.Location = new Uri("https://example.com/acme/account/1");
                    account.Headers.Add("Replay-Nonce", Base64Url.EncodeToString("account nonce"u8));
                    // Deliver the successful registration even though shutdown has started.
                    await account.Content.LoadIntoBufferAsync(cancellationToken);
                    Registrations++;
                    cancel();
                    return account;
                default:
                    throw new InvalidOperationException($"Unexpected ACME request: {request.Method} {request.RequestUri}");
            }
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class OrderStub(OrderStatus initial, OrderStatus result) : IOrderContext
    {
        public Uri Location { get; } = new("https://example.com/acme/order");
        public int RetryAfter => 0;
        public int Reads { get; private set; }
        public int Finalizations { get; private set; }
        public int Downloads { get; private set; }
        public OrderStatus ResourceStatus { get; set; } = initial;
        public CertificateChain? Chain { get; init; }
        public TaskCompletionSource Finalized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Order> Resource()
        {
            Reads++;
            return Task.FromResult(new Order
            {
                Status = ResourceStatus,
                Identifiers = [new Identifier { Type = IdentifierType.Dns, Value = Domain }]
            });
        }

        public Task<Order> Finalize(byte[] csr)
        {
            Assert.That(csr, Is.Not.Empty);
            Finalizations++;
            Finalized.SetResult();
            return Task.FromResult(new Order { Status = result });
        }

        public Task<CertificateChain> Download(string preferredChain)
        {
            Downloads++;
            return Task.FromResult(Chain ?? throw new InvalidOperationException("Unexpected download."));
        }

        public Task<IEnumerable<IAuthorizationContext>> Authorizations() => throw new NotSupportedException();
    }

    private sealed class BlockingHandler(bool responseBody) : HttpMessageHandler
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (responseBody)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent(Blocked) };
            }

            Blocked.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Expected cancellation.");
        }
    }

    private sealed class BlockingContent(TaskCompletionSource blocked) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            blocked.SetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
