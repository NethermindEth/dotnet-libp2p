// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Benchmarks;

/// <summary>
/// Signing a published message and verifying a received one (Ed25519 identity).
/// </summary>
[MemoryDiagnoser]
public class SignatureBenchmarks
{
    private Identity identity = null!;
    private byte[] from = null!;
    private byte[] payload = null!;
    private Message signed = null!;

    [Params(1024, 128 * 1024)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        identity = RouterFixture.Identity(1);
        from = identity.PeerId.Bytes;
        payload = new byte[PayloadSize];
        Random.Shared.NextBytes(payload);
        signed = new Rpc().WithMessages("topic", 1, from, payload, identity).Publish[0];
    }

    [Benchmark]
    public Rpc Sign() => new Rpc().WithMessages("topic", 1, from, payload, identity);

    [Benchmark]
    public bool Verify() => signed.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign);
}
