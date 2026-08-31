using System.Diagnostics;

namespace DualLink;

public sealed class TransferRateLimiter
{
    private readonly object _gate = new();
    private int _megabitsPerSecond;
    private long _nextSlot;
    // A completed task is cheaper to replace than a CancellationTokenSource and
    // lets callers abandon a stale delay as soon as a live limit changes. This is
    // important for turning a route off while a download is already in flight.
    private TaskCompletionSource _limitChanged = CreateSignal();

    public int MegabitsPerSecond => Volatile.Read(ref _megabitsPerSecond);

    public void SetLimit(int megabitsPerSecond)
    {
        var nextLimit = Math.Max(0, megabitsPerSecond);
        TaskCompletionSource previous;
        lock (_gate)
        {
            Volatile.Write(ref _megabitsPerSecond, nextLimit);
            _nextSlot = Stopwatch.GetTimestamp();
            previous = _limitChanged;
            _limitChanged = CreateSignal();
        }
        previous.TrySetResult();
    }

    public async ValueTask ThrottleAsync(int bytes, CancellationToken token)
    {
        if (bytes <= 0) return;

        while (true)
        {
            var limit = MegabitsPerSecond;
            if (limit <= 0) return;

            long waitTicks;
            Task changed;
            lock (_gate)
            {
                // The limit may have changed between the fast read and the lock.
                // Re-read it so this reservation is made against the current rate.
                limit = _megabitsPerSecond;
                if (limit <= 0) return;
                var now = Stopwatch.GetTimestamp();
                var slot = Math.Max(now, _nextSlot);
                waitTicks = Math.Max(0, slot - now);
                var duration = bytes * 8d / (limit * 1_000_000d) * Stopwatch.Frequency;
                _nextSlot = slot + Math.Max(1, (long)duration);
                changed = _limitChanged.Task;
            }

            if (waitTicks <= 0) return;

            var delay = Task.Delay(TimeSpan.FromSeconds(waitTicks / (double)Stopwatch.Frequency), token);
            var completed = await Task.WhenAny(delay, changed).ConfigureAwait(false);
            if (ReferenceEquals(completed, delay))
            {
                // Awaiting the delay propagates the caller's cancellation token.
                await delay.ConfigureAwait(false);
                return;
            }

            // A new limit reset the schedule. Recalculate instead of carrying the
            // old wait into the new policy.
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
