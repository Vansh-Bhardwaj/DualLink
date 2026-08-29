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

public readonly record struct RouteDefinition(string Address, int Weight, bool IsPrimary = false, string? Name = null);

public readonly record struct RouteStatus(
    string Address,
    string Name,
    int Weight,
    int ActiveConnections,
    int ConsecutiveFailures,
    DateTime UnhealthyUntilUtc)
{
    public bool IsHealthy => DateTime.UtcNow >= UnhealthyUntilUtc;
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
