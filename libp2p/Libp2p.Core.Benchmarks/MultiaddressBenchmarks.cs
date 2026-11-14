// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Dto;

namespace Nethermind.Libp2p.Core.Benchmarks;

[MemoryDiagnoser]
public class MultiaddressBenchmarks
{
    private Multiaddress _identityAddress = null!;
    private Multiaddress _sha256Address = null!;

    [GlobalSetup]
    public void Setup()
    {
        _identityAddress = $"/ip4/127.0.0.1/tcp/4001/p2p/{new Identity(keyType: KeyType.Ed25519).PeerId}";
        _sha256Address = $"/ip4/127.0.0.1/tcp/4001/p2p/{new Identity(keyType: KeyType.Rsa).PeerId}";
    }

    [Benchmark]
    public PeerId? GetPeerId_Identity() => _identityAddress.GetPeerId();

    [Benchmark]
    public PeerId? GetPeerId_Sha256() => _sha256Address.GetPeerId();
}
