// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Protocols.AutoTls;

public sealed class AutoTlsOptions
{
    public const string DefaultAcmeDirectoryUrl = "https://acme-v02.api.letsencrypt.org/directory";
    public const string StagingAcmeDirectoryUrl = "https://acme-staging-v02.api.letsencrypt.org/directory";
    public const string DefaultForgeRegistrationEndpoint = "https://registration.libp2p.direct/v1/_acme-challenge";
    public const string DefaultForgeDomain = "libp2p.direct";

    public string AcmeDirectoryUrl { get; set; } = DefaultAcmeDirectoryUrl;
    public string ForgeRegistrationEndpoint { get; set; } = DefaultForgeRegistrationEndpoint;
    public string ForgeDomain { get; set; } = DefaultForgeDomain;
    public string? ContactEmail { get; set; }
    public string CertificateStorePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "autotls");
    public TimeSpan RenewBefore { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);
    public IReadOnlyList<Multiaddress>? AnnouncedAddresses { get; set; }
}
