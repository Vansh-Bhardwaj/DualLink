using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace DualLink;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly ProxiFyreManager _proxiFyre;
    private readonly Socks5Balancer _balancer;
    private readonly ProxyCredentials _proxyCredentials;
    private readonly BoostHealthMonitor _healthMonitor;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _networkDebounceTimer;
    private readonly bool _previewMode;
    private readonly SemaphoreSlim _controllerGate = new(1, 1);
    private TrayManager? _tray;
    private UserSettings _settings = new();
    private LinkInfo? _selectedEthernet;
    private LinkInfo? _selectedWifi;
    private RoutingModeOption? _selectedRoutingModeOption;
    private UpdateChannelOption? _selectedUpdateChannelOption;
    private bool _autoBoost = true;
    private bool _armed;
    private bool _boosting;
    private DateTime _nextHealthCheckUtc = DateTime.MinValue;
    private DateTime _nextStartAttemptUtc = DateTime.MinValue;
    private int _startFailureCount;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _closeToTray = true;
    private bool _loadingSettings;
    private string _statusText = "Ready";
    private Brush _statusColor = new SolidColorBrush(Color.FromRgb(140, 150, 165));
    private string _prerequisiteText = "Checking";
    private DateTime _lastRateUpdateUtc = DateTime.UtcNow;
    private DateTime _nextProcessScanUtc = DateTime.MinValue;
    private CancellationTokenSource? _diagnosticsCts;
    private CancellationTokenSource? _wifiConnectCts;
    private CancellationTokenSource? _updateCts;
    private string _diagnosticsSummaryText = "Run a check when something feels wrong.";
    private string _updateStatusText = "Updates are checked only when you ask.";
    private string _wifiNetworksStatusText = "Open to scan nearby networks.";
    private UpdateCheckResult? _availableUpdate;
    private readonly Queue<TrafficSample> _trafficHistory = new();
    private readonly Dictionary<string, RouteTrafficBaseline> _routeTrafficBaselines = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(bool previewMode = false)
    {
        InitializeComponent();
        DataContext = this;
        _previewMode = previewMode;

        _settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink");
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        Directory.CreateDirectory(_settingsDirectory);
        _proxiFyre = new ProxiFyreManager(Log);
        _proxyCredentials = ProxyCredentials.Create();
        _balancer = new Socks5Balancer(0, Log, _proxyCredentials);
        _healthMonitor = new BoostHealthMonitor(
            () => _balancer.IsRunning,
            _proxiFyre.IsServiceRunningAsync,
            _proxiFyre.EnsureServiceRunningAsync);

        LoadProfilesAndSettings();
        if (_previewMode) LoadPreviewAdapters();
        else RefreshAdapters();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _networkDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _networkDebounceTimer.Tick += NetworkDebounce_Tick;
        if (!_previewMode)
        {
            _tray = new TrayManager(
                () => Dispatcher.BeginInvoke(ShowFromTray),
                () => Dispatcher.BeginInvoke(async () => await ToggleArmedAsync()),
                () => Dispatcher.BeginInvoke(ExitFromTray));
            UpdateButton();
            StartWatchdog();
            _timer.Start();
            NetworkChange.NetworkAddressChanged += NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
            SystemEvents.PowerModeChanged += PowerModeChanged;
            Loaded += async (_, _) => await RefreshPrerequisitesAsync();
        }
        else
        {
            PrerequisiteText = "Ready";
            foreach (var profile in Profiles)
                profile.IsRunning = profile.Name is "Default browser" or "Steam";
            StatusText = "Preview";
            StatusColor = new SolidColorBrush(Color.FromRgb(69, 198, 255));
            DiagnosticsSummaryText = "Both connections are ready";
            Diagnostics.Add(new ConnectionCheckResult("Ethernet", "Wired connection reached the internet in 18 ms.", DiagnosticState.Good));
            Diagnostics.Add(new ConnectionCheckResult("Wi-Fi", "Mobile hotspot reached the internet in 42 ms.", DiagnosticState.Good));
            Diagnostics.Add(new ConnectionCheckResult("Name lookup", "Web addresses are resolving normally.", DiagnosticState.Good));
            SeedPreviewTraffic();
        }
        Closing += MainWindow_Closing;
        Log(_previewMode ? "Read-only design preview" : "DualLink ready — normal routing is active");
    }

    public ObservableCollection<AppProfile> Profiles { get; } = new();
    public ObservableCollection<LinkInfo> EthernetLinks { get; } = new();
    public ObservableCollection<LinkInfo> WifiLinks { get; } = new();
    public ObservableCollection<string> Activity { get; } = new();
    public ObservableCollection<ConnectionCheckResult> Diagnostics { get; } = new();
    public ObservableCollection<RunningAppInfo> RunningApplications { get; } = new();
    public ObservableCollection<WifiNetworkInfo> WifiNetworks { get; } = new();
    public ObservableCollection<RouteSpeedOption> RouteSpeedOptions { get; } = new()
    {
        new() { Mbps = 0, DisplayName = "Off" },
        new() { Mbps = 1, DisplayName = "1 Mbps" },
        new() { Mbps = 2, DisplayName = "2 Mbps" },
        new() { Mbps = 5, DisplayName = "5 Mbps" },
        new() { Mbps = 10, DisplayName = "10 Mbps" },
        new() { Mbps = 15, DisplayName = "15 Mbps" },
        new() { Mbps = 20, DisplayName = "20 Mbps" },
        new() { Mbps = 25, DisplayName = "25 Mbps" },
        new() { Mbps = 30, DisplayName = "30 Mbps" },
        new() { Mbps = 40, DisplayName = "40 Mbps" },
        new() { Mbps = 50, DisplayName = "50 Mbps" },
        new() { Mbps = 75, DisplayName = "75 Mbps" },
        new() { Mbps = 100, DisplayName = "100 Mbps" },
        new() { Mbps = 125, DisplayName = "125 Mbps" },
        new() { Mbps = 150, DisplayName = "150 Mbps" },
        new() { Mbps = 200, DisplayName = "200 Mbps" },
        new() { Mbps = 250, DisplayName = "250 Mbps" },
        new() { Mbps = 300, DisplayName = "300 Mbps" },
        new() { Mbps = 400, DisplayName = "400 Mbps" },
        new() { Mbps = 500, DisplayName = "500 Mbps" },
        new() { Mbps = 750, DisplayName = "750 Mbps" },
        new() { Mbps = 1000, DisplayName = "1 Gbps" },
        new() { Mbps = LinkInfo.FullSpeedControlMbps, DisplayName = "Full speed" }
    };
    public ObservableCollection<RoutingModeOption> RoutingModeOptions { get; } = new()
    {
        new RoutingModeOption { Mode = RoutingMode.Smart, DisplayName = "Smart", Description = "Adapts to live load and route limits" },
        new RoutingModeOption { Mode = RoutingMode.Balanced, DisplayName = "Balanced", Description = "Follows each route's speed limit" },
        new RoutingModeOption { Mode = RoutingMode.Failover, DisplayName = "Backup", Description = "Ethernet first, Wi-Fi if it fails" }
    };
    public ObservableCollection<UpdateChannelOption> UpdateChannelOptions { get; } = new()
    {
        new UpdateChannelOption { Channel = UpdateChannel.Stable, DisplayName = "Stable", Description = "Only substantial public releases" },
        new UpdateChannelOption { Channel = UpdateChannel.Preview, DisplayName = "Preview", Description = "Development tags, including alpha builds" }
    };

    public LinkInfo? SelectedEthernet
    {
        get => _selectedEthernet;
        set { if (_selectedEthernet != value) { _selectedEthernet = value; OnPropertyChanged(); OnPropertyChanged(nameof(CombinedSpeedText)); OnPropertyChanged(nameof(CombinedUploadSpeedText)); OnPropertyChanged(nameof(EthernetQualityText)); SaveSettings(); } }
    }

    public LinkInfo? SelectedWifi
    {
        get => _selectedWifi;
        set { if (_selectedWifi != value) { _selectedWifi = value; OnPropertyChanged(); OnPropertyChanged(nameof(CombinedSpeedText)); OnPropertyChanged(nameof(CombinedUploadSpeedText)); OnPropertyChanged(nameof(WifiQualityText)); SaveSettings(); } }
    }

    public bool AutoBoost
    {
        get => _autoBoost;
        set { if (_autoBoost != value) { _autoBoost = value; OnPropertyChanged(); SaveSettings(); } }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set { if (_closeToTray != value) { _closeToTray = value; OnPropertyChanged(); SaveSettings(); } }
    }

    public RoutingModeOption? SelectedRoutingModeOption
    {
        get => _selectedRoutingModeOption;
        set
        {
            if (_selectedRoutingModeOption == value) return;
            _selectedRoutingModeOption = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoutingModeDescription));
            ApplyRouteMix();
        }
    }

    public string RoutingModeDescription => SelectedRoutingModeOption?.Description ?? string.Empty;

    public UpdateChannelOption? SelectedUpdateChannelOption
    {
        get => _selectedUpdateChannelOption;
        set
        {
            if (_selectedUpdateChannelOption == value) return;
            _selectedUpdateChannelOption = value;
            _availableUpdate = null;
            UpdateStatusText = value?.Description ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateActionText));
            SaveSettings();
        }
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set { if (_updateStatusText != value) { _updateStatusText = value; OnPropertyChanged(); } }
    }
    public string UpdateActionText => _availableUpdate is null
        ? "Check now"
        : _availableUpdate.CanInstall ? $"Update to {_availableUpdate.Version}" : "View version";

    public string WifiNetworksStatusText
    {
        get => _wifiNetworksStatusText;
        private set { if (_wifiNetworksStatusText != value) { _wifiNetworksStatusText = value; OnPropertyChanged(); } }
    }

    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }
    public Brush StatusColor { get => _statusColor; private set { _statusColor = value; OnPropertyChanged(); } }
    public string PrerequisiteText { get => _prerequisiteText; private set { _prerequisiteText = value; OnPropertyChanged(); } }
    public int ActiveConnections => _balancer.ActiveConnections;
    public string CombinedSpeedText => $"{(SelectedEthernet?.DownloadMbps ?? 0) + (SelectedWifi?.DownloadMbps ?? 0):0.0} Mbps";
    public string CombinedUploadSpeedText => $"{(SelectedEthernet?.UploadMbps ?? 0) + (SelectedWifi?.UploadMbps ?? 0):0.0} Mbps";
    public string TrafficScopeText => _boosting ? "Selected application traffic" : "What both connections are using right now";
    public string TrafficHistoryToolTip => _boosting
        ? "Last minute of selected application traffic · Ethernet and Wi-Fi"
        : "Last minute of total adapter traffic · Ethernet and Wi-Fi";
    public PointCollection EthernetGraphPoints => BuildTrafficPoints(x => x.EthernetMbps);
    public PointCollection WifiGraphPoints => BuildTrafficPoints(x => x.WifiMbps);
    public string DiagnosticsSummaryText
    {
        get => _diagnosticsSummaryText;
        private set { if (_diagnosticsSummaryText != value) { _diagnosticsSummaryText = value; OnPropertyChanged(); } }
    }
    public string EthernetQualityText => GetQualityText(SelectedEthernet);
    public string WifiQualityText => GetQualityText(SelectedWifi);
    public string BoostContributionHeadline
    {
        get
        {
            if (_previewMode) return "Both connections contributed";
            if (!_boosting) return "Ready for the next boost";
            var used = _balancer.RouteStatuses.Where(x => x.SuccessfulConnections > 0 || x.DownloadedBytes > 0 || x.UploadedBytes > 0).ToArray();
            return used.Length switch
            {
                > 1 => "Both connections contributed",
                1 => $"{used[0].Name} is carrying this boost",
                _ => "Waiting for application traffic"
            };
        }
    }
    public string BoostContributionSummary
    {
        get
        {
            if (_previewMode) return "394 MB downloaded · 67 MB uploaded · 27 connections";
            if (!_boosting) return "Usage starts at zero whenever boost begins.";
            var statuses = _balancer.RouteStatuses.Where(x => x.AcceptingNewConnections).ToArray();
            var downloaded = statuses.Sum(x => x.DownloadedBytes);
            var uploaded = statuses.Sum(x => x.UploadedBytes);
            var connections = statuses.Sum(x => x.SuccessfulConnections);
            return $"{FormatBytes(downloaded)} downloaded · {FormatBytes(uploaded)} uploaded · {FormatCount(connections, "connection")}";
        }
    }
    public string EthernetBoostContribution => _previewMode ? "126 MB · 9 connections" : GetRouteContribution(SelectedEthernet);
    public string WifiBoostContribution => _previewMode ? "268 MB · 18 connections" : GetRouteContribution(SelectedWifi);
    public string RouteHealthText
    {
        get
        {
            if (!_balancer.IsRunning) return "Idle";
            var allStatuses = _balancer.RouteStatuses;
            var statuses = allStatuses.Where(x => x.AcceptingNewConnections).ToArray();
            var draining = allStatuses.Where(x => !x.AcceptingNewConnections && x.ActiveConnections > 0).Select(x => x.Name).ToArray();
            string WithDrainingState(string value) => draining.Length == 0 ? value : $"{value} · {string.Join(", ", draining)} draining";
            var unavailable = statuses.Where(x => !x.IsHealthy).Select(x => x.Name).ToArray();
            if (unavailable.Length > 0)
                return WithDrainingState($"Using backup · {string.Join(", ", unavailable)} unavailable");

            var degraded = statuses
                .Where(x => x.QualityLabel is "Unstable" or "Fair" or "Slow")
                .Select(x => x.QualityLabel == "Unstable"
                    ? $"{x.Name} unstable ({x.ReliabilityPercent}%)"
                    : $"{x.Name} {x.QualityLabel.ToLowerInvariant()}")
                .ToArray();
            var health = degraded.Length > 0
                ? string.Join(" · ", degraded)
                : statuses.Length > 1 && statuses.All(x => x.SuccessfulConnections > 0)
                    ? $"{SelectedRoutingModeOption?.DisplayName ?? "Smart"} · both connections used"
                    : $"{SelectedRoutingModeOption?.DisplayName ?? "Smart"} · healthy";
            return WithDrainingState(health);
        }
    }
    public string VersionText => $"Version {UpdateChecker.CurrentVersion}";
    public string RunningApplicationsStatusText => RunningApplications.Count == 0
        ? "No visible applications are running right now."
        : RunningApplications.Count == 1 ? "1 running application found" : $"{RunningApplications.Count} running applications found";

    private void LoadProfilesAndSettings()
    {
        _loadingSettings = true;
        try
        {
            if (!_previewMode && File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_settingsPath)) ?? new UserSettings();
        }
        catch { _settings = new UserSettings(); }

        AutoBoost = _settings.AutoBoost || !File.Exists(_settingsPath);
        CloseToTray = _settings.CloseToTray;
        _armed = _settings.Armed;
        SelectedRoutingModeOption = RoutingModeOptions.FirstOrDefault(x => x.Mode == _settings.RoutingMode) ?? RoutingModeOptions[0];
        SelectedUpdateChannelOption = UpdateChannelOptions.FirstOrDefault(x => x.Channel == _settings.UpdateChannel) ?? UpdateChannelOptions[0];
        var defaults = new List<AppProfile>
        {
            new AppProfile { Name="Epic Games", Subtitle="Epic and EOS game downloads", Accent="#49B8FF", Processes=new(){"EpicGamesLauncher.exe","EpicOnlineServicesInstallHelper.exe"} },
            new AppProfile { Name="Steam", Subtitle="Steam library downloads and updates", Accent="#66C0F4", Processes=new(){"steam.exe"} },
            new AppProfile { Name="Riot Games", Subtitle="Riot client and Valorant updates", Accent="#FF4655", Processes=new(){"RiotClientServices.exe","RiotClientUx.exe"} },
            new AppProfile { Name="Battle.net", Subtitle="Blizzard downloads and Agent updates", Accent="#148EFF", Processes=new(){"Battle.net.exe","Agent.exe"} },
            new AppProfile { Name="EA app", Subtitle="EA downloads and background updater", Accent="#FF6A2A", Processes=new(){"EADesktop.exe","EABackgroundService.exe"} }
        };
        var jDownloader = ApplicationProfileDiscovery.FindJDownloader();
        if (jDownloader is not null) defaults.Add(jDownloader);
        var browser = BrowserDiscovery.FindDefaultBrowser();
        if (browser is not null)
        {
            defaults.Insert(0, new AppProfile
            {
                Name = "Default browser",
                Subtitle = browser.DisplayName,
                Accent = "#A78BFA",
                Processes = new() { browser.ProcessName },
                ExecutablePaths = new() { browser.ExecutablePath },
                IsSystemDetected = true
            });
        }
        foreach (var customProfile in _settings.CustomProfiles ?? new List<AppProfile>())
            ApplicationProfileDiscovery.EnrichKnownApplication(customProfile);
        foreach (var profile in defaults.Concat(_settings.CustomProfiles ?? new List<AppProfile>()))
        {
            profile.IsSelected = _previewMode
                ? profile.Name is "Default browser" or "Steam"
                : _settings.SelectedProfiles.Contains(profile.Name, StringComparer.OrdinalIgnoreCase);
            Profiles.Add(profile);
        }
        _loadingSettings = false;
    }

    private void RefreshAdapters(bool logDiscovery = true)
    {
        var ethernetId = SelectedEthernet?.Id ?? _settings.EthernetId;
        var wifiId = SelectedWifi?.Id ?? _settings.WifiId;
        var ethernetControl = SelectedEthernet?.RouteControlMbps ?? SavedRouteControl(_settings.EthernetWeight, _settings.EthernetSpeedLimitMbps);
        var wifiControl = SelectedWifi?.RouteControlMbps ?? SavedRouteControl(_settings.WifiWeight, _settings.WifiSpeedLimitMbps);
        var discovered = NetworkDiscovery.FindInternetLinks();
        _loadingSettings = true;
        try
        {
            EthernetLinks.Clear();
            WifiLinks.Clear();
            foreach (var link in discovered.Where(x => x.Kind == "Ethernet")) EthernetLinks.Add(link);
            foreach (var link in discovered.Where(x => x.Kind == "Wi-Fi")) WifiLinks.Add(link);

            _selectedEthernet = EthernetLinks.FirstOrDefault(x => x.Id == ethernetId) ?? EthernetLinks.FirstOrDefault();
            _selectedWifi = WifiLinks.FirstOrDefault(x => x.Id == wifiId) ?? WifiLinks.FirstOrDefault();
            if (_selectedEthernet is not null) _selectedEthernet.RouteControlMbps = ethernetControl;
            if (_selectedWifi is not null) _selectedWifi.RouteControlMbps = wifiControl;
            if (_selectedEthernet?.Weight == 0 && _selectedWifi?.Weight == 0 && _selectedEthernet is not null)
                _selectedEthernet.Weight = 1;
            OnPropertyChanged(nameof(SelectedEthernet));
            OnPropertyChanged(nameof(SelectedWifi));
            OnPropertyChanged(nameof(CombinedSpeedText));
            OnPropertyChanged(nameof(CombinedUploadSpeedText));
            OnPropertyChanged(nameof(EthernetQualityText));
            OnPropertyChanged(nameof(WifiQualityText));
        }
        finally { _loadingSettings = false; }
        if (logDiscovery)
            Log($"Detected {EthernetLinks.Count} Ethernet and {WifiLinks.Count} Wi-Fi internet link(s)");
    }

    private static int SavedRouteControl(int legacyWeight, int speedLimitMbps) => legacyWeight <= 0
        ? 0
        : speedLimitMbps <= 0 ? LinkInfo.FullSpeedControlMbps : Math.Min(speedLimitMbps, 1000);

    private void LoadPreviewAdapters()
    {
        EthernetLinks.Clear();
        WifiLinks.Clear();
        AutoBoost = true;
        var ethernet = new LinkInfo
        {
            Id = "preview-ethernet", Name = "Ethernet", Description = "Wired connection",
            Address = "192.0.2.10", Gateway = "192.0.2.1", Kind = "Ethernet", DownloadMbps = 126.4, UploadMbps = 18.2,
            Weight = 2
        };
        var wifi = new LinkInfo
        {
            Id = "preview-wifi", Name = "Wi-Fi", Description = "Wireless connection", NetworkName = "Mobile hotspot",
            Address = "192.0.2.20", Gateway = "192.0.2.1", Kind = "Wi-Fi", DownloadMbps = 268.7, UploadMbps = 49.1,
            Weight = 5
        };
        EthernetLinks.Add(ethernet);
        WifiLinks.Add(wifi);
        SelectedEthernet = ethernet;
        SelectedWifi = wifi;
    }

    private async Task RefreshPrerequisitesAsync()
    {
        var check = await _proxiFyre.CheckPrerequisitesAsync();
        PrerequisiteText = check.Installed ? "Ready" : "Needs setup";
        if (!check.Installed) Log(check.Message);
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (!await _controllerGate.WaitAsync(0)) return;
        try
        {
            var now = DateTime.UtcNow;
            var elapsedSeconds = Math.Max(0.2, (now - _lastRateUpdateUtc).TotalSeconds);
            _lastRateUpdateUtc = now;
            NetworkDiscovery.UpdateRates(EthernetLinks.Concat(WifiLinks), elapsedSeconds);
            if (_boosting) UpdateBoostRates(elapsedSeconds);
            RecordTrafficSample();
            OnPropertyChanged(nameof(ActiveConnections));
            OnPropertyChanged(nameof(CombinedSpeedText));
            OnPropertyChanged(nameof(CombinedUploadSpeedText));
            OnPropertyChanged(nameof(RouteHealthText));
            OnPropertyChanged(nameof(EthernetQualityText));
            OnPropertyChanged(nameof(WifiQualityText));
            RefreshBoostContributionProperties();
            if (now >= _nextProcessScanUtc)
            {
                UpdateRunningProfiles();
                _nextProcessScanUtc = now.AddSeconds(_boosting || IsVisible ? 2 : 8);
            }

            var selected = Profiles.Where(x => x.IsSelected).ToList();
            var shouldBoost = _armed && selected.Count > 0 && (!AutoBoost || selected.Any(x => x.IsRunning));
            if (shouldBoost && !_boosting && now >= _nextStartAttemptUtc) await StartBoostAsync(selected);
            else if (!shouldBoost && _boosting) await StopBoostAsync("No selected target is running");
            else if (shouldBoost && _boosting && DateTime.UtcNow >= _nextHealthCheckUtc)
            {
                _nextHealthCheckUtc = DateTime.UtcNow.AddSeconds(2);
                await VerifyBoostHealthAsync();
            }
            else if (_armed && !_boosting)
            {
                var retrySeconds = Math.Max(0, (int)Math.Ceiling((_nextStartAttemptUtc - now).TotalSeconds));
                StatusText = shouldBoost && retrySeconds > 0 ? $"Retrying in {retrySeconds}s" : "Waiting";
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 184, 77));
                UpdateTray();
            }
            _timer.Interval = TimeSpan.FromSeconds(IsVisible || _boosting ? 1 : 5);
            UpdateTray();
        }
        catch (Exception ex)
        {
            Log($"Controller error: {ex.Message}");
            try
            {
                await StopBoostAsync("Safety stop");
                if (_armed) ScheduleStartRetry();
            }
            catch (Exception restoreError)
            {
                Log($"Automatic restore needs attention: {restoreError.Message}");
                StatusText = "Restore needs attention";
                StatusColor = new SolidColorBrush(Color.FromRgb(240, 108, 123));
                UpdateTray();
            }
        }
        finally { _controllerGate.Release(); }
    }

    private void RecordTrafficSample()
    {
        var now = DateTime.UtcNow;
        _trafficHistory.Enqueue(new TrafficSample(
            now,
            (SelectedEthernet?.DownloadMbps ?? 0) + (SelectedEthernet?.UploadMbps ?? 0),
            (SelectedWifi?.DownloadMbps ?? 0) + (SelectedWifi?.UploadMbps ?? 0)));
        while (_trafficHistory.TryPeek(out var oldest) && now - oldest.TimestampUtc > TimeSpan.FromMinutes(1))
            _trafficHistory.Dequeue();
        OnPropertyChanged(nameof(EthernetGraphPoints));
        OnPropertyChanged(nameof(WifiGraphPoints));
    }

    private void SeedPreviewTraffic()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 42; i++)
        {
            var ethernet = 72 + Math.Sin(i * 0.31) * 22 + (i % 9) * 2.1;
            var wifi = 118 + Math.Cos(i * 0.23) * 35 + (i % 7) * 3.2;
            _trafficHistory.Enqueue(new TrafficSample(now.AddSeconds(i - 41), Math.Max(0, ethernet), Math.Max(0, wifi)));
        }
        OnPropertyChanged(nameof(EthernetGraphPoints));
        OnPropertyChanged(nameof(WifiGraphPoints));
    }

    private PointCollection BuildTrafficPoints(Func<TrafficSample, double> selector)
    {
        var samples = _trafficHistory.ToArray();
        var points = new PointCollection();
        if (samples.Length == 0) return points;
        var maximum = Math.Max(1d, samples.Max(x => Math.Max(x.EthernetMbps, x.WifiMbps)));
        var first = samples[0].TimestampUtc;
        var last = samples[^1].TimestampUtc;
        var durationSeconds = Math.Max(1d, (last - first).TotalSeconds);
        for (var i = 0; i < samples.Length; i++)
        {
            var x = Math.Clamp((samples[i].TimestampUtc - first).TotalSeconds / durationSeconds, 0d, 1d) * 232d;
            var y = 30d - Math.Clamp(selector(samples[i]) / maximum, 0d, 1d) * 28d;
            points.Add(new System.Windows.Point(x, y));
        }
        return points;
    }

    private async Task VerifyBoostHealthAsync()
    {
        if (!await _proxiFyre.IsServiceRunningAsync())
        {
            StatusText = "Recovering…";
            StatusColor = new SolidColorBrush(Color.FromRgb(255, 184, 77));
            UpdateTray();
        }
        if (!await _healthMonitor.CheckAndRecoverAsync()) return;
        UpdateActiveRouteStatus();
        UpdateTray();
        Log("Filter service recovered");
        _tray?.Notify("DualLink recovered", "The application filter restarted and routing is active again.");
    }

    private void UpdateRunningProfiles()
    {
        HashSet<string> names;
        HashSet<string> paths;
        try
        {
            var processes = Process.GetProcesses();
            try
            {
                names = processes.Select(x => x.ProcessName + ".exe").ToHashSet(StringComparer.OrdinalIgnoreCase);
                paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var process in processes)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(path)) paths.Add(Path.GetFullPath(path));
                    }
                    catch { }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { return; }
        foreach (var profile in Profiles)
            profile.IsRunning = profile.Processes.Any(names.Contains) || profile.ExecutablePaths.Any(paths.Contains);
    }

    private async Task StartBoostAsync(IReadOnlyCollection<AppProfile> selected)
    {
        var routes = BuildRouteDefinitions();
        var prerequisite = await _proxiFyre.CheckPrerequisitesAsync();
        if (!prerequisite.Installed)
        {
            PrerequisiteText = "Needs setup";
            _armed = false;
            UpdateButton();
            throw new InvalidOperationException(prerequisite.Message);
        }

        StatusText = "Starting…";
        await _balancer.StartAsync(routes, SelectedRoutingModeOption?.Mode ?? RoutingMode.Smart);
        ResetBoostRateBaselines();
        try
        {
            var processMatchers = selected.SelectMany(x => x.ProcessMatchers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await _proxiFyre.StartAsync(processMatchers, _balancer.BoundPort, _proxyCredentials);
            _boosting = true;
            _startFailureCount = 0;
            _nextStartAttemptUtc = DateTime.MinValue;
            OnPropertyChanged(nameof(TrafficScopeText));
            OnPropertyChanged(nameof(TrafficHistoryToolTip));
            RefreshBoostContributionProperties();
            UpdateActiveRouteStatus();
            UpdateTray();
            Log($"Boost active for {string.Join(", ", selected.Select(x => x.Name))}");
        }
        catch
        {
            await _balancer.StopAsync();
            await _proxiFyre.RestoreAsync();
            throw;
        }
    }

    private async Task StopBoostAsync(string reason)
    {
        if (_boosting || File.Exists(_proxiFyre.SessionPath))
        {
            await _proxiFyre.RestoreAsync();
            await _balancer.StopAsync();
            _boosting = false;
            _routeTrafficBaselines.Clear();
            OnPropertyChanged(nameof(TrafficScopeText));
            OnPropertyChanged(nameof(TrafficHistoryToolTip));
            RefreshBoostContributionProperties();
            Log(reason);
        }
        StatusText = _armed ? "Waiting" : "Ready";
        StatusColor = new SolidColorBrush(_armed ? Color.FromRgb(255, 184, 77) : Color.FromRgb(140, 150, 165));
        UpdateTray();
    }

    private void StartWatchdog()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "DualLink.Watchdog.exe");
        var executable = File.Exists(helper) ? helper : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = File.Exists(helper) ? Environment.ProcessId.ToString() : $"--watchdog {Environment.ProcessId}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private void SaveSettings()
    {
        if (!IsInitialized || _loadingSettings || _previewMode) return;
        try
        {
            _settings.AutoBoost = AutoBoost;
            _settings.Armed = _armed;
            if (SelectedEthernet is not null)
            {
                _settings.EthernetId = SelectedEthernet.Id;
                _settings.EthernetWeight = SelectedEthernet.Weight;
                _settings.EthernetSpeedLimitMbps = SelectedEthernet.SpeedLimitMbps;
            }
            if (SelectedWifi is not null)
            {
                _settings.WifiId = SelectedWifi.Id;
                _settings.WifiWeight = SelectedWifi.Weight;
                _settings.WifiSpeedLimitMbps = SelectedWifi.SpeedLimitMbps;
            }
            _settings.CloseToTray = CloseToTray;
            _settings.RoutingMode = SelectedRoutingModeOption?.Mode ?? RoutingMode.Smart;
            _settings.UpdateChannel = SelectedUpdateChannelOption?.Channel ?? UpdateChannel.Stable;
            _settings.SelectedProfiles = Profiles.Where(x => x.IsSelected).Select(x => x.Name).ToList();
            _settings.CustomProfiles = Profiles.Where(x => x.IsCustom).ToList();
            var serialized = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            var temporaryPath = _settingsPath + ".new";
            File.WriteAllText(temporaryPath, serialized);
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch { }
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            Activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (Activity.Count > 80) Activity.RemoveAt(Activity.Count - 1);
        });
    }

    private void UpdateButton()
    {
        BoostButton.Content = _armed ? "Restore routing" : "Enable boost";
        BoostButton.Background = new SolidColorBrush(_armed ? Color.FromRgb(72, 73, 82) : Color.FromRgb(125, 143, 255));
        BoostButton.BorderBrush = new SolidColorBrush(_armed ? Color.FromRgb(92, 93, 104) : Color.FromRgb(125, 143, 255));
        BoostButton.Foreground = new SolidColorBrush(_armed ? Color.FromRgb(245, 245, 247) : Color.FromRgb(16, 17, 22));
        UpdateTray();
    }

    private void UpdateTray()
    {
        _tray?.Update(new TraySnapshot(
            _armed,
            _boosting,
            (SelectedEthernet?.DownloadMbps ?? 0) + (SelectedWifi?.DownloadMbps ?? 0),
            (SelectedEthernet?.UploadMbps ?? 0) + (SelectedWifi?.UploadMbps ?? 0),
            _balancer.ActiveConnections,
            SelectedRoutingModeOption?.DisplayName ?? "Smart",
            RouteHealthText));
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (_tray is not null) Hide();
        else WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewMode && CloseToTray && !_exitRequested) Hide();
        else { _armed = false; _exitRequested = true; Close(); }
    }

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e) => RefreshAdapters();
    private void EthernetWeightDown_Click(object sender, RoutedEventArgs e) => ChangeWeight(SelectedEthernet, -1);
    private void EthernetWeightUp_Click(object sender, RoutedEventArgs e) => ChangeWeight(SelectedEthernet, 1);
    private void WifiWeightDown_Click(object sender, RoutedEventArgs e) => ChangeWeight(SelectedWifi, -1);
    private void WifiWeightUp_Click(object sender, RoutedEventArgs e) => ChangeWeight(SelectedWifi, 1);
    private void EthernetOnly_Click(object sender, RoutedEventArgs e) => UseOnly(SelectedEthernet, SelectedWifi);
    private void WifiOnly_Click(object sender, RoutedEventArgs e) => UseOnly(SelectedWifi, SelectedEthernet);
    private void RouteLimit_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingSettings || !IsInitialized) return;
        var changed = ReferenceEquals(sender, EthernetRouteLimit) ? SelectedEthernet : SelectedWifi;
        if (changed is null) return;
        if (sender is System.Windows.Controls.ComboBox { SelectedItem: RouteSpeedOption option })
            changed.RouteControlMbps = option.Mbps;
        if (SelectedEthernet is { Weight: 0 } && SelectedWifi is { Weight: 0 })
        {
            changed.RouteControlMbps = LinkInfo.FullSpeedControlMbps;
            Log("Keep at least one connection on");
        }
        var balanced = RoutingModeOptions.First(x => x.Mode == RoutingMode.Balanced);
        if (SelectedRoutingModeOption?.Mode != RoutingMode.Balanced)
        {
            SelectedRoutingModeOption = balanced;
            Log("Balanced mode selected for manual route speeds");
        }
        else ApplyRouteMix();
    }
    private async void ProfileSelection_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_boosting) await ApplySelectionChangeAsync();
    }
    private void AutoBoost_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private async Task ApplySelectionChangeAsync()
    {
        await _controllerGate.WaitAsync();
        try
        {
            UpdateRunningProfiles();
            var selected = Profiles.Where(x => x.IsSelected).ToList();
            var shouldBoost = _armed && selected.Count > 0 && (!AutoBoost || selected.Any(x => x.IsRunning));
            if (_boosting && shouldBoost)
            {
                StatusText = "Updating apps…";
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 184, 77));
                UpdateTray();
                var processMatchers = selected.SelectMany(x => x.ProcessMatchers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                await _proxiFyre.UpdateTargetsAsync(processMatchers, _balancer.BoundPort, _proxyCredentials);
                UpdateActiveRouteStatus();
                UpdateTray();
            }
            else if (_boosting)
            {
                await StopBoostAsync("No selected target is running");
            }
            else if (shouldBoost)
            {
                await StartBoostAsync(selected);
            }
        }
        catch (Exception ex)
        {
            Log($"Target update failed: {ex.Message}");
            try { await StopBoostAsync("Safety stop"); }
            catch (Exception restoreError)
            {
                Log($"Restore needs attention: {restoreError.Message}");
                StatusText = "Restore needs attention";
                StatusColor = new SolidColorBrush(Color.FromRgb(240, 108, 123));
                UpdateTray();
            }
        }
        finally { _controllerGate.Release(); }
    }

    private async void BoostButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleArmedAsync();
    }

    private async Task ToggleArmedAsync()
    {
        await _controllerGate.WaitAsync();
        try
        {
            _armed = !_armed;
            if (_armed)
            {
                _startFailureCount = 0;
                _nextStartAttemptUtc = DateTime.MinValue;
            }
            UpdateButton();
            SaveSettings();
            if (!_armed)
            {
                try { await StopBoostAsync("Boost disabled by user"); }
                catch (Exception ex)
                {
                    Log($"Restore needs attention: {ex.Message}");
                    StatusText = "Restore needs attention";
                    StatusColor = new SolidColorBrush(Color.FromRgb(240, 108, 123));
                    UpdateTray();
                }
            }
            else Log("Auto-boost armed");
        }
        finally { _controllerGate.Release(); }
    }

    private void ChangeWeight(LinkInfo? link, int delta)
    {
        if (link is null) return;
        var other = ReferenceEquals(link, SelectedEthernet) ? SelectedWifi : SelectedEthernet;
        if (delta < 0 && link.Weight == 1 && (other?.Weight ?? 0) == 0)
        {
            Log("Keep at least one route enabled");
            return;
        }
        link.Weight += delta;
        ApplyRouteMix();
    }

    private void UseOnly(LinkInfo? enabled, LinkInfo? disabled)
    {
        if (enabled is null || disabled is null) return;
        if (enabled.Weight == 0) enabled.Weight = 1;
        disabled.Weight = 0;
        ApplyRouteMix();
    }

    private void ApplyRouteMix()
    {
        SaveSettings();
        if (!_balancer.IsRunning) return;
        _balancer.UpdateSources(BuildRouteDefinitions(), SelectedRoutingModeOption?.Mode ?? RoutingMode.Smart);
        UpdateActiveRouteStatus();
        RefreshBoostContributionProperties();
    }

    private RouteDefinition[] BuildRouteDefinitions()
    {
        var routes = new List<RouteDefinition>(2);
        if (SelectedEthernet is { Weight: > 0 } ethernet)
            routes.Add(new RouteDefinition(ethernet.Address, 1, true, "Ethernet", ethernet.SpeedLimitMbps));
        if (SelectedWifi is { Weight: > 0 } wifi)
            routes.Add(new RouteDefinition(wifi.Address, 1, false, "Wi-Fi", wifi.SpeedLimitMbps));
        if (routes.Count == 0)
            throw new InvalidOperationException("Keep at least one connected route enabled.");
        return routes.ToArray();
    }

    private string GetQualityText(LinkInfo? link)
    {
        if (link is null) return "Disconnected";
        var status = _balancer.RouteStatuses.FirstOrDefault(x => x.Address.Equals(link.Address, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(status.Address)) return "Ready";
        var quality = status.QualityLabel == "Unstable"
            ? $"Unstable · {status.ReliabilityPercent}%"
            : status.ConnectLatencyMs is double latency
            ? $"{status.QualityLabel} · {latency:0} ms"
            : status.QualityLabel;
        return _boosting && status.SuccessfulConnections > 0
            ? $"{quality} · {status.SuccessfulConnections} used"
            : quality;
    }

    private string GetRouteContribution(LinkInfo? link)
    {
        if (link is null) return "Not connected";
        if (!_boosting) return "Waiting";
        var status = _balancer.RouteStatuses.FirstOrDefault(x => x.Address.Equals(link.Address, StringComparison.OrdinalIgnoreCase));
        if (link.Weight == 0)
        {
            if (string.IsNullOrEmpty(status.Address)) return "Turned off";
            var retiredTotal = status.DownloadedBytes + status.UploadedBytes;
            if (status.ActiveConnections > 0) return $"Draining · {FormatBytes(retiredTotal)}";
            return retiredTotal > 0 ? $"Off · {FormatBytes(retiredTotal)} used" : "Turned off";
        }
        if (string.IsNullOrEmpty(status.Address)) return "Not active";
        var total = status.DownloadedBytes + status.UploadedBytes;
        return total == 0 && status.SuccessfulConnections == 0
            ? "Waiting"
            : $"{FormatBytes(total)} · {FormatCount(status.SuccessfulConnections, "connection")}";
    }

    private void RefreshBoostContributionProperties()
    {
        OnPropertyChanged(nameof(BoostContributionHeadline));
        OnPropertyChanged(nameof(BoostContributionSummary));
        OnPropertyChanged(nameof(EthernetBoostContribution));
        OnPropertyChanged(nameof(WifiBoostContribution));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var display = (double)Math.Max(0, bytes);
        var unit = 0;
        while (display >= 1000 && unit < units.Length - 1)
        {
            display /= 1000;
            unit++;
        }
        return unit == 0 ? $"{display:0} {units[unit]}" : $"{display:0.#} {units[unit]}";
    }

    private static string FormatCount(long count, string singular) => $"{count} {(count == 1 ? singular : singular + "s")}";

    private void NetworkChanged(object? sender, EventArgs e)
    {
        if (_allowClose || _previewMode) return;
        Dispatcher.BeginInvoke(() =>
        {
            _networkDebounceTimer.Stop();
            _networkDebounceTimer.Start();
        });
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Resume or PowerModes.StatusChange)
            NetworkChanged(sender, e);
    }

    private async void NetworkDebounce_Tick(object? sender, EventArgs e)
    {
        _networkDebounceTimer.Stop();
        if (!await _controllerGate.WaitAsync(0))
        {
            _networkDebounceTimer.Start();
            return;
        }
        try
        {
            var previousAddresses = new[] { SelectedEthernet?.Address, SelectedWifi?.Address };
            var previousCount = previousAddresses.Count(x => !string.IsNullOrWhiteSpace(x));
            RefreshAdapters(logDiscovery: false);
            var currentAddresses = new[] { SelectedEthernet?.Address, SelectedWifi?.Address };
            if (previousAddresses.SequenceEqual(currentAddresses, StringComparer.OrdinalIgnoreCase)) return;

            Log("Network changed; refreshed available connections");
            var currentCount = currentAddresses.Count(x => !string.IsNullOrWhiteSpace(x));
            if (currentCount == 0)
                _tray?.Notify("Connections unavailable", "Ethernet and Wi-Fi are both offline. Normal routing will be restored.", System.Windows.Forms.ToolTipIcon.Warning);
            else if (currentCount < previousCount)
                _tray?.Notify("One connection was lost", "DualLink will keep new sessions on the remaining connection.", System.Windows.Forms.ToolTipIcon.Warning);
            else if (currentCount > previousCount)
                _tray?.Notify("Connection restored", "The recovered connection is available for new sessions.");
            if (!_balancer.IsRunning) return;
            try
            {
                _balancer.UpdateSources(BuildRouteDefinitions(), SelectedRoutingModeOption?.Mode ?? RoutingMode.Smart);
                UpdateActiveRouteStatus();
                Log("New sessions will use the refreshed connections");
            }
            catch (InvalidOperationException)
            {
                await StopBoostAsync("No selected internet connection is available");
            }
        }
        catch (Exception ex) { Log($"Network refresh failed: {ex.Message}"); }
        finally { _controllerGate.Release(); }
    }

    private void UpdateActiveRouteStatus()
    {
        var ethernetEnabled = SelectedEthernet is { Weight: > 0 };
        var wifiEnabled = SelectedWifi is { Weight: > 0 };
        StatusText = ethernetEnabled && !wifiEnabled ? "Ethernet only" :
            wifiEnabled && !ethernetEnabled ? "Wi-Fi only" : "Boosting";
        StatusColor = new SolidColorBrush(Color.FromRgb(85, 230, 165));
        OnPropertyChanged(nameof(RouteHealthText));
    }

    private void ScheduleStartRetry()
    {
        _startFailureCount = Math.Min(_startFailureCount + 1, 5);
        var delaySeconds = Math.Min(30, 3 * (1 << (_startFailureCount - 1)));
        _nextStartAttemptUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
        StatusText = $"Retrying in {delaySeconds}s";
        StatusColor = new SolidColorBrush(Color.FromRgb(255, 184, 77));
        Log($"Will retry in {delaySeconds} seconds");
        UpdateTray();
    }

    private void ResetBoostRateBaselines()
    {
        _routeTrafficBaselines.Clear();
        foreach (var status in _balancer.RouteStatuses)
            _routeTrafficBaselines[status.Address] = new RouteTrafficBaseline(status.DownloadedBytes, status.UploadedBytes);
    }

    private void UpdateBoostRates(double elapsedSeconds)
    {
        var statuses = _balancer.RouteStatuses.ToDictionary(x => x.Address, StringComparer.OrdinalIgnoreCase);
        foreach (var link in new[] { SelectedEthernet, SelectedWifi }.OfType<LinkInfo>())
        {
            if (!statuses.TryGetValue(link.Address, out var status))
            {
                link.DownloadMbps = 0;
                link.UploadMbps = 0;
                continue;
            }

            if (_routeTrafficBaselines.TryGetValue(link.Address, out var previous))
            {
                link.DownloadMbps = Math.Max(0, status.DownloadedBytes - previous.DownloadedBytes) * 8d / elapsedSeconds / 1_000_000d;
                link.UploadMbps = Math.Max(0, status.UploadedBytes - previous.UploadedBytes) * 8d / elapsedSeconds / 1_000_000d;
            }
            else
            {
                link.DownloadMbps = 0;
                link.UploadMbps = 0;
            }
            _routeTrafficBaselines[link.Address] = new RouteTrafficBaseline(status.DownloadedBytes, status.UploadedBytes);
        }
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        SettingsDrawer.Visibility = Visibility.Collapsed;
        AddAppDrawer.Visibility = Visibility.Collapsed;
        WifiNetworksDrawer.Visibility = Visibility.Collapsed;
        DetailsDrawer.Visibility = DetailsDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Activity_Click(object sender, RoutedEventArgs e)
    {
        var showActivity = ActivityPanel.Visibility != Visibility.Visible;
        ActivityPanel.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticPanel.Visibility = showActivity ? Visibility.Collapsed : Visibility.Visible;
        ActivityToggleButton.Content = showActivity ? "Results" : "Activity";
    }

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        _diagnosticsCts?.Cancel();
        _diagnosticsCts?.Dispose();
        _diagnosticsCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = _diagnosticsCts.Token;
        Diagnostics.Clear();
        DiagnosticsSummaryText = "Checking both connections…";
        try
        {
            var prerequisite = await _proxiFyre.CheckPrerequisitesAsync();
            Diagnostics.Add(new ConnectionCheckResult(
                "Network filter",
                prerequisite.Installed ? "The application filter is ready." : prerequisite.Message,
                prerequisite.Installed ? DiagnosticState.Good : DiagnosticState.Problem));

            var linksAreIndependent = SelectedEthernet is not null && SelectedWifi is not null &&
                !SelectedEthernet.Id.Equals(SelectedWifi.Id, StringComparison.OrdinalIgnoreCase) &&
                !SelectedEthernet.Address.Equals(SelectedWifi.Address, StringComparison.OrdinalIgnoreCase);
            Diagnostics.Add(new ConnectionCheckResult(
                "Connection mix",
                linksAreIndependent ? "Ethernet and Wi-Fi use separate local routes." : "Two different connected routes are not currently available.",
                linksAreIndependent ? DiagnosticState.Good : DiagnosticState.Notice));

            var connectivity = await Task.WhenAll(
                NetworkDiscovery.CheckConnectivityAsync(SelectedEthernet, token),
                NetworkDiscovery.CheckConnectivityAsync(SelectedWifi, token),
                NetworkDiscovery.CheckDnsAsync(token));
            foreach (var check in connectivity) Diagnostics.Add(check);

            if (_boosting)
            {
                var routeStatuses = _balancer.RouteStatuses;
                var enabledRouteCount = routeStatuses.Count(x => x.AcceptingNewConnections);
                var contributing = routeStatuses.Where(x => x.SuccessfulConnections > 0).ToArray();
                var activeText = ActiveConnections == 1 ? "1 session is active now." : $"{ActiveConnections} sessions are active now.";
                var routeMessage = enabledRouteCount == 1 && contributing.Length == 1
                    ? $"{contributing[0].Name} is the only enabled connection and is carrying selected-app traffic. {activeText}"
                    : contributing.Length >= 2
                        ? $"Both connections have carried selected-app sessions in this boost. {activeText}"
                        : contributing.Length == 1
                            ? $"Only {contributing[0].Name} has carried sessions so far. DualLink assigns whole new connections; one connection cannot be split."
                            : "No selected application has opened a routed session yet.";
                var routeState = (enabledRouteCount == 1 && contributing.Length == 1) || contributing.Length >= 2
                    ? DiagnosticState.Good
                    : DiagnosticState.Notice;
                Diagnostics.Add(new ConnectionCheckResult(
                    "Active routing",
                    routeMessage,
                    routeState));
            }
            else
            {
                Diagnostics.Add(new ConnectionCheckResult("Active routing", "Boost is idle; connection checks still work normally.", DiagnosticState.Notice));
            }

            var problems = Diagnostics.Count(x => x.State == DiagnosticState.Problem);
            var notices = Diagnostics.Count(x => x.State == DiagnosticState.Notice);
            DiagnosticsSummaryText = problems > 0
                ? $"{problems} issue{(problems == 1 ? string.Empty : "s")} need attention"
                : notices > 0 ? "Connections work, with a few notes" : "Both connections are ready";
        }
        catch (OperationCanceledException)
        {
            DiagnosticsSummaryText = "The check was cancelled.";
        }
        catch (Exception ex)
        {
            DiagnosticsSummaryText = "The check could not finish.";
            Diagnostics.Add(new ConnectionCheckResult("Diagnostics", ex.Message, DiagnosticState.Problem));
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not null)
        {
            if (_availableUpdate.CanInstall) await InstallAvailableUpdateAsync(_availableUpdate);
            else Process.Start(new ProcessStartInfo { FileName = _availableUpdate.PageUrl, UseShellExecute = true });
            return;
        }

        UpdateCheckButton.IsEnabled = false;
        UpdateStatusText = "Checking GitHub…";
        try
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _updateCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await UpdateChecker.CheckAsync(
                SelectedUpdateChannelOption?.Channel ?? UpdateChannel.Stable,
                _updateCts.Token);
            UpdateStatusText = result.Message;
            _availableUpdate = result.IsAvailable ? result : null;
            OnPropertyChanged(nameof(UpdateActionText));
        }
        catch
        {
            UpdateStatusText = "Could not reach GitHub. Try again later.";
        }
        finally { UpdateCheckButton.IsEnabled = true; }
    }

    private async Task InstallAvailableUpdateAsync(UpdateCheckResult update)
    {
        var answer = MessageBox.Show(
            $"Download and install DualLink {update.Version}?\n\nDualLink will verify the published SHA-256 checksum, restore normal routing, and close before setup continues.",
            "Install DualLink update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        UpdateCheckButton.IsEnabled = false;
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try
        {
            var progress = new Progress<int>(percent => UpdateStatusText = $"Downloading {update.Version} · {percent}%");
            var installer = await UpdateChecker.DownloadInstallerAsync(update, progress, _updateCts.Token);
            UpdateStatusText = "Verified · preparing setup";

            await _controllerGate.WaitAsync();
            try
            {
                _armed = false;
                UpdateButton();
                SaveSettings();
                await StopBoostAsync("Preparing verified update — normal routing restored");
            }
            finally { _controllerGate.Release(); }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/UPDATE=1 /CLOSEAPPLICATIONS /NORESTART",
                UseShellExecute = true
            });
            if (process is null) throw new InvalidOperationException("Windows could not start the verified installer.");
            _exitRequested = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update cancelled";
        }
        catch (Exception ex)
        {
            UpdateStatusText = "Update could not be installed";
            Log($"Update failed: {ex.Message}");
            MessageBox.Show(
                "DualLink couldn't download and verify the update. Check your internet connection, then try again. No changes were made.",
                "Update not installed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            if (!_exitRequested) UpdateCheckButton.IsEnabled = true;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        DetailsDrawer.Visibility = Visibility.Collapsed;
        AddAppDrawer.Visibility = Visibility.Collapsed;
        WifiNetworksDrawer.Visibility = Visibility.Collapsed;
        SettingsDrawer.Visibility = SettingsDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowSettingsPreview() => SettingsDrawer.Visibility = Visibility.Visible;
    public void ShowDetailsPreview() => DetailsDrawer.Visibility = Visibility.Visible;
    public void ShowNetworkPickerPreview()
    {
        WifiNetworks.Clear();
        WifiNetworks.Add(new WifiNetworkInfo("Mobile hotspot", "Mobile hotspot", Guid.Empty, "Wi-Fi", 91, true, true));
        WifiNetworks.Add(new WifiNetworkInfo("Home 5 GHz", "Home 5 GHz", Guid.Empty, "Wi-Fi", 76, false, true));
        WifiNetworks.Add(new WifiNetworkInfo("Guest network", string.Empty, Guid.Empty, "Wi-Fi", 58, false, true));
        WifiNetworksStatusText = "3 nearby networks";
        WifiNetworksDrawer.Visibility = Visibility.Visible;
    }
    public void ShowAddApplicationPreview()
    {
        RunningApplications.Clear();
        RunningApplications.Add(new RunningAppInfo("Example downloader", "Downloader.exe", @"C:\Apps\Downloader.exe"));
        RunningApplications.Add(new RunningAppInfo("Media player", "Player.exe", @"C:\Apps\Player.exe"));
        RunningApplications.Add(new RunningAppInfo("Chat application", "Chat.exe", @"C:\Apps\Chat.exe"));
        OnPropertyChanged(nameof(RunningApplicationsStatusText));
        AddAppDrawer.Visibility = Visibility.Visible;
    }
    public void ShowTrayPreview()
    {
        _tray ??= new TrayManager(() => { }, () => { }, () => { });
        _tray.Update(new TraySnapshot(true, true, 395.1, 67.3, 27, "Smart", "Both connections healthy"));
        _tray.ShowMenuForPreview();
    }

    private void CloseDrawer_Click(object sender, RoutedEventArgs e)
    {
        DetailsDrawer.Visibility = Visibility.Collapsed;
        SettingsDrawer.Visibility = Visibility.Collapsed;
        AddAppDrawer.Visibility = Visibility.Collapsed;
        WifiNetworksDrawer.Visibility = Visibility.Collapsed;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _armed = false;
        SaveSettings();
        _exitRequested = true;
        Show();
        Close();
    }

    private void AddExecutable_Click(object sender, RoutedEventArgs e)
    {
        DetailsDrawer.Visibility = Visibility.Collapsed;
        SettingsDrawer.Visibility = Visibility.Collapsed;
        WifiNetworksDrawer.Visibility = Visibility.Collapsed;
        RefreshRunningApplications();
        AddAppDrawer.Visibility = Visibility.Visible;
    }

    private async void WifiNetworks_Click(object sender, RoutedEventArgs e)
    {
        DetailsDrawer.Visibility = Visibility.Collapsed;
        SettingsDrawer.Visibility = Visibility.Collapsed;
        AddAppDrawer.Visibility = Visibility.Collapsed;
        WifiNetworksDrawer.Visibility = Visibility.Visible;
        await RefreshWifiNetworksAsync();
    }

    private async void RefreshWifiNetworks_Click(object sender, RoutedEventArgs e) => await RefreshWifiNetworksAsync();

    private async Task RefreshWifiNetworksAsync()
    {
        if (_previewMode) return;
        WifiNetworksStatusText = "Scanning…";
        try
        {
            var networks = await Task.Run(() => WifiManager.GetAvailableNetworks());
            WifiNetworks.Clear();
            foreach (var network in networks) WifiNetworks.Add(network);
            WifiNetworksStatusText = networks.Count == 0
                ? "No nearby networks found"
                : networks.Count == 1 ? "1 nearby network" : $"{networks.Count} nearby networks";
        }
        catch
        {
            WifiNetworksStatusText = "Wi-Fi scan was unavailable";
        }
    }

    private async void ConnectWifi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WifiNetworkInfo network }) return;
        if (!network.IsSaved)
        {
            OpenWindowsWifiPicker();
            WifiNetworksStatusText = $"Choose {network.Name} in Windows to enter its password";
            return;
        }

        _wifiConnectCts?.Cancel();
        _wifiConnectCts?.Dispose();
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _wifiConnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        WifiNetworksStatusText = $"Connecting to {network.Name}…";
        try
        {
            var connected = await WifiManager.ConnectAsync(network, _wifiConnectCts.Token);
            if (!connected)
            {
                WifiNetworksStatusText = $"Could not connect to {network.Name}";
                return;
            }
            await Task.Delay(900);
            RefreshAdapters(logDiscovery: false);
            if (_balancer.IsRunning) ApplyRouteMix();
            await RefreshWifiNetworksAsync();
            Log($"Wi-Fi changed to {network.Name}");
        }
        catch (OperationCanceledException)
        {
            WifiNetworksStatusText = "Wi-Fi connection timed out";
        }
    }

    private void OpenWindowsWifi_Click(object sender, RoutedEventArgs e) => OpenWindowsWifiPicker();

    private static void OpenWindowsWifiPicker() => Process.Start(new ProcessStartInfo
    {
        FileName = "ms-availablenetworks:",
        UseShellExecute = true
    });

    private async void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = "Choose an application to boost" };
        if (dialog.ShowDialog(this) != true) return;
        await AddExecutablePathAsync(dialog.FileName);
    }

    private async void AddRunningApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RunningAppInfo application }) return;
        await AddExecutablePathAsync(application.ExecutablePath);
    }

    private async Task AddExecutablePathAsync(string path)
    {
        var executablePath = Path.GetFullPath(path);
        var processName = Path.GetFileName(executablePath);
        var existing = Profiles.FirstOrDefault(x => x.ExecutablePaths.Any(path =>
                path.Equals(executablePath, StringComparison.OrdinalIgnoreCase)))
            ?? Profiles.FirstOrDefault(x => !x.IsCustom && x.Processes.Contains(processName, StringComparer.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var selectionChanged = !existing.IsSelected;
            existing.IsSelected = true;
            SaveSettings();
            if (_boosting && selectionChanged) await ApplySelectionChangeAsync();
            Log($"{existing.Name} is already in the application list");
            AddAppDrawer.Visibility = Visibility.Collapsed;
            return;
        }

        var executablePaths = ApplicationProfileDiscovery.ExpandExecutablePaths(executablePath);
        var profile = new AppProfile
        {
            Name = GetExecutableDisplayName(executablePath),
            Subtitle = executablePaths.Count > 1 ? "Application and its download engine" : "Custom application",
            Accent = "#B6C2D1",
            Processes = new List<string> { processName },
            ExecutablePaths = executablePaths,
            IsCustom = true,
            IsSelected = true
        };
        Profiles.Add(profile);
        SaveSettings();
        Log($"Added {processName}");
        AddAppDrawer.Visibility = Visibility.Collapsed;
        if (_boosting) await ApplySelectionChangeAsync();
    }

    private void RefreshRunningApplications()
    {
        var discovered = new Dictionary<string, RunningAppInfo>(StringComparer.OrdinalIgnoreCase);
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    var executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) continue;
                    executablePath = Path.GetFullPath(executablePath);
                    if (discovered.ContainsKey(executablePath)) continue;
                    var processName = Path.GetFileName(executablePath);
                    discovered[executablePath] = new RunningAppInfo(GetExecutableDisplayName(executablePath), processName, executablePath);
                }
                catch { }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        RunningApplications.Clear();
        foreach (var application in discovered.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            RunningApplications.Add(application);
        OnPropertyChanged(nameof(RunningApplicationsStatusText));
    }

    private static string GetExecutableDisplayName(string executablePath)
    {
        string? displayName = null;
        try { displayName = FileVersionInfo.GetVersionInfo(executablePath).FileDescription; }
        catch { }
        if (string.IsNullOrWhiteSpace(displayName)) displayName = Path.GetFileNameWithoutExtension(executablePath);
        var normalized = displayName.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 80 ? normalized : normalized[..77] + "…";
    }

    private async void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AppProfile profile } || !profile.IsCustom) return;
        var requiresRestart = _boosting && profile.IsSelected;
        Profiles.Remove(profile);
        SaveSettings();
        Log($"Removed {profile.Name}");
        if (requiresRestart) await ApplySelectionChangeAsync();
    }

    private async void InstallFilter_Click(object sender, RoutedEventArgs e)
    {
        var check = await _proxiFyre.CheckPrerequisitesAsync();
        if (check.Installed)
        {
            PrerequisiteText = "Ready";
            MessageBox.Show("ProxiFyre and Windows Packet Filter are already installed.", "DualLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        const string expectedHash = "1a79c20aac1a333463fe46d8f9196a8c39121980cf18fe8ccc9e978e51601139";
        var installer = Path.Combine(AppContext.BaseDirectory, "ProxiFyre-2.5.0-win-x64-setup.exe");
        if (!File.Exists(installer))
        {
            MessageBox.Show("The verified ProxiFyre setup is missing from the DualLink folder.", "DualLink", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        await using var stream = File.OpenRead(installer);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("The ProxiFyre installer checksum does not match. Installation was blocked.", "DualLink", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var answer = MessageBox.Show(
            "Install the verified ProxiFyre 2.5.0 application, persistent service, and Windows Packet Filter kernel driver?\n\nDualLink only starts the filter while boosting and restores its previous state when stopped.",
            "Install network filter",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var process = Process.Start(new ProcessStartInfo { FileName = installer, UseShellExecute = true });
        if (process is not null) await process.WaitForExitAsync();
        await RefreshPrerequisitesAsync();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_previewMode)
        {
            _tray?.Dispose();
            _tray = null;
            _allowClose = true;
            return;
        }
        if (!_exitRequested && CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (_allowClose) return;
        e.Cancel = true;
        _timer.Stop();
        _networkDebounceTimer.Stop();
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        _diagnosticsCts?.Cancel();
        _diagnosticsCts?.Dispose();
        _wifiConnectCts?.Cancel();
        _wifiConnectCts?.Dispose();
        await _controllerGate.WaitAsync();
        try
        {
            _armed = false;
            UpdateButton();
            try
            {
                await StopBoostAsync("DualLink closed — normal routing restored");
            }
            catch (Exception ex)
            {
                Log($"Foreground restore failed; watchdog will retry: {ex.Message}");
            }
            SaveSettings();
            _tray?.Dispose();
            _tray = null;
            _allowClose = true;
            Close();
        }
        finally { _controllerGate.Release(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private readonly record struct TrafficSample(DateTime TimestampUtc, double EthernetMbps, double WifiMbps);
    private readonly record struct RouteTrafficBaseline(long DownloadedBytes, long UploadedBytes);
}
