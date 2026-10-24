// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Text;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Network;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht;

/// <summary>Queries and replicates to the nearest peers currently in the local routing table.</summary>
public sealed class DhtClient : IDisposable, IAsyncDisposable
{
    private readonly SharedDhtState _state;
    private readonly IValueStore _values;
    private readonly IRecordValidator _validator;
    private readonly KadDhtOptions _options;
    private readonly Func<DhtNode, byte[], byte[], CancellationToken, Task<bool>> _put;
    private readonly Func<DhtNode, byte[], CancellationToken, Task<GetValueResult>> _get;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _lifecycle = new();
    private TaskCompletionSource? _drained;
    private int _operations;
    private bool _disposed;
    private Task? _disposal;

    [Obsolete("Use the constructor accepting IDhtMessageSender, IValueStore, IRecordValidator and KadDhtOptions, or the registered peer service.")]
    public DhtClient(SharedDhtState sharedState, LibP2pKademliaMessageSender<PublicKey, DhtNode> messageSender, ILoggerFactory? loggerFactory = null)
    {
        _state = sharedState ?? throw new ArgumentNullException(nameof(sharedState));
        ArgumentNullException.ThrowIfNull(messageSender);
        _values = sharedState.Values;
        _validator = CompositeRecordValidator.CreateDefault();
        _options = new KadDhtOptions();
        _logger = loggerFactory?.CreateLogger<DhtClient>();
        _put = (peer, key, value, token) => messageSender.PutValue(peer, key, value, token: token);
        _get = async (peer, key, token) =>
        {
            var result = await messageSender.GetValue(peer, key, token).ConfigureAwait(false);
            return new GetValueResult { Value = result.found ? result.value : null, Timestamp = result.timestamp };
        };
    }

    public DhtClient(SharedDhtState sharedState, IDhtMessageSender messageSender, IValueStore valueStore,
        IRecordValidator validator, KadDhtOptions options, ILoggerFactory? loggerFactory = null)
    {
        _state = sharedState ?? throw new ArgumentNullException(nameof(sharedState));
        ArgumentNullException.ThrowIfNull(messageSender);
        _values = valueStore ?? throw new ArgumentNullException(nameof(valueStore));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _state.UseValueStore(_values, _options.RecordTtl);
        _logger = loggerFactory?.CreateLogger<DhtClient>();
        _put = messageSender.PutValueAsync;
        _get = messageSender.GetValueAsync;
    }

    public async Task<int> PutValueAsync(string key, string value, CancellationToken token = default)
    {
        using var operation = BeginOperation(token);
        token = operation.Token;
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(value);
        token.ThrowIfCancellationRequested();
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        if (valueBytes.Length > _options.MaxValueSize)
            throw new ArgumentException("Value exceeds MaxValueSize.", nameof(value));
        if (!_validator.Validate(keyBytes, valueBytes)) return 0;
        int local = await _values.PutValueAsync(keyBytes, new StoredValue
        {
            Value = valueBytes,
            Publisher = _state.LocalPeerKey is { } publisher ? new PeerId(publisher.Bytes.ToArray()) : null,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Ttl = _options.RecordTtl
        }, token).ConfigureAwait(false) ? 1 : 0;
        token.ThrowIfCancellationRequested();
        var results = await Task.WhenAll(_state.GetKNearestPeers(new PublicKey(keyBytes)).Select(async peer =>
        {
            try { return await _put(peer, keyBytes, valueBytes, token).ConfigureAwait(false) ? 1 : 0; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger?.LogDebug(ex, "PUT to {PeerId} failed", peer.PeerId); return 0; }
        })).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return local + results.Sum();
    }

    public async Task<(bool found, string? value)> GetValueAsync(string key, CancellationToken token = default)
    {
        using var operation = BeginOperation(token);
        token = operation.Token;
        ArgumentException.ThrowIfNullOrEmpty(key);
        token.ThrowIfCancellationRequested();
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var local = await _values.GetValueAsync(keyBytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (local is not null && Valid(local.Value)) return (true, Encoding.UTF8.GetString(local.Value));
        var results = await Task.WhenAll(_state.GetKNearestPeers(new PublicKey(keyBytes)).Select(async peer =>
        {
            try
            {
                var result = await _get(peer, keyBytes, token).ConfigureAwait(false);
                return result.HasValue && Valid(result.Value!) ? result : null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger?.LogDebug(ex, "GET from {PeerId} failed", peer.PeerId); return null; }
        })).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var candidates = results.OfType<GetValueResult>().ToArray();
        if (candidates.Length == 0) return (false, null);
        int selected = _validator.Select(keyBytes, candidates.Select(result => result.Value!).ToArray());
        if (selected < 0 || selected >= candidates.Length) return (false, null);
        var best = candidates[selected];
        // A valid result can still be returned when the bounded cache refuses admission.
        await _values.PutValueAsync(keyBytes, new StoredValue
        {
            Value = best.Value!,
            Timestamp = best.Timestamp,
            Ttl = _options.RecordTtl
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return (true, Encoding.UTF8.GetString(best.Value!));

        bool Valid(byte[] value) => value.Length > 0 && value.Length <= _options.MaxValueSize && _validator.Validate(keyBytes, value);
    }

    private Operation BeginOperation(CancellationToken token)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
            if (_operations++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Operation(this, linked);
        }
    }

    private sealed class Operation(DhtClient owner, CancellationTokenSource linked) : IDisposable
    {
        public CancellationToken Token => linked.Token;
        public void Dispose()
        {
            linked.Dispose();
            lock (owner._lifecycle)
                if (--owner._operations == 0) owner._drained!.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            Task? drained = _drained?.Task;
            return new(_disposal = Task.Run(async () =>
            {
                try
                {
                    _shutdown.Cancel();
                    if (drained is not null) await drained.ConfigureAwait(false);
                }
                finally { _shutdown.Dispose(); }
            }));
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
