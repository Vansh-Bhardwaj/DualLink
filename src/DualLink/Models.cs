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

    [JsonIgnore] public string RunState => IsRunning ? "RUNNING" : "IDLE";
    [JsonIgnore] public string ProcessSummary => string.Join(" · ", Processes.Select(Path.GetFileNameWithoutExtension));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class LinkInfo : INotifyPropertyChanged
{
    private double _downloadMbps;
    private int _weight = 1;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Address { get; init; }
    public required string Gateway { get; init; }
    public required string Kind { get; init; }
    public long LastReceivedBytes { get; set; }

    public double DownloadMbps
    {
        get => _downloadMbps;
        set { if (Math.Abs(_downloadMbps - value) > 0.05) { _downloadMbps = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedText)); } }
    }

    public int Weight
    {
        get => _weight;
        set { var next = Math.Clamp(value, 0, 10); if (_weight != next) { _weight = next; OnPropertyChanged(); OnPropertyChanged(nameof(WeightText)); OnPropertyChanged(nameof(IsEnabled)); } }
    }

    public string SpeedText => $"{DownloadMbps:0.0} Mbps";
    public string WeightText => Weight == 0 ? "OFF" : $"{Weight}×";
    public bool IsEnabled => Weight > 0;
    public string DisplayName => Name;
    public string DiagnosticName => $"{Name} · {Address}";

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
    public bool CloseToTray { get; set; } = true;
    public List<string> SelectedProfiles { get; set; } = new();
    public List<AppProfile> CustomProfiles { get; set; } = new();
}

public sealed class BoostSessionState
{
    public bool ConfigExisted { get; set; }
    public bool ServiceWasRunning { get; set; }
    public string ConfigPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
}
