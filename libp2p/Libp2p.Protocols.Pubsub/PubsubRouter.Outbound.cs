// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Metrics;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Diagnostics.Metrics;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter
{
    private readonly object _outboundLock = new();
    private readonly OutboundLimits _outboundLimits;
    private int _outboundEntries;
    private long _outboundBytes;
    private static readonly Counter<long> OutboundDrops = Libp2pMetrics.Meter.CreateCounter<long>("libp2p.pubsub.outbound.dropped");
    private static readonly Counter<long> OverloadDisconnects = Libp2pMetrics.Meter.CreateCounter<long>("libp2p.pubsub.overload.disconnects");

    private sealed record OutboundLimits
    {
        public int PeerEntries { get; }
        public long PeerBytes { get; }
        public int Entries { get; }
        public long Bytes { get; }
        public int OptionalEntries { get; }
        public long OptionalBytes { get; }
        public TimeSpan Timeout { get; }
        public OutboundLimits(PubsubSettings settings)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.MaxOutboundRpcEntriesPerPeer);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.MaxOutboundRpcBytesPerPeer);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.MaxOutboundRpcEntries);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.MaxOutboundRpcBytes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.OutboundRpcTimeout);
            Timeout = TimeSpan.FromMilliseconds(settings.OutboundRpcTimeout);
            PeerEntries = settings.MaxOutboundRpcEntriesPerPeer;
            PeerBytes = settings.MaxOutboundRpcBytesPerPeer;
            Entries = settings.MaxOutboundRpcEntries;
            Bytes = settings.MaxOutboundRpcBytes;
            // Keep one peer's allocation, capped at half the total (rounded up), for required
            // control. Optional-only stalled peers cannot consume the initial hello or mesh budget.
            OptionalEntries = Entries - (int)Math.Min(PeerEntries, ((long)Entries + 1) / 2);
            OptionalBytes = Bytes - Math.Min(PeerBytes, Bytes / 2 + Bytes % 2);
        }
    }

    /// <summary>Outcome of one <see cref="PubsubPeer.Send"/>. Admission queues work; it never acknowledges delivery.</summary>
    internal enum SendResult
    {
        /// <summary>Every frame of the RPC was queued.</summary>
        Admitted,
        /// <summary>Optional content was not queued. Required control in the same RPC, if any, was queued and the generation stays open.</summary>
        Dropped,
        /// <summary>Required control could not be queued, or the generation was already terminal; its owning session is closing.</summary>
        Closing,
    }
    internal sealed class OutboundFrame(byte[] bytes, bool extensions, IwantPromiseTracker.Promise? promise, long admittedAt, Rpc? pendingControl)
    {
        public byte[] Bytes { get; private set; } = bytes;
        public bool Extensions { get; } = extensions;
        public IwantPromiseTracker.Promise? Promise { get; } = promise;
        public long AdmittedAt { get; } = admittedAt;
        /// <summary>Owned copy of queued required control; later control for other topics merges into it until the writer takes the frame.</summary>
        public Rpc? PendingControl { get; private set; } = pendingControl;
        internal void Replace(byte[] replacement, Rpc pending) { Bytes = replacement; PendingControl = pending; }
        internal void Seal() => PendingControl = null;
    }

    internal sealed partial class PubsubPeer
    {
        private readonly PubsubRouter _router;
        internal readonly Func<Task> Disconnect;
        internal readonly Queue<OutboundFrame> Queue = new();
        internal readonly SemaphoreSlim Wake = new(0, 1);
        internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<IOResult>>? Write;
        internal Func<ValueTask>? Abort;
        internal ITimer DeadlineTimer = null!;
        internal OutboundFrame? ActiveFrame;
        internal OutboundFrame? TailControl; // Last queued frame while it still accepts required control; owned by the outbound lock.
        internal int IncomingStreams; // Owned by the router lock.
        internal int IncomingEntries; // Admitted incoming message-ID RPCs; owned by the router lock.
        internal long IncomingBytes;
        internal int Entries;
        internal long Bytes;
        internal bool ExtensionsQueued;
        internal volatile bool Terminal;
        internal bool DisconnectRequired;
        internal TaskCompletionSource Idle = CompletedSignal();
        internal readonly TaskCompletionSource Stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Teardown = Task.CompletedTask;
        public Task Completion { get; internal set; } = Task.CompletedTask;
        public bool HasWriter => Write is not null;

        public SendResult Send(Rpc rpc) => _router.AdmitOutbound(this, rpc);

        internal bool AttachWriter(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<IOResult>> write, Func<ValueTask> abort)
        {
            lock (_router._outboundLock)
            {
                if (Terminal || Write is not null) return false;
                Write = write;
                Abort = abort;
            }
            Signal();
            return true;
        }

        internal void Signal()
        {
            // One asynchronous waiter per peer; concurrent producers coalesce wake signals.
            lock (Wake)
            {
                if (Wake.CurrentCount == 0) Wake.Release();
            }
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    internal (int Entries, long Bytes) OutboundUsage
    {
        get { lock (_outboundLock) return (_outboundEntries, _outboundBytes); }
    }

    // An observation barrier, also useful to callers inside this assembly that need to drain a test transport.
    internal Task FlushOutboundAsync()
    {
        lock (_outboundLock) return Task.WhenAll(peerState.Values.Where(p => p.HasWriter).Select(p => p.Idle.Task));
    }

    private SendResult AdmitOutbound(PubsubPeer peer, Rpc original)
    {
        SendResult result;
        string? reason, optionalReason = null;
        bool required;
        lock (_outboundLock)
        {
            if (peer.Terminal || _stopped.IsCancellationRequested) return SendResult.Closing;
            if (TryPrepareFrames(peer, original, out Rpc first, out Rpc? optional, out required))
            {
                result = AdmitFrameUnderLock(peer, first, required, out reason);
                // The control frame is queued ahead of the optional frame, which drops independently.
                if (optional is not null && result == SendResult.Admitted)
                {
                    result = AdmitFrameUnderLock(peer, optional, required: false, out optionalReason);
                }
            }
            else
            {
                reason = "serialization";
                if (required) TerminateUnderLock(peer, disconnect: true);
                result = required ? SendResult.Closing : SendResult.Dropped;
            }
        }
        peer.Signal();
        RecordDrop(peer, required, reason);
        RecordDrop(peer, required: false, optionalReason);
        return result;
    }

    private void RecordDrop(PubsubPeer peer, bool required, string? reason)
    {
        if (reason is null) return;
        OutboundDrops.Add(1, new KeyValuePair<string, object?>("reason", reason));
        if (required)
        {
            OverloadDisconnects.Add(1, new KeyValuePair<string, object?>("reason", reason));
            logger?.LogDebug("Closing pubsub generation for {peerId}: {reason}", peer.PeerId, reason);
        }
    }

    // Applies peer-specific transformations without mutating the caller's RPC, then separates required control
    // from optional content so payload can never occupy the headroom reserved for control.
    private static bool TryPrepareFrames(PubsubPeer peer, Rpc original, out Rpc first, out Rpc? optional, out bool required)
    {
        try
        {
            first = peer.AddExtensionsIfNeeded(peer.RemoveUnsupportedPartialSubscriptionOptions(original));
            required = HasRequiredControl(first);
            optional = null;
            if (required && HasOptionalContent(first)) (first, optional) = Split(first);
            return true;
        }
        catch
        {
            // Nothing was reserved; classify the loss by the caller's RPC.
            first = original;
            optional = null;
            required = HasRequiredControl(original) || peer.NeedsExtensions;
            return false;
        }
    }

    private static bool HasRequiredControl(Rpc rpc) =>
        rpc.Subscriptions.Count != 0 ||
        rpc.Control is { } control && (control.Graft.Count != 0 || control.Prune.Count != 0 || control.Extensions is not null);

    private static bool HasOptionalContent(Rpc rpc) =>
        rpc.Publish.Count != 0 || rpc.Partial is not null ||
        rpc.Control is { } control && (control.Ihave.Count != 0 || control.Iwant.Count != 0 || control.Idontwant.Count != 0);

    // The halves reference the caller's message instances only until serialization completes under the lock.
    private static (Rpc Required, Rpc Optional) Split(Rpc rpc)
    {
        Rpc required = new() { Subscriptions = { rpc.Subscriptions } };
        Rpc optional = new() { Publish = { rpc.Publish }, Partial = rpc.Partial };
        if (rpc.Control is { } control)
        {
            if (control.Graft.Count != 0 || control.Prune.Count != 0 || control.Extensions is not null)
            {
                required.Control = new() { Graft = { control.Graft }, Prune = { control.Prune }, Extensions = control.Extensions };
            }
            if (control.Ihave.Count != 0 || control.Iwant.Count != 0 || control.Idontwant.Count != 0)
            {
                optional.Control = new() { Ihave = { control.Ihave }, Iwant = { control.Iwant }, Idontwant = { control.Idontwant } };
            }
        }
        return (required, optional);
    }

    // Required frames use the full budgets and close the generation when they cannot fit; optional frames use
    // the lower optional budgets and drop newest. Both charge the same per-peer limits.
    private SendResult AdmitFrameUnderLock(PubsubPeer peer, Rpc rpc, bool required, out string? dropReason)
    {
        // A burst of required control to one peer, such as subscribing to many topics while its writer is busy
        // or before its reply stream attaches, must not consume one entry per RPC and close a healthy generation.
        if (required && peer.TailControl is { PendingControl: { } pending } tail && TryMergeControl(pending, rpc, out Rpc merged))
        {
            return ReplacePendingControlUnderLock(peer, tail, merged, out dropReason);
        }
        int reserved = 0;
        IwantPromiseTracker.Promise? promise = null;
        try
        {
            bool extensions = !peer.ExtensionsQueued && rpc.Control?.Extensions is not null;
            long length = rpc.CalculateSize();
            long size = length < 0 ? (long)int.MaxValue + 1 : length + VarInt.GetSizeInBytes((ulong)length);
            int entryLimit = required ? _outboundLimits.Entries : _outboundLimits.OptionalEntries;
            long byteLimit = required ? _outboundLimits.Bytes : _outboundLimits.OptionalBytes;
            if (size > int.MaxValue || peer.Entries >= _outboundLimits.PeerEntries || _outboundEntries >= entryLimit ||
                size > _outboundLimits.PeerBytes - peer.Bytes || size > byteLimit - _outboundBytes)
            {
                dropReason = "capacity";
            }
            else
            {
                // Reserve before allocation. The queue owns bytes, never the caller's mutable RPC.
                long admittedAt = _timeProvider.GetTimestamp();
                reserved = (int)size;
                peer.Entries++;
                peer.Bytes += size;
                _outboundEntries++;
                _outboundBytes += size;
                if (peer.Idle.Task.IsCompleted) peer.Idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
                byte[] bytes = new byte[reserved];
                int offset = 0;
                VarInt.Encode((int)length, bytes, ref offset);
                rpc.WriteTo(bytes.AsSpan(offset));
                promise = _iwantPromises.Reserve(peer.PeerId, peer, SamplePromiseId(rpc));
                // Required frames keep an owned copy so later control can merge without reading the caller's RPC again.
                OutboundFrame frame = new(bytes, extensions, promise, admittedAt, required ? rpc.Clone() : null);
                peer.Queue.Enqueue(frame);
                peer.TailControl = required ? frame : null;
                peer.ExtensionsQueued |= extensions;
                if (peer.Entries == 1) RefreshDeadlineUnderLock(peer);
                dropReason = null;
                return SendResult.Admitted;
            }
        }
        catch
        {
            _iwantPromises.Cancel(promise);
            if (reserved != 0) ReleaseUnderLock(peer, reserved);
            dropReason = "serialization";
        }
        if (required) TerminateUnderLock(peer, disconnect: true);
        return required ? SendResult.Closing : SendResult.Dropped;
    }

    // Re-serializes the pending control frame with the merged RPC. Entries are unchanged, the byte delta is charged
    // like a new frame, and the frame keeps its original admission time, so merging never extends a deadline.
    private SendResult ReplacePendingControlUnderLock(PubsubPeer peer, OutboundFrame tail, Rpc merged, out string? dropReason)
    {
        try
        {
            long length = merged.CalculateSize();
            long size = length < 0 ? (long)int.MaxValue + 1 : length + VarInt.GetSizeInBytes((ulong)length);
            long delta = size - tail.Bytes.Length;
            if (size > int.MaxValue || delta > _outboundLimits.PeerBytes - peer.Bytes || delta > _outboundLimits.Bytes - _outboundBytes)
            {
                dropReason = "capacity";
            }
            else
            {
                byte[] bytes = new byte[(int)size];
                int offset = 0;
                VarInt.Encode((int)length, bytes, ref offset);
                merged.WriteTo(bytes.AsSpan(offset));
                tail.Replace(bytes, merged);
                peer.Bytes += delta;
                _outboundBytes += delta;
                dropReason = null;
                return SendResult.Admitted;
            }
        }
        catch
        {
            dropReason = "serialization";
        }
        TerminateUnderLock(peer, disconnect: true);
        return SendResult.Closing;
    }

    // Merges required control for distinct topics into one pending frame. Control for a topic the pending frame
    // already mentions keeps its own frame, so receivers observe the same per-topic order; a repeated GRAFT is
    // redundant and a repeated PRUNE is superseded by the newer one. The result owns copies of both inputs.
    private static bool TryMergeControl(Rpc pending, Rpc rpc, out Rpc merged)
    {
        merged = null!;
        if (rpc.Control?.Extensions is not null) return false;
        HashSet<string> subscribed = new(pending.Subscriptions.Select(s => s.Topicid ?? string.Empty), StringComparer.Ordinal);
        HashSet<string> grafted = new((pending.Control?.Graft ?? []).Select(g => g.TopicID ?? string.Empty), StringComparer.Ordinal);
        HashSet<string> pruned = new((pending.Control?.Prune ?? []).Select(g => g.TopicID ?? string.Empty), StringComparer.Ordinal);
        foreach (Rpc.Types.SubOpts subscription in rpc.Subscriptions)
        {
            string topic = subscription.Topicid ?? string.Empty;
            if (subscribed.Contains(topic) || grafted.Contains(topic) || pruned.Contains(topic)) return false;
        }
        foreach (ControlGraft graft in rpc.Control?.Graft ?? [])
        {
            string topic = graft.TopicID ?? string.Empty;
            if (subscribed.Contains(topic) || pruned.Contains(topic)) return false;
        }
        foreach (ControlPrune prune in rpc.Control?.Prune ?? [])
        {
            string topic = prune.TopicID ?? string.Empty;
            if (subscribed.Contains(topic) || grafted.Contains(topic)) return false;
        }
        Rpc incoming = rpc.Clone();
        merged = pending.Clone();
        merged.Subscriptions.Add(incoming.Subscriptions);
        if (incoming.Control is { } control && (control.Graft.Count != 0 || control.Prune.Count != 0))
        {
            merged.Control ??= new ControlMessage();
            foreach (ControlGraft graft in control.Graft)
            {
                if (grafted.Add(graft.TopicID ?? string.Empty)) merged.Control.Graft.Add(graft);
            }
            foreach (ControlPrune prune in control.Prune)
            {
                int existing = merged.Control.Prune.ToList().FindIndex(p => (p.TopicID ?? string.Empty) == (prune.TopicID ?? string.Empty));
                if (existing >= 0) merged.Control.Prune[existing] = prune;
                else merged.Control.Prune.Add(prune);
            }
        }
        return true;
    }

    private static IEnumerable<MessageId> SamplePromiseId(Rpc rpc)
    {
        ByteString? selected = null;
        int count = 0;
        foreach (ControlIWant want in rpc.Control?.Iwant ?? [])
        {
            foreach (ByteString id in want.MessageIDs)
            {
                if (Random.Shared.Next(++count) == 0) selected = id;
            }
        }
        return selected is null ? [] : [new MessageId(selected.ToByteArray())];
    }

    // All terminal transitions and promise deadlines serialize with heartbeat's expiry/penalty step.
    private void TerminateUnderLock(PubsubPeer peer, bool disconnect)
    {
        peer.DisconnectRequired |= disconnect;
        if (!peer.Terminal) peer.DeadlineTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        peer.Terminal = true;
        peer.Stop.TrySetResult();
        _iwantPromises.ClearOwner(peer);
        peer.TailControl = null;
        while (peer.Queue.TryDequeue(out OutboundFrame? frame)) ReleaseUnderLock(peer, frame.Bytes.Length);
    }

    private void ReleaseUnderLock(PubsubPeer peer, int size)
    {
        peer.Entries--;
        peer.Bytes -= size;
        _outboundEntries--;
        _outboundBytes -= size;
        if (peer.Entries == 0) peer.Idle.TrySetResult();
        if (!peer.Terminal) RefreshDeadlineUnderLock(peer);
    }

    private static OutboundFrame? OldestFrameUnderLock(PubsubPeer peer) =>
        peer.ActiveFrame ?? (peer.Queue.TryPeek(out OutboundFrame? frame) ? frame : null);

    private void RefreshDeadlineUnderLock(PubsubPeer peer)
    {
        OutboundFrame? oldest = OldestFrameUnderLock(peer);
        TimeSpan remaining = oldest is null ? Timeout.InfiniteTimeSpan
            : _outboundLimits.Timeout - _timeProvider.GetElapsedTime(oldest.AdmittedAt);
        peer.DeadlineTimer.Change(oldest is not null && remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining, Timeout.InfiniteTimeSpan);
    }

    private void ExpireOutbound(PubsubPeer peer)
    {
        lock (_outboundLock)
        {
            if (peer.Terminal || OldestFrameUnderLock(peer) is not { } oldest) return;
            // A callback may already have been scheduled when the previous frame completed.
            if (_timeProvider.GetElapsedTime(oldest.AdmittedAt) < _outboundLimits.Timeout)
            {
                RefreshDeadlineUnderLock(peer);
                return;
            }
            TerminateUnderLock(peer, disconnect: true);
        }
        peer.Signal();
        OverloadDisconnects.Add(1, new KeyValuePair<string, object?>("reason", "timeout"));
        logger?.LogDebug("Closing stalled pubsub generation for {peerId}", peer.PeerId);
    }

    private void StopPeer(PubsubPeer peer)
    {
        lock (_outboundLock) TerminateUnderLock(peer, disconnect: false);
        peer.Signal();
    }

    private async Task RunWriterAsync(PubsubPeer peer)
    {
        try
        {
            while (true)
            {
                await peer.Wake.WaitAsync().ConfigureAwait(false);
                while (true)
                {
                    OutboundFrame? frame;
                    lock (_outboundLock)
                    {
                        if (peer.Terminal) return;
                        if (peer.Write is null || !peer.Queue.TryDequeue(out frame)) break;
                        // Once the writer owns the frame its bytes are final; later control starts a new frame.
                        if (ReferenceEquals(peer.TailControl, frame)) peer.TailControl = null;
                        frame.Seal();
                        peer.ActiveFrame = frame;
                    }
                    try
                    {
                        // Timer callbacks may be delayed; never start or accept a write past its deadline.
                        if (_timeProvider.GetElapsedTime(frame.AdmittedAt) >= _outboundLimits.Timeout)
                        {
                            ExpireOutbound(peer);
                            return;
                        }
                        IOResult result = await peer.Write(frame.Bytes, peer.TokenSource.Token).ConfigureAwait(false);
                        if (_timeProvider.GetElapsedTime(frame.AdmittedAt) >= _outboundLimits.Timeout) ExpireOutbound(peer);
                        DateTime deadline = result == IOResult.Ok
                            ? _timeProvider.GetUtcNow().UtcDateTime.AddMilliseconds(_settings.IWantFollowupTime) : default;
                        lock (_outboundLock)
                        {
                            if (result == IOResult.Ok && !peer.Terminal)
                            {
                                peer.ExtensionsSent |= frame.Extensions;
                                _iwantPromises.Arm(frame.Promise, deadline);
                            }
                            else TerminateUnderLock(peer, disconnect: !peer.Terminal);
                        }
                    }
                    catch (OperationCanceledException) when (peer.TokenSource.IsCancellationRequested)
                    {
                        lock (_outboundLock) TerminateUnderLock(peer, disconnect: false);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Pubsub write failed");
                        lock (_outboundLock) TerminateUnderLock(peer, disconnect: !peer.Terminal);
                    }
                    finally
                    {
                        // The write has terminated, including any partly consumed chunk.
                        int size = frame.Bytes.Length;
                        frame = null;
                        lock (_outboundLock)
                        {
                            peer.ActiveFrame = null;
                            ReleaseUnderLock(peer, size);
                        }
                    }
                    if (peer.Terminal) return;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Pubsub writer failed");
            lock (_outboundLock) TerminateUnderLock(peer, disconnect: true);
        }
        finally
        {
            StopPeer(peer);
            await peer.Teardown.ConfigureAwait(false);
        }
    }

    private async Task TeardownPeerAsync(PubsubPeer peer)
    {
        await peer.Stop.Task.ConfigureAwait(false);
        await peer.DeadlineTimer.DisposeAsync().ConfigureAwait(false);
        // Remove the generation and register its reconnection before anything observing its token can run.
        RemovePeer(peer.PeerId, peer.Address, peer);
        // Abort independently of the writer: cancellation cannot retract a partly consumed chunk.
        try { peer.TokenSource.Cancel(); }
        catch (Exception ex) { logger?.LogWarning(ex, "Pubsub peer cancellation failed"); }
        if (peer.Abort is not null)
        {
            try { await peer.Abort().ConfigureAwait(false); }
            catch (Exception ex) { logger?.LogWarning(ex, "Pubsub stream abort failed"); }
        }
        if (peer.DisconnectRequired)
        {
            try { await peer.Disconnect().ConfigureAwait(false); }
            catch (Exception ex) { logger?.LogWarning(ex, "Pubsub session disconnect failed"); }
        }
    }
}
