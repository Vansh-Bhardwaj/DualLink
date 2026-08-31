namespace DualLink;

public sealed class BoostHealthMonitor
{
    private readonly Func<bool> _isBalancerRunning;
    private readonly Func<Task<bool>> _isFilterRunning;
    private readonly Func<Task> _restartFilter;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);

    public BoostHealthMonitor(Func<bool> isBalancerRunning, Func<Task<bool>> isFilterRunning, Func<Task> restartFilter)
    {
        _isBalancerRunning = isBalancerRunning;
        _isFilterRunning = isFilterRunning;
        _restartFilter = restartFilter;
    }

    public async Task<bool> CheckAndRecoverAsync()
    {
        await _recoveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isBalancerRunning())
                throw new InvalidOperationException("Local balancer stopped unexpectedly.");
            if (await _isFilterRunning().ConfigureAwait(false)) return false;

            await _restartFilter().ConfigureAwait(false);
            if (!await _isFilterRunning().ConfigureAwait(false))
                throw new InvalidOperationException("Application filter did not remain running after restart.");
            return true;
        }
        finally { _recoveryGate.Release(); }
    }
}
