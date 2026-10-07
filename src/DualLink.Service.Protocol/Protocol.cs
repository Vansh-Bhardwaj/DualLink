using System.Text.Json;
using System.Text.Json.Serialization;

namespace DualLink.Service.Protocol;

/// <summary>
/// Contract shared by the unelevated UI and the local privileged helper.
/// Keep this assembly free of Windows, WPF, and networking implementation types.
/// </summary>
public static class DualLinkServiceProtocol
{
    public const int CurrentVersion = 2;
    public const string PipePrefix = "DualLink.Service.";
    public const int MaximumFrameBytes = 256 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    public static string CreatePipeName() => $"{PipePrefix}{Guid.NewGuid():N}";

    public static bool IsValidPipeName(string? pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 180) return false;
        if (!pipeName.StartsWith(PipePrefix, StringComparison.Ordinal)) return false;
        return pipeName.All(static c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    }
}

public sealed record ServiceRequest(
    string Id,
    int ProtocolVersion,
    string Command,
    JsonElement? Payload = null);

public sealed record ServiceResponse(
    string Id,
    int ProtocolVersion,
    bool Success,
    string? Error = null,
    JsonElement? Payload = null);

public sealed record HelloRequest(int ProtocolVersion, string ClientName);

public sealed record HelloResponse(
    int ProtocolVersion,
    int ProcessId,
    string ServiceName,
    IReadOnlyList<string> Capabilities);

public sealed record StartSessionRequest(
    IReadOnlyList<RouteDefinition> Routes,
    RoutingMode Mode,
    IReadOnlyList<string> ProcessMatchers,
    int SocksPort,
    string Username,
    string Password);

public sealed record UpdateTargetsRequest(
    IReadOnlyList<string> ProcessMatchers,
    int SocksPort,
    string Username,
    string Password);

public sealed record UpdateRoutesRequest(
    IReadOnlyList<RouteDefinition> Routes,
    RoutingMode Mode);

public sealed record RouteDefinition(
    string Address,
    int Weight,
    bool IsPrimary,
    string? Name,
    int SpeedLimitMbps);

public sealed record SessionStatus(
    bool IsRunning,
    bool FilterRunning,
    int BoundPort,
    int ActiveConnections,
    RoutingMode Mode,
    IReadOnlyList<RouteStatus> Routes,
    bool CompatibilityGuardActive,
    bool CompatibilityGuardWarmingUp,
    int RememberedDestinations,
    string? Failure = null,
    DateTimeOffset? SampledAtUtc = null,
    bool WatchdogRunning = false,
    IReadOnlyList<string>? ProcessMatchers = null);

public readonly record struct RouteStatus(
    string Address,
    string Name,
    int SpeedLimitMbps,
    bool AcceptingNewConnections,
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

public enum RoutingMode
{
    Smart,
    Balanced,
    Failover
}
