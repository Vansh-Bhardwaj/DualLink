using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DualLink;

public sealed class AppProfile : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isRunning;

    public required string Name { get; init; }
    public required string Subtitle { get; init; }
    public required string Accent { get; init; }
    public required List<string> Processes { get; init; }
    public List<string> ExecutablePaths { get; init; } = new();
    public bool IsCustom { get; init; }
    public bool IsSystemDetected { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning != value)
            {
                _isRunning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RunState));
            }
        }
    }

    [JsonIgnore] public string RunState => IsRunning ? "Running" : "Idle";
    [JsonIgnore] public string ProcessSummary => string.Join(" · ", Processes.Select(Path.GetFileNameWithoutExtension));
    [JsonIgnore] public IEnumerable<string> ProcessMatchers => ExecutablePaths.Concat(Processes);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class LinkInfo : INotifyPropertyChanged
{
    public const int FullSpeedControlMbps = 500;
    private double _downloadMbps;
    private double _uploadMbps;
    private int _weight = 1;
    private double _routeControlMbps = FullSpeedControlMbps;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Address { get; init; }
    public required string Gateway { get; init; }
    public required string Kind { get; init; }
    public string? NetworkName { get; init; }
    public long LastReceivedBytes { get; set; }
    public long LastSentBytes { get; set; }

    public double DownloadMbps
    {
        get => _downloadMbps;
        set { if (Math.Abs(_downloadMbps - value) > 0.05) { _downloadMbps = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedText)); } }
    }

    public double UploadMbps
    {
        get => _uploadMbps;
        set { if (Math.Abs(_uploadMbps - value) > 0.05) { _uploadMbps = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedText)); } }
    }

    public int Weight
    {
        get => _weight;
        set
        {
            var enabled = value > 0;
            if (!enabled) RouteControlMbps = 0;
            else if (_routeControlMbps <= 0) RouteControlMbps = FullSpeedControlMbps;
        }
    }

    public double RouteControlMbps
    {
        get => _routeControlMbps;
        set
        {
            var next = Math.Clamp(Math.Round(value / 5d) * 5d, 0d, FullSpeedControlMbps);
            if (Math.Abs(_routeControlMbps - next) < 0.1) return;
            _routeControlMbps = next;
            _weight = next <= 0 ? 0 : 1;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RouteControlText));
            OnPropertyChanged(nameof(SpeedLimitMbps));
            OnPropertyChanged(nameof(Weight));
            OnPropertyChanged(nameof(WeightText));
            OnPropertyChanged(nameof(IsEnabled));
        }
    }

    public string SpeedText => $"↓ {DownloadMbps:0.0}  ↑ {UploadMbps:0.0}";
    public string WeightText => Weight == 0 ? "Off" : Weight == 1 ? "1 share" : $"{Weight} shares";
    public int SpeedLimitMbps => RouteControlMbps >= FullSpeedControlMbps ? 0 : (int)RouteControlMbps;
    public string RouteControlText => RouteControlMbps <= 0 ? "Off" : SpeedLimitMbps == 0 ? "Full" : $"{SpeedLimitMbps} Mbps";
    public bool IsEnabled => Weight > 0;
    public string DisplayName => string.IsNullOrWhiteSpace(NetworkName) ? Name : NetworkName;
    public string DetailText => string.IsNullOrWhiteSpace(NetworkName) ? Description : $"{Name} · {Description}";
    public string DiagnosticName => $"{DisplayName} · {Address}";

    public override string ToString() => DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class UserSettings
{
    public bool Armed { get; set; }
    public bool AutoBoost { get; set; }
    public string? EthernetId { get; set; }
    public string? WifiId { get; set; }
    public int EthernetWeight { get; set; } = 2;
    public int WifiWeight { get; set; } = 5;
    public int EthernetSpeedLimitMbps { get; set; }
    public int WifiSpeedLimitMbps { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int BandwidthLimitMbps { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int DownloadLimitMbps { get; set; }
    public RoutingMode RoutingMode { get; set; } = RoutingMode.Smart;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;
    public List<string> SelectedProfiles { get; set; } = new();
    public List<AppProfile> CustomProfiles { get; set; } = new();
}

public enum UpdateChannel
{
    Stable,
    Preview
}

public sealed class UpdateChannelOption
{
    public required UpdateChannel Channel { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public override string ToString() => DisplayName;
}

public enum DiagnosticState
{
    Good,
    Notice,
    Problem
}

public sealed record ConnectionCheckResult(string Title, string Message, DiagnosticState State)
{
    [JsonIgnore]
    public string Accent => State switch
    {
        DiagnosticState.Good => "#66D49A",
        DiagnosticState.Notice => "#F2B84B",
        _ => "#F06C7B"
    };
}

public sealed class BandwidthOption
{
    public required int Mbps { get; init; }
    public required string DisplayName { get; init; }
    public override string ToString() => DisplayName;
}

public sealed record RunningAppInfo(string DisplayName, string ProcessName, string ExecutablePath)
{
    public string Subtitle => $"{ProcessName} · Running now";
}

public sealed class BoostSessionState
{
    public bool ConfigExisted { get; set; }
    public bool ServiceWasRunning { get; set; }
    public string ConfigPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
}
