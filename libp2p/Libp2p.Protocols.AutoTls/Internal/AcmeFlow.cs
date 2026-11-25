// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Certes.Pkcs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.AutoTls.Internal;

/// <summary>
/// Drives an ACME order end-to-end for the AutoTLS wildcard domain.
/// Uses the p2p-forge broker to publish the DNS-01 TXT record on our behalf.
/// </summary>
public sealed class AcmeFlow
{
    private static readonly TimeSpan OrderTimeout = TimeSpan.FromMinutes(5);

    private readonly AutoTlsOptions _options;
    private readonly FileCertificateStore _store;
    private readonly P2pForgeClient _forge;
    private readonly ILogger<AcmeFlow>? _logger;

    public AcmeFlow(
        IOptions<AutoTlsOptions> options,
        FileCertificateStore store,
        P2pForgeClient forge,
        ILogger<AcmeFlow>? logger = null)
    {
        _options = options.Value;
        _store = store;
        _forge = forge;
        _logger = logger;
    }

    public async Task<X509Certificate2> IssueAsync(
        Identity identity,
        IReadOnlyList<Multiaddress> announcedAddresses,
        CancellationToken ct)
    {
        if (announcedAddresses.Count == 0)
        {
            throw new AutoTlsException("AutoTLS requires at least one announced multiaddress; the broker probes these for reachability.");
        }

        // p2p-forge / AutoTLS uses CIDv1 / libp2p-key in base36 lower (k-prefix)
        // for the subdomain — NOT the default base58btc PeerId.ToString().
        string peerId = PeerIdEncoding.ToBase36CidString(identity.PeerId);
        string wildcardDomain = $"*.{peerId}.{_options.ForgeDomain}";

        ct.ThrowIfCancellationRequested();
        using CancellationTokenSource issuance = CancellationTokenSource.CreateLinkedTokenSource(ct);
        AcmeCancellationHandler handler = new(new HttpClientHandler(), issuance.Token);
        using HttpClient http = new(handler);
        AcmeContext acme = await GetOrCreateAccountAsync(http, ct);

        _logger?.LogInformation("Placing ACME order for {Domain}", wildcardDomain);
        IOrderContext order = await acme.NewOrder(new[] { wildcardDomain });

        IAuthorizationContext authz = (await order.Authorizations()).First();
        IChallengeContext dnsChallenge = await authz.Dns();
        string dnsTxt = acme.AccountKey.DnsTxt(dnsChallenge.Token);

        ct.ThrowIfCancellationRequested();
        await _forge.SubmitChallengeAsync(identity, dnsTxt, announcedAddresses, ct);

        await dnsChallenge.Validate();
        await WaitForChallengeValidAsync(dnsChallenge, ct);

        IKey csrKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        CertificateChain chain;
        try
        {
            chain = await CompleteOrderAsync(
                order, new CsrInfo { CommonName = wildcardDomain }, csrKey, issuance,
                TimeProvider.System, () => handler.RetryAfter);
        }
        catch (OperationCanceledException) when (issuance.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new AutoTlsException("ACME order timed out waiting for certificate issuance.");
        }

        ct.ThrowIfCancellationRequested();
        PfxBuilder pfxBuilder = chain.ToPfx(csrKey);
        // Empty password is acceptable on disk because FileCertificateStore enforces 0600.
        byte[] pfx = pfxBuilder.Build($"libp2p-autotls-{peerId}", string.Empty);

        X509Certificate2 cert = X509CertificateLoader.LoadPkcs12(
            pfx,
            password: string.Empty,
            keyStorageFlags: X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        _store.Save(peerId, cert);
        _logger?.LogInformation("Issued AutoTLS certificate for {PeerId}, valid until {NotAfter:o}", peerId, cert.NotAfter);
        return cert;
    }

    internal static async Task<CertificateChain> CompleteOrderAsync(
        IOrderContext order, CsrInfo csr, IKey key, CancellationTokenSource issuance, TimeProvider clock,
        Func<RetryConditionHeaderValue?> getRetryAfter)
    {
        using CancellationTokenSource deadline = new(OrderTimeout, clock);
        // Certes does not accept a token. Cancel the same attempt that owns its HTTP client.
        using CancellationTokenRegistration cancelIssuance = deadline.Token.UnsafeRegister(
            static state => ((CancellationTokenSource)state!).Cancel(), issuance);
        CancellationToken ct = issuance.Token;

        ct.ThrowIfCancellationRequested();
        Order? state = await order.Resource();
        while (state?.Status == OrderStatus.Pending)
        {
            state = await PollOrderAsync(order, getRetryAfter(), clock, ct);
        }

        ct.ThrowIfCancellationRequested();
        if (state?.Status != OrderStatus.Ready)
        {
            throw new AutoTlsException($"ACME order is not ready to finalize (status: {state?.Status}).");
        }

        state = await order.Finalize(csr, key);
        while (state?.Status is OrderStatus.Pending or OrderStatus.Processing)
        {
            state = await PollOrderAsync(order, getRetryAfter(), clock, ct);
        }

        ct.ThrowIfCancellationRequested();
        if (state?.Status != OrderStatus.Valid)
        {
            throw new AutoTlsException($"ACME order did not become valid (status: {state?.Status}).");
        }

        CertificateChain chain = await order.Download();
        ct.ThrowIfCancellationRequested();
        return chain;
    }

    private static async Task<Order> PollOrderAsync(
        IOrderContext order, RetryConditionHeaderValue? retryAfter, TimeProvider clock, CancellationToken ct)
    {
        TimeSpan delay = retryAfter?.Delta ?? (retryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.Zero;
        delay = delay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay;
        // The deadline may end the attempt before the next poll is allowed.
        delay = delay >= OrderTimeout ? Timeout.InfiniteTimeSpan : delay;
        await Task.Delay(delay, clock, ct);
        ct.ThrowIfCancellationRequested();
        return await order.Resource();
    }

    private async Task<AcmeContext> GetOrCreateAccountAsync(HttpClient http, CancellationToken ct)
    {
        Uri directory = new(_options.AcmeDirectoryUrl);
        AcmeHttpClient acmeHttp = new(directory, http);
        string? pem = _store.TryLoadAccountKey();
        if (pem is not null)
        {
            IKey existing = KeyFactory.FromPem(pem);
            return new AcmeContext(directory, existing, acmeHttp);
        }

        AcmeContext acme = new(directory, http: acmeHttp);
        string contact = string.IsNullOrEmpty(_options.ContactEmail)
            ? "noreply@libp2p.direct"
            : _options.ContactEmail;
        await acme.NewAccount(contact, termsOfServiceAgreed: true);
        ct.ThrowIfCancellationRequested();
        _store.SaveAccountKey(acme.AccountKey.ToPem());
        return acme;
    }

    private async Task WaitForChallengeValidAsync(IChallengeContext challenge, CancellationToken ct)
    {
        TimeSpan delay = TimeSpan.FromSeconds(2);
        TimeSpan max = TimeSpan.FromSeconds(15);
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            Challenge state = await challenge.Resource();
            if (state.Status == ChallengeStatus.Valid)
            {
                return;
            }
            if (state.Status == ChallengeStatus.Invalid)
            {
                throw new AutoTlsException($"ACME DNS-01 challenge failed: {state.Error?.Detail ?? "unknown error"}");
            }
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(max.TotalSeconds, delay.TotalSeconds * 2));
        }
        throw new AutoTlsException("ACME DNS-01 challenge timed out waiting for validation.");
    }

    internal sealed class AcmeCancellationHandler(HttpMessageHandler inner, CancellationToken issuanceToken)
        : DelegatingHandler(inner)
    {
        // Each issuance uses its own client and awaits its ACME calls in sequence.
        internal RetryConditionHeaderValue? RetryAfter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using CancellationTokenSource requestCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, issuanceToken);
            CancellationToken ct = requestCancellation.Token;
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage response = await base.SendAsync(request, ct);
            try
            {
                // Keep the issuance token in effect while reading the response, too.
                await response.Content.LoadIntoBufferAsync(ct);
                RetryAfter = response.Headers.RetryAfter;
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }
}
