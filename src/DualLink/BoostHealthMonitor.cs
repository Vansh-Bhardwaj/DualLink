namespace DualLink;

public sealed class BoostHealthMonitor
{
    private readonly Func<bool> _isBalancerRunning;
    private readonly Func<Task<bool>> _isFilterRunning;
    private readonly Func<Task> _restartFilter;

    public BoostHealthMonitor(Func<bool> isBalancerRunning, Func<Task<bool>> isFilterRunning, Func<Task> restartFilter)
    {
        _isBalancerRunning = isBalancerRunning;
        _isFilterRunning = isFilterRunning;
        _restartFilter = restartFilter;
    }

    public async Task<bool> CheckAndRecoverAsync()
    {
        if (!_isBalancerRunning())
            throw new InvalidOperationException("Local balancer stopped unexpectedly.");
        if (await _isFilterRunning()) return false;

        await _restartFilter();
        if (!await _isFilterRunning())
            throw new InvalidOperationException("Application filter did not remain running after restart.");
        return true;
    }
}
