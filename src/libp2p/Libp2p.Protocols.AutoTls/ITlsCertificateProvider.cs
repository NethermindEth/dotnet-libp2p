// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.AutoTls;

public interface ITlsCertificateProvider
{
    X509Certificate2? Current { get; }
    event Action<X509Certificate2>? CertificateChanged;
    Task<X509Certificate2> WaitForCertificateAsync(CancellationToken ct);

    /// <summary>
    /// Provide the local peer identity and the publicly-reachable multiaddrs
    /// that the p2p-forge broker should probe before issuing a certificate.
    /// </summary>
    void Configure(Identity identity, IReadOnlyList<Multiaddress> announcedAddresses);
}
