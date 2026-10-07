using DualLink.Service.Protocol;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DualLink.Tests")]
[assembly: InternalsVisibleTo("DualLink")]

namespace DualLink;

public enum RecoveryAction { None, Wait, Restart, Restore }

/// <summary>Reconciles controller intent with the helper, including a bounded filter recovery window.</summary>
public sealed class SessionRecovery(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset? _filterLostAt;

    public RecoveryAction Evaluate(SessionStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.Failure)) return RecoveryAction.Restore;
        if (!status.IsRunning)
        {
            _filterLostAt = null;
            return status.FilterRunning ? RecoveryAction.Restore : RecoveryAction.Restart;
        }
        if (status.FilterRunning)
        {
            _filterLostAt = null;
            return RecoveryAction.None;
        }
        _filterLostAt ??= _clock.GetUtcNow();
        return _clock.GetUtcNow() - _filterLostAt >= TimeSpan.FromSeconds(15)
            ? RecoveryAction.Restore : RecoveryAction.Wait;
    }

    public void Reset() => _filterLostAt = null;
}

public sealed class BoostSessionState
{
    public bool ConfigExisted { get; set; }
    public bool ServiceWasRunning { get; set; }
    public string ConfigPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string? ConfigSecuritySddl { get; set; }
}
