// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Threading.Tasks.Sources;

namespace Nethermind.Channels;

// A binary semaphore whose pending waits reuse their completion state. Cancellation
// registrations belong to the wait, so callers do not need linked token sources.
internal sealed class AsyncSemaphore(bool available)
{
    private readonly Lock _lock = new();
    private bool _available = available;
    private Waiter? _first;
    private Waiter? _last;
    private Waiter? _cached;
    private int _cachedCount;

    public bool TryWait()
    {
        lock (_lock)
        {
            if (!_available) return false;
            _available = false;
            return true;
        }
    }

    public ValueTask WaitAsync(CancellationToken token, CancellationToken closed = default)
    {
        lock (_lock)
        {
            if (token.IsCancellationRequested) return ValueTask.FromCanceled(token);
            if (closed.IsCancellationRequested) return ValueTask.FromCanceled(closed);
            if (_available)
            {
                _available = false;
                return ValueTask.CompletedTask;
            }

            Waiter waiter;
            if (_cached is null) waiter = new(this);
            else
            {
                waiter = _cached;
                _cached = waiter.Next;
                _cachedCount--;
            }
            waiter.Next = null;
            waiter.Previous = _last;
            waiter.Queued = true;
            if (_last is null) _first = waiter;
            else _last.Next = waiter;
            _last = waiter;

            // Register under the same lock as completion. A synchronous cancellation
            // may unlink this waiter, but no consumer can see it until both registrations
            // have been stored and this method returns.
            waiter.Register(token, closed);
            return waiter.Task;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_first is not null)
            {
                Waiter waiter = _first;
                Remove(waiter);
                waiter.Complete();
            }
            else
            {
                if (_available) throw new SemaphoreFullException();
                _available = true;
            }
        }
    }

    private void Remove(Waiter waiter)
    {
        if (waiter.Previous is null) _first = waiter.Next;
        else waiter.Previous.Next = waiter.Next;
        if (waiter.Next is null) _last = waiter.Previous;
        else waiter.Next.Previous = waiter.Previous;
        waiter.Previous = waiter.Next = null;
        waiter.Queued = false;
    }

    private void Cancel(Waiter waiter, CancellationToken token)
    {
        lock (_lock)
        {
            if (!waiter.Queued) return;
            Remove(waiter);
            waiter.Cancel(token);
        }
    }

    private void Return(Waiter waiter)
    {
        lock (_lock)
        {
            waiter.Reset();
            // Bound retained state even if many callers queue on a read/write gate.
            if (_cachedCount == 2) return;
            waiter.Next = _cached;
            _cached = waiter;
            _cachedCount++;
        }
    }

    private sealed class Waiter : IValueTaskSource
    {
        private readonly AsyncSemaphore _semaphore;
        private ManualResetValueTaskSourceCore<bool> _completion = new() { RunContinuationsAsynchronously = true };
        private CancellationTokenRegistration _cancellation;
        private CancellationTokenRegistration _closed;
        public Waiter? Previous;
        public Waiter? Next;
        public bool Queued;

        public Waiter(AsyncSemaphore semaphore) => _semaphore = semaphore;
        public ValueTask Task => new(this, _completion.Version);

        public void Register(CancellationToken token, CancellationToken closed)
        {
            _cancellation = token.UnsafeRegister(static (state, cancelled) =>
            {
                Waiter waiter = (Waiter)state!;
                waiter._semaphore.Cancel(waiter, cancelled);
            }, this);
            if (closed != token)
            {
                _closed = closed.UnsafeRegister(static (state, cancelled) =>
                {
                    Waiter waiter = (Waiter)state!;
                    waiter._semaphore.Cancel(waiter, cancelled);
                }, this);
            }
        }

        public void Complete() => _completion.SetResult(true);
        public void Cancel(CancellationToken token) => _completion.SetException(new OperationCanceledException(token));

        public void Reset()
        {
            _cancellation = _closed = default;
            _completion.Reset();
        }

        public void GetResult(short token)
        {
            try
            {
                _completion.GetResult(token);
            }
            finally
            {
                // Dispose outside the semaphore lock: a racing callback may need it.
                // Both callbacks must finish before this identity can serve another wait.
                _cancellation.Dispose();
                _closed.Dispose();
                _semaphore.Return(this);
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _completion.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _completion.OnCompleted(continuation, state, token, flags);
    }
}
