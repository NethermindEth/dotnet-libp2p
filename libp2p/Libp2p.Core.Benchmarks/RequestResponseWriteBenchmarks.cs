// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p.Core.Benchmarks;

[MemoryDiagnoser]
public class RequestResponseWriteBenchmarks
{
    private RequestResponseProtocol<PublicKey, PublicKey> _protocol = null!;
    private PublicKey _request = null!;
    private Channel _channel = null!;
    private IChannel _remote = null!;
    private int _wireLength;

    [Params(64, 1024, 65536)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _protocol = new RequestResponseProtocol<PublicKey, PublicKey>(
            "/bench/1.0.0", (request, _) => Task.FromResult(request), expectsResponse: _ => false);
        _request = new PublicKey { Type = KeyType.Ed25519, Data = ByteString.CopyFrom(new byte[PayloadSize]) };
        int size = _request.CalculateSize();
        _wireLength = VarInt.GetSizeInBytes(size) + size;
        _channel = new Channel();
        _remote = _channel.Reverse;
    }

    [Benchmark]
    public async Task DialOneWay()
    {
        Task<PublicKey> dial = _protocol.DialAsync(_channel, null!, _request);
        await _remote.ReadAsync(_wireLength, ReadBlockingMode.WaitAll).OrThrow();
        await dial;
    }
}
