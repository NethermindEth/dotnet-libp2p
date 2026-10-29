<!--
SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
SPDX-License-Identifier: MIT
-->

## Summary

Targets `main`, with the PR content compared against base `5811fe009c6ea2f4a0f7d55e55063381a3cc9c44`.

Received pubsub payloads require a current local subscription before message-ID calculation, signature or application validation, deferred work, caching, delivery scoring, events, or forwarding. Remote topic announcements remain available for discovery when the application subscribes later. Subscriptions and control messages in the same RPC still run, and subscribed topics retain their default delivery rewards and invalid-message penalties.

Remote memberships have per-peer and global count and topic-name byte limits. Each membership charges `2L * topic.Length` bytes for UTF-16 characters. At a global limit, memberships for locally subscribed topics can reclaim the oldest unwanted memberships, while per-peer limits still apply. Remote unsubscribe and disconnect release remote membership accounting and related metadata. Eviction attempts are bounded and reject admission if correlated indexes are inconsistent or removal cannot free capacity. With consistent indexes, the capacity pre-check proves the entire membership fits before eviction starts.

This PR also introduces remote RSA admission bounds in `Libp2p.Core` through `PublicKeyDecoder.Decode`: the modulus must have 2048 through 8192 significant bits, and the public exponent must be between 2 and 2147483647. The upper bounds are checked from DER parameters before native key import to bound subsequent verification work; native key-validity checks still apply. These are remote admission limits. Local key normalization and existing private-key imports retain their contracts, and existing identities are never regenerated silently. See [remote identity documentation](docs/development/remote-identities.md) for the policy and compatibility details.

Outstanding IWANT promises survive local unsubscribe. Unwanted replies cannot fulfill them, so expiry can still penalize the announcing peer. Regression coverage checks that the penalty is applied once.

## Validation

Validation uses pinned head `bd0a6701d67027e0f2b8f885530be8366fb2fe3f`. The pubsub and core/session suites ran before the additional RSA boundary test; production code is unchanged by this review revision. The decoder suite ran again after adding the test.

| Check | Passed | Skipped | Total |
| --- | ---: | ---: | ---: |
| Pubsub unit suite | 401 | 1 | 402 |
| Core/session E2E suite | 249 | 2 | 251 |
| `PublicKeyDecoderTests`, including the new boundary case | 66 | 0 | 66 |

The pubsub skip is an existing TODO. The two core/session skips require the host WebRTC runtime.

Commands run from the repository root, using the configured session temporary directory:

```sh
dotnet test libp2p/Libp2p.Protocols.Pubsub.Tests/Libp2p.Protocols.Pubsub.Tests.csproj -c Release -p:ArtifactsPath=$TMPDIR/pr417-feedback/art -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 1 --timeout 2m
dotnet test libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release -p:ArtifactsPath=$TMPDIR/pr417-feedback/art -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 1 --timeout 2m
dotnet test libp2p/Libp2p.Core.Tests/Libp2p.Core.Tests.csproj -c Release -p:ArtifactsPath=$TMPDIR/pr417-feedback/art -p:TreatWarningsAsErrors=true -- --filter FullyQualifiedName~PublicKeyDecoderTests --minimum-expected-tests 66 --timeout 2m
```

The added `(2048, 2147483649L)` rejection case pins the first odd exponent above the documented ceiling. A temporary mutation bypassing `CheckRemoteRsaLimits` made this case fail because the native importer accepted the key and no exception was thrown. The mutation run discovered five rejection cases: four failed as expected, while the existing even-exponent case still passed through native rejection. The production decoder was restored byte-for-byte before the final 66-test run passed.

Mutation command:

```sh
dotnet test libp2p/Libp2p.Core.Tests/Libp2p.Core.Tests.csproj -c Release -p:ArtifactsPath=$TMPDIR/pr417-feedback/art -p:TreatWarningsAsErrors=true -- --filter FullyQualifiedName~RemoteRsaRejectsUnboundedParameters --minimum-expected-tests 5 --timeout 30s
```
