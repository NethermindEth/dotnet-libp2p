// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core;

internal sealed class SessionRecovery(ILocalPeer peer, SessionRecoveryOptions options, TimeProvider clock)
    : ISessionRecovery, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<PeerId, RecoveryRun> _runs = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _peerShutdown = (peer as LocalPeer)?.ShutdownToken ?? default;
    private Task? _disposal;

    private bool IsStopping => _lifetime.IsCancellationRequested || peer is LocalPeer { IsDisposing: true };

    public async IAsyncEnumerable<SessionRecoveryUpdate> WatchAsync(PeerId remotePeerId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remotePeerId);

        RecoveryRun? run;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run = Subscribe(remotePeerId, out Task? draining);
            if (draining is null) break;
            await draining.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (run is null)
        {
            yield return PeerStopped();
            yield break;
        }

        using Subscription interest = new(this, run);
        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((Subscription)state!).Dispose(), interest);

        SessionRecoveryUpdate? observed = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (SessionRecoveryUpdate latest, Task changed) = Read(run);
            if (ReferenceEquals(latest, observed))
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            yield return latest;
            if (latest.IsTerminal) yield break;
            observed = latest;
        }
    }

    private RecoveryRun? Subscribe(PeerId peerId, out Task? draining)
    {
        draining = null;
        lock (_gate)
        {
            if (IsStopping) return null;
            if (_runs.TryGetValue(peerId, out RecoveryRun? existing))
            {
                if (existing.Subscribers == 0 || existing.Latest.IsTerminal)
                {
                    draining = existing.Completion;
                    return null;
                }
                existing.Subscribers++;
                return existing;
            }

            RecoveryRun run = new(peerId, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _peerShutdown));
            _runs.Add(peerId, run);
            run.Completion = Task.Run(() => RunAsync(run));
            return run;
        }
    }

    private (SessionRecoveryUpdate, Task) Read(RecoveryRun run)
    {
        lock (_gate) return (run.Latest, run.Changed.Task);
    }

    private void Publish(RecoveryRun run, SessionRecoveryUpdate update)
    {
        lock (_gate)
        {
            run.Latest = update;
            TaskCompletionSource changed = run.Changed;
            run.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }

    private void Release(RecoveryRun run)
    {
        lock (_gate)
        {
            if (--run.Subscribers == 0 && !run.Latest.IsTerminal)
            {
                // Claim cancellation synchronously and let callbacks run outside the lock.
                run.Canceling = run.Cancellation.CancelAsync();
            }
        }
    }

    private async Task RunAsync(RecoveryRun run)
    {
        SessionRecoveryUpdate update;
        try
        {
            SessionCloseReason reason = SessionCloseReason.Unknown;
            while (true)
            {
                update = await ConnectAsync(run, reason).ConfigureAwait(false);
                if (update.IsTerminal) break;

                run.Token.ThrowIfCancellationRequested();
                Publish(run, update);
                ISession session = update.Session!;
                await WaitForClosureAsync(session.Closed, run.Token).ConfigureAwait(false);
                run.Token.ThrowIfCancellationRequested();
                reason = session.CloseReason;
                if (reason != SessionCloseReason.ConnectionLost)
                {
                    update = new(SessionRecoveryState.Stopped, CloseReason: reason);
                    break;
                }
            }
        }
        catch (Exception) when (IsStopping || run.Token.IsCancellationRequested)
        {
            update = IsStopping ? PeerStopped() : new(SessionRecoveryState.Stopped);
        }
        catch (Exception failure)
        {
            update = new(SessionRecoveryState.Faulted, Error: failure);
        }

        Publish(run, update);
        if (run.Canceling is { } cancellation) await cancellation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        run.Cancellation.Dispose();

        // Keep the retiring run registered until its last dial or address lookup has returned.
        lock (_gate) _runs.Remove(run.PeerId);
    }

    private async Task<SessionRecoveryUpdate> ConnectAsync(RecoveryRun run, SessionCloseReason reason)
    {
        bool recovering = reason == SessionCloseReason.ConnectionLost;
        Exception? failure = null;
        for (int attempt = 1; ; attempt++)
        {
            run.Token.ThrowIfCancellationRequested();
            if (recovering || attempt > 1)
            {
                int retry = recovering ? attempt : attempt - 1;
                Task delay = Task.Delay(options.GetRetryDelay(retry), clock, run.Token);
                Publish(run, new(SessionRecoveryState.Retrying, Attempt: attempt, CloseReason: reason, Error: failure));
                await delay.ConfigureAwait(false);
            }

            run.Token.ThrowIfCancellationRequested();
            (ISession? session, failure) = await TryDialAsync(run.PeerId, run.Token).ConfigureAwait(false);
            run.Token.ThrowIfCancellationRequested();
            if (failure is null)
            {
                if (session is null || !session.IsInitialized || !session.Closed.CanBeCanceled || session.RemotePeerId != run.PeerId)
                    throw new NotSupportedException("Session recovery requires an initialized session for the requested peer with a cancelable Closed token.");

                if (!session.Closed.IsCancellationRequested)
                    return new(SessionRecoveryState.Ready, session, attempt);

                reason = session.CloseReason;
                if (reason != SessionCloseReason.ConnectionLost)
                    return new(SessionRecoveryState.Stopped, CloseReason: reason);

                failure = new Libp2pException("The session closed before recovery observed readiness.");
            }

            if (attempt == options.MaxAttempts)
                return new(SessionRecoveryState.Exhausted, Attempt: attempt, CloseReason: reason, Error: failure);
        }
    }

    private async Task<(ISession? Session, Exception? Failure)> TryDialAsync(PeerId peerId, CancellationToken token)
    {
        Task<ISession>? dial = null;
        Multiaddress[]? addresses = null;
        if (options.SelectAddresses is { } selectAddresses)
        {
            dial = (peer as LocalPeer)?.TryReuseDial(peerId, token);
            if (dial is null)
            {
                addresses = await selectAddresses(peerId, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (addresses is null || addresses.Any(address => address.GetPeerId() != peerId))
                    throw new Libp2pException($"Recovery addresses must all name peer {peerId}.");
                if (addresses.Length == 0)
                    return (null, new Libp2pException($"No recovery addresses were selected for peer {peerId}."));
            }
        }

        try
        {
            dial ??= addresses is null ? peer.DialAsync(peerId, token) : peer.DialAsync(addresses, token);
            return (await dial.ConfigureAwait(false), null);
        }
        catch (Exception error) when (!IsStopping && !token.IsCancellationRequested)
        {
            return (null, error);
        }
    }

    private static async Task WaitForClosureAsync(CancellationToken closed, CancellationToken token)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = closed.UnsafeRegister(static state => ((TaskCompletionSource)state!).TrySetResult(), completion);
        await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= DisposeRunsAsync([.. _runs.Values.Select(run => run.Completion)]));
    }

    private async Task DisposeRunsAsync(Task[] runs)
    {
        try { await Task.WhenAll(runs.Append(_lifetime.CancelAsync())).ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }

    private static SessionRecoveryUpdate PeerStopped() => new(SessionRecoveryState.Stopped, CloseReason: SessionCloseReason.PeerShutdown);

    private sealed class RecoveryRun(PeerId peerId, CancellationTokenSource cancellation)
    {
        public PeerId PeerId { get; } = peerId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken Token { get; } = cancellation.Token;
        public int Subscribers { get; set; } = 1;
        public Task Completion { get; set; } = Task.CompletedTask;
        public Task? Canceling { get; set; }
        public SessionRecoveryUpdate Latest { get; set; } = new(SessionRecoveryState.Connecting, Attempt: 1);
        public TaskCompletionSource Changed { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Subscription(SessionRecovery owner, RecoveryRun run) : IDisposable
    {
        private SessionRecovery? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(run);
    }
}
