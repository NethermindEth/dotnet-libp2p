// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Nethermind.Libp2p.Core.Dto;

namespace Nethermind.Libp2p.Core.Benchmarks;

[MemoryDiagnoser]
public class IdentityPeerIdBenchmarks
{
    private Identity _ed25519 = null!;
    private Identity _rsa = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ed25519 = new Identity(keyType: KeyType.Ed25519);
        _rsa = new Identity(keyType: KeyType.Rsa);
    }

    [Benchmark]
    public PeerId PeerId_Ed25519() => _ed25519.PeerId;

    [Benchmark]
    public PeerId PeerId_Rsa() => _rsa.PeerId;
}
