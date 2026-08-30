using System.Net;
using System.Security.Cryptography;

namespace DualLink;

public enum RoutingMode
{
    Smart,
    Balanced,
    Failover
}

public sealed class RoutingModeOption
{
    public required RoutingMode Mode { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public override string ToString() => DisplayName;
}

public readonly record struct RouteDefinition(string Address, int Weight, bool IsPrimary = false, string? Name = null, int SpeedLimitMbps = 0);

public readonly record struct RouteStatus(
    string Address,
    string Name,
    int Weight,
    bool AcceptingNewConnections,
    int SpeedLimitMbps,
    int ActiveConnections,
    int ConsecutiveFailures,
    DateTime UnhealthyUntilUtc,
    double? ConnectLatencyMs,
    DateTime? LastSuccessUtc,
    double Reliability,
    long DownloadedBytes,
    long UploadedBytes,
    long SuccessfulConnections)
{
    public bool IsHealthy => DateTime.UtcNow >= UnhealthyUntilUtc;
    public int ReliabilityPercent => (int)Math.Round(Math.Clamp(Reliability, 0d, 1d) * 100d);
    public string QualityLabel => !IsHealthy ? "Unavailable" : ConnectLatencyMs switch
    {
        _ when Reliability < 0.85d => "Unstable",
        null => "Ready",
        <= 40 => "Excellent",
        <= 90 => "Good",
        <= 180 => "Fair",
        _ => "Slow"
    };
}

public sealed record ProxyCredentials(string Username, string Password)
{
    public static ProxyCredentials Create()
    {
        return new ProxyCredentials(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
    }
}
