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
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.AutoTls.Internal;

/// <summary>
/// Drives an ACME order end-to-end for the AutoTLS wildcard domain.
/// Uses the p2p-forge broker to publish the DNS-01 TXT record on our behalf.
/// </summary>
public sealed class AcmeFlow
{
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

        string peerId = identity.PeerId.ToString();
        string wildcardDomain = $"*.{peerId}.{_options.ForgeDomain}";

        AcmeContext acme = await GetOrCreateAccountAsync(ct);

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
        CertificateChain chain = await order.Generate(
            new CsrInfo { CommonName = wildcardDomain },
            csrKey);

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

    private async Task<AcmeContext> GetOrCreateAccountAsync(CancellationToken ct)
    {
        Uri directory = new(_options.AcmeDirectoryUrl);
        string? pem = _store.TryLoadAccountKey();
        if (pem is not null)
        {
            IKey existing = KeyFactory.FromPem(pem);
            return new AcmeContext(directory, existing);
        }

        AcmeContext acme = new(directory);
        string contact = string.IsNullOrEmpty(_options.ContactEmail)
            ? "noreply@libp2p.direct"
            : _options.ContactEmail;
        await acme.NewAccount(contact, termsOfServiceAgreed: true);
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
}
