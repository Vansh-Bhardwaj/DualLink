using System.Diagnostics;

namespace DualLink;

public sealed class TransferRateLimiter
{
    private readonly object _gate = new();
    private int _megabitsPerSecond;
    private long _nextSlot;

    public int MegabitsPerSecond => Volatile.Read(ref _megabitsPerSecond);

    public void SetLimit(int megabitsPerSecond)
    {
        Volatile.Write(ref _megabitsPerSecond, Math.Max(0, megabitsPerSecond));
        lock (_gate) _nextSlot = Stopwatch.GetTimestamp();
    }

    public async ValueTask ThrottleAsync(int bytes, CancellationToken token)
    {
        var limit = MegabitsPerSecond;
        if (limit <= 0 || bytes <= 0) return;

        long waitTicks;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var slot = Math.Max(now, _nextSlot);
            waitTicks = Math.Max(0, slot - now);
            var duration = bytes * 8d / (limit * 1_000_000d) * Stopwatch.Frequency;
            _nextSlot = slot + Math.Max(1, (long)duration);
        }

        if (waitTicks > 0)
            await Task.Delay(TimeSpan.FromSeconds(waitTicks / (double)Stopwatch.Frequency), token);
    }
}
