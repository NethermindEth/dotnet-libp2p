# AutoTLS

Automatic browser-trusted TLS certificates for libp2p nodes, via the
[p2p-forge](https://github.com/ipshipyard/p2p-forge) registration broker and
Let's Encrypt.

## What this is (and isn't)

The libp2p TLS protocol (`/tls/1.0.0`, see `Libp2p.Protocols.Tls`) already
encrypts every connection with self-signed, identity-derived certificates.
**That is not what AutoTLS replaces.**

AutoTLS exists so a non-browser libp2p node can obtain a publicly-trusted
wildcard certificate for `*.<base36 PeerID CID>.libp2p.direct`. With such a
certificate, browsers can open Secure WebSocket (WSS) connections directly to
your node, which the in-browser TLS stack would otherwise reject.

This module produces and renews the certificate. Consuming it requires a
WebSocket transport that reads from `ITlsCertificateProvider` (not yet
shipped in this repo).

## Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Protocols.AutoTls;

services
    .AddLibp2p(builder => /* ... */)
    .AddAutoTls(opts =>
    {
        opts.ContactEmail = "you@example.com";
        opts.AcmeDirectoryUrl = AutoTlsOptions.StagingAcmeDirectoryUrl; // for testing
    });

// After your peer starts listening:
var provider = serviceProvider.GetRequiredService<ITlsCertificateProvider>();
provider.Configure(localPeer.Identity, localPeer.ListenAddresses.ToArray());

provider.CertificateChanged += cert =>
{
    // hand `cert` to your WSS listener
};
```

## Behavior

- On startup, a stored certificate (if any) is loaded from
  `CertificateStorePath` (default: `<AppContext.BaseDirectory>/autotls`).
- If no stored cert, or its remaining validity is less than `RenewBefore`
  (default 30 days), an issuance is started: ACME order, DNS-01 challenge,
  broker submission, validation, finalize, save.
- After issuance the manager sleeps until the next renewal window.
- Failures back off exponentially up to `MaxRetryDelay` (default 1 hour).

## Caveats

- **Public reachability is required.** The p2p-forge broker probes the
  multiaddrs you supply before publishing the DNS-01 TXT record. Issuance
  will fail on unreachable nodes. There is no automatic reachability gate
  yet — only call `Configure` once you know the node is reachable.
- **Let's Encrypt rate limits apply** (~50 certs / week / registered domain).
  The on-disk store prevents re-issuance churn across restarts; do not delete
  it casually.
- **HTTP peer-id auth is implemented to spec but unverified against a live
  broker** in this PR. Test against staging
  (`AutoTlsOptions.StagingAcmeDirectoryUrl`) before pointing at production.
- **No transport currently consumes the certificate.** A WSS transport is a
  prerequisite to surface this feature to end users.

## References

- [Announcing AutoTLS — libp2p blog](https://blog.libp2p.io/autotls/)
- [p2p-forge](https://github.com/ipshipyard/p2p-forge)
- [HTTP peer-id-auth spec](https://github.com/libp2p/specs/blob/master/http/peer-id-auth.md)
- [Certes (ACME client)](https://github.com/fszlin/certes)
