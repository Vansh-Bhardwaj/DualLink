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
using ServiceProtocol = DualLink.Service.Protocol;

namespace DualLink;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly ProxiFyreManager _proxiFyre;
    private readonly ProxyCredentials _proxyCredentials;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _networkDebounceTimer;
    private readonly bool _previewMode;
    private readonly SemaphoreSlim _controllerGate = new(1, 1);
    private readonly SessionRecovery _sessionRecovery = new();
    private DiagnosticLog? _diagnosticLog;
    private DateTimeOffset? _lastServiceSampleUtc;
    private bool _resetDeviceRateBaseline = true;
    private int _routeUpdateVersion;
    private bool _discoveringApps;
    private bool _scanningInstalledApps;
    private bool _busy;
    private string _actionHint = string.Empty;
    private readonly Dictionary<string, int> _enabledRouteLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _settingsWriteGate = new(1, 1);
    private Task _settingsWriteTask = Task.CompletedTask;
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
    private string _statusText = "Off";
    private Brush _statusColor = new SolidColorBrush(Color.FromRgb(140, 150, 165));
    private string _prerequisiteText = "Checking";
    private DateTime _lastRateUpdateUtc = DateTime.UtcNow;
    private DateTime _nextProcessScanUtc = DateTime.MinValue;
    private int _processScanInProgress;
    private CancellationTokenSource? _diagnosticsCts;
    private CancellationTokenSource? _wifiConnectCts;
    private CancellationTokenSource? _updateCts;
    private string _diagnosticsSummaryText = "Ready to check";
    private string _updateStatusText = string.Empty;
    private string _wifiNetworksStatusText = "Ready";
    private UpdateCheckResult? _availableUpdate;
    private readonly Queue<TrafficSample> _trafficHistory = new();
    private readonly Dictionary<string, RouteTrafficBaseline> _routeTrafficBaselines = new(StringComparer.OrdinalIgnoreCase);
    private DualLinkServiceClient? _serviceClient;
    private ServiceProtocol.SessionStatus? _serviceStatus;

    public MainWindow(bool previewMode = false)
    {
        InitializeComponent();
        DataContext = this;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && Drawers().Any(x => x.Visibility == Visibility.Visible))
            {
                CloseDrawers();
                e.Handled = true;
            }
        };
        Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedAppsText));
            OnPropertyChanged(nameof(HasNoApps));
        };
        _previewMode = previewMode;

        _settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink");
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        Directory.CreateDirectory(_settingsDirectory);
        _diagnosticLog = new DiagnosticLog(Path.Combine(_settingsDirectory, "app.log"));
        _proxiFyre = new ProxiFyreManager(Log);
        _proxyCredentials = ProxyCredentials.Create();

        LoadProfilesAndSettings();
        if (_previewMode)
        {
            LoadPreviewAdapters();
            LoadPreviewProfiles();
        }

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
            NetworkChange.NetworkAddressChanged += NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged;
            SystemEvents.PowerModeChanged += PowerModeChanged;
            Loaded += async (_, _) =>
            {
                IsBusy = true;
                try
                {
                    await RefreshInstalledAppsAsync();
                    await RefreshAdaptersAsync();
                    await RefreshPrerequisitesAsync();
                    await UpdateRunningProfilesAsync();
                }
                catch (Exception ex) { Log($"Startup failed: {ex.Message}"); ActionHint = "Try Refresh again"; }
                finally { IsBusy = false; if (!_exitRequested) _timer.Start(); }
            };
        }
        else
        {
            PrerequisiteText = "Ready";
            foreach (var profile in Profiles)
                profile.IsRunning = profile.Name is "Default browser" or "Download manager";
            StatusText = "Preview";
            StatusColor = new SolidColorBrush(Color.FromRgb(69, 198, 255));
            DiagnosticsSummaryText = "Ready";
            Diagnostics.Add(new ConnectionCheckResult("Ethernet", "18 ms", DiagnosticState.Good));
            Diagnostics.Add(new ConnectionCheckResult("Wi-Fi", "42 ms", DiagnosticState.Good));
            Diagnostics.Add(new ConnectionCheckResult("Web links", "Ready", DiagnosticState.Good));
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
        new RoutingModeOption { Mode = RoutingMode.Balanced, DisplayName = "Both", Description = "Share new connections" },
        new RoutingModeOption { Mode = RoutingMode.Smart, DisplayName = "Safe", Description = "Keep each server on one link" },
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
        set { if (_selectedEthernet != value) { _selectedEthernet = value; OnPropertyChanged(); OnPropertyChanged(nameof(CombinedSpeedText)); OnPropertyChanged(nameof(CombinedUploadSpeedText)); OnPropertyChanged(nameof(EthernetQualityText)); ApplyRouteMix(); } }
    }

    public LinkInfo? SelectedWifi
    {
        get => _selectedWifi;
        set { if (_selectedWifi != value) { _selectedWifi = value; OnPropertyChanged(); OnPropertyChanged(nameof(CombinedSpeedText)); OnPropertyChanged(nameof(CombinedUploadSpeedText)); OnPropertyChanged(nameof(WifiQualityText)); ApplyRouteMix(); } }
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
            OnPropertyChanged(nameof(CompatibilityGuardText));
            ApplyRouteMix();
        }
    }

    public string RoutingModeDescription => SelectedRoutingModeOption?.Description ?? string.Empty;
    public string CompatibilityGuardText
    {
        get
        {
            var mode = SelectedRoutingModeOption?.Mode ?? RoutingMode.Smart;
            if (mode == RoutingMode.Balanced) return "Manual limits apply instantly to new connections.";
            if (mode == RoutingMode.Failover) return "Ethernet first; Wi-Fi takes over if needed.";
            var guard = _serviceStatus;
            if (guard?.CompatibilityGuardActive != true) return "Protects sign-in and keeps destinations consistent.";
            if (guard.CompatibilityGuardWarmingUp) return "Protecting sign-in traffic before using both connections.";
            return guard.RememberedDestinations == 0
                ? "Learning the safest connection for each destination."
                : $"Keeping {FormatCount(guard.RememberedDestinations, "destination")} consistent.";
        }
    }

    public UpdateChannelOption? SelectedUpdateChannelOption
    {
        get => _selectedUpdateChannelOption;
        set
        {
            if (_selectedUpdateChannelOption == value) return;
            _selectedUpdateChannelOption = value;
            _availableUpdate = null;
            UpdateStatusText = string.Empty;
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
    public bool IsBusy { get => _busy; private set { _busy = value; OnPropertyChanged(); UpdateButton(); } }
    public string ActionHint { get => _actionHint; private set { _actionHint = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasActionHint)); } }
    public bool HasActionHint => !string.IsNullOrWhiteSpace(ActionHint);
    public string RecoveryText => _serviceStatus?.WatchdogRunning == true ? "Ready" : "Off";
    public string SessionTrafficText => _previewMode ? "394 MB" : !_boosting ? "0 MB" : FormatBytes(CurrentRouteStatuses.Sum(x => x.DownloadedBytes));
    public string SelectedAppsText => $"{Profiles.Count(x => x.IsSelected)} selected";
    public string EthernetLinkState => GetLinkState(SelectedEthernet);
    public string WifiLinkState => GetLinkState(SelectedWifi);
    public string EthernetUsageText => GetLinkUsage(SelectedEthernet, "126 MB");
    public string WifiUsageText => GetLinkUsage(SelectedWifi, "268 MB");
    public Brush StatusColor { get => _statusColor; private set { _statusColor = value; OnPropertyChanged(); } }
    public string PrerequisiteText { get => _prerequisiteText; private set { _prerequisiteText = value; OnPropertyChanged(); } }
    public int ActiveConnections => _previewMode ? 27 : _serviceStatus?.ActiveConnections ?? 0;
    public string CombinedSpeedText => $"{(SelectedEthernet?.DownloadMbps ?? 0) + (SelectedWifi?.DownloadMbps ?? 0):0.0} Mbps";
    public string CombinedUploadSpeedText => $"{(SelectedEthernet?.UploadMbps ?? 0) + (SelectedWifi?.UploadMbps ?? 0):0.0} Mbps";
    public string TrafficScopeText => _boosting || _previewMode ? "App speed" : "Device speed";
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
            if (_previewMode) return "Both links used";
            if (!_boosting) return "Off";
            var used = CurrentRouteStatuses.Where(x => x.SuccessfulConnections > 0 || x.DownloadedBytes > 0 || x.UploadedBytes > 0).ToArray();
            return used.Length switch
            {
                > 1 => "Both links used",
                1 => $"{used[0].Name} used",
                _ => "Waiting for traffic"
            };
        }
    }
    public string BoostContributionSummary
    {
        get
        {
            if (_previewMode) return "394 MB downloaded · 67 MB uploaded · 27 connections";
            if (!_boosting) return "0 MB";
            var statuses = CurrentRouteStatuses;
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
            if (_previewMode) return "Smart · both connections used";
            if (!RouterIsRunning) return "Idle";
            var allStatuses = CurrentRouteStatuses;
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
    public string RunningApplicationsStatusText => _discoveringApps ? "Loading…" : RunningApplications.Count == 0
        ? "No open apps" : $"{RunningApplications.Count} open";

    private bool RouterIsRunning => _serviceStatus?.IsRunning == true;
    private IReadOnlyList<ServiceProtocol.RouteStatus> CurrentRouteStatuses =>
        _serviceStatus?.Routes ?? Array.Empty<ServiceProtocol.RouteStatus>();

    private string GetLinkState(LinkInfo? link)
    {
        if (link is null) return "Not connected";
        if (link.Weight == 0) return "Off";
        if (_previewMode) return "In use";
        var route = CurrentRouteStatuses.FirstOrDefault(x => x.Address == link.Address);
        if (_boosting && !route.IsHealthy) return "Reconnecting";
        return route.ActiveConnections > 0 ? "In use" : "Ready";
    }

    private string GetLinkUsage(LinkInfo? link, string preview)
    {
        if (_previewMode) return preview;
        if (link is null || !_boosting) return "0 MB";
        return FormatBytes(CurrentRouteStatuses.Where(x => x.Address == link.Address).Sum(x => x.DownloadedBytes));
    }

    private void SetBoosting(bool value)
    {
        if (_boosting == value) return;
        _boosting = value;
        if (!value) _resetDeviceRateBaseline = true;
        _sessionRecovery.Reset();
        _trafficHistory.Clear();
        _lastServiceSampleUtc = null;
        _routeTrafficBaselines.Clear();
        foreach (var link in new[] { SelectedEthernet, SelectedWifi }.OfType<LinkInfo>())
        { link.DownloadMbps = 0; link.UploadMbps = 0; }
        OnPropertyChanged(nameof(TrafficScopeText));
        OnPropertyChanged(nameof(TrafficHistoryToolTip));
        OnPropertyChanged(nameof(EthernetGraphPoints));
        OnPropertyChanged(nameof(WifiGraphPoints));
    }

    private void LoadProfilesAndSettings()
    {
        _loadingSettings = true;
        try
        {
            if (!_previewMode && File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_settingsPath)) ?? new UserSettings();
        }
        catch { _settings = new UserSettings(); }

        _settings.SelectedProfiles ??= new();
        _settings.CustomProfiles = (_settings.CustomProfiles ?? new()).Where(x => x is not null &&
            !string.IsNullOrWhiteSpace(x.Name) && x.Processes is not null && x.ExecutablePaths is not null &&
            (x.Processes.Count > 0 || x.ExecutablePaths.Count > 0)).ToList();

        AutoBoost = _settings.AutoBoost || !File.Exists(_settingsPath);
        CloseToTray = _settings.CloseToTray;
        // An old crash may leave Armed=true in settings. V4 never raises an
        // administrator prompt from background startup; the user explicitly
        // enables each controller session.
        _armed = false;
        SelectedRoutingModeOption = RoutingModeOptions.FirstOrDefault(x => x.Mode == _settings.RoutingMode) ?? RoutingModeOptions[0];
        SelectedUpdateChannelOption = UpdateChannelOptions.FirstOrDefault(x => x.Channel == _settings.UpdateChannel) ?? UpdateChannelOptions[0];
        foreach (var profile in _settings.CustomProfiles)
        {
            profile.IsSelected = _settings.SelectedProfiles.Contains(profile.Name, StringComparer.OrdinalIgnoreCase);
            Profiles.Add(profile);
        }
        _loadingSettings = false;
    }

    public bool HasNoApps => Profiles.Count == 0 && !_scanningInstalledApps;
    public string AppScanText => _scanningInstalledApps ? "Scanning…" : SelectedAppsText;

    private async Task RefreshInstalledAppsAsync()
    {
        if (_scanningInstalledApps || _previewMode || _exitRequested) return;
        _scanningInstalledApps = true;
        OnPropertyChanged(nameof(AppScanText));
        OnPropertyChanged(nameof(HasNoApps));
        var existing = Profiles.ToArray();
        var selections = existing.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Any(app => app.IsSelected), StringComparer.OrdinalIgnoreCase);
        try
        {
            var discovered = await Task.Run(() =>
            {
                var apps = InstalledAppDiscovery.Scan();
                foreach (var app in apps) app.Icon = AppIconLoader.Load(app.ExecutablePaths[0]);
                var browser = BrowserDiscovery.FindDefaultBrowser();
                return (Apps: apps, DefaultBrowser: browser?.ExecutablePath);
            });
            if (_exitRequested) return;
            var updated = new List<AppProfile>();
            foreach (var app in discovered.Apps)
            {
                app.IsSelected = selections.TryGetValue(app.Name, out var selected) ? selected :
                    _settings.SelectedProfiles.Contains(app.Name, StringComparer.OrdinalIgnoreCase) ||
                    _settings.SelectedProfiles.Contains("Default browser", StringComparer.OrdinalIgnoreCase) &&
                    app.ExecutablePaths.Contains(discovered.DefaultBrowser ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                updated.Add(app);
            }
            foreach (var app in existing.Where(x => x.IsCustom || _boosting && x.IsSelected))
            {
                var duplicate = updated.FirstOrDefault(x => !app.IsCustom && x.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase) ||
                    x.ExecutablePaths.Intersect(app.ExecutablePaths, StringComparer.OrdinalIgnoreCase).Any());
                if (duplicate is null) updated.Add(app);
                else if (_boosting && app.IsSelected)
                {
                    // Preserve the exact targets acknowledged by the active helper.
                    updated.Remove(duplicate);
                    updated.Add(app);
                }
                else duplicate.IsSelected |= app.IsSelected;
            }
            var icons = await Task.Run(() => updated.Where(x => x.Icon is null && x.ExecutablePaths.Count > 0)
                .Select(x => (App: x, Icon: AppIconLoader.Load(x.ExecutablePaths[0]))).ToArray());
            Profiles.Clear();
            foreach (var app in updated) Profiles.Add(app);
            foreach (var (app, icon) in icons) app.Icon = icon;
            OnPropertyChanged(nameof(SelectedAppsText));
            OnPropertyChanged(nameof(AppScanText));
        }
        catch (Exception ex) { Log($"Installed app scan failed: {ex.Message}"); ActionHint = "Scan failed · try again"; }
        finally
        {
            _scanningInstalledApps = false;
            OnPropertyChanged(nameof(AppScanText));
            OnPropertyChanged(nameof(HasNoApps));
        }
    }

    private async void ScanApps_Click(object sender, RoutedEventArgs e)
    {
        await _controllerGate.WaitAsync();
        try { await RefreshInstalledAppsAsync(); await UpdateRunningProfilesAsync(); }
        finally { _controllerGate.Release(); }
    }

    private async Task RefreshAdaptersAsync(bool logDiscovery = true)
    {
        var ethernetId = _settings.EthernetId ?? SelectedEthernet?.Id;
        var wifiId = _settings.WifiId ?? SelectedWifi?.Id;
        var ethernetControl = SelectedEthernet?.RouteControlMbps ?? SavedRouteControl(_settings.EthernetWeight, _settings.EthernetSpeedLimitMbps);
        var wifiControl = SelectedWifi?.RouteControlMbps ?? SavedRouteControl(_settings.WifiWeight, _settings.WifiSpeedLimitMbps);
        var discovered = await Task.Run(NetworkDiscovery.FindInternetLinks);
        if (_exitRequested) return;
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
            OnPropertyChanged(nameof(EthernetLinkState));
            OnPropertyChanged(nameof(WifiLinkState));
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

    private void LoadPreviewProfiles()
    {
        Profiles.Clear();
        var profiles = new[]
        {
            new AppProfile { Name = "Default browser", Subtitle = "Your Windows browser", Accent = "#A78BFA", Processes = new() { "Browser.exe" }, IsSystemDetected = true, IsSelected = true },
            new AppProfile { Name = "Game launcher", Subtitle = "Game downloads and updates", Accent = "#49B8FF", Processes = new() { "GameLauncher.exe" } },
            new AppProfile { Name = "Download manager", Subtitle = "Parallel file downloads", Accent = "#66C0F4", Processes = new() { "DownloadManager.exe" }, IsSelected = true },
            new AppProfile { Name = "Desktop client", Subtitle = "Application downloads and updates", Accent = "#FF4655", Processes = new() { "DesktopClient.exe" } },
            new AppProfile { Name = "Media library", Subtitle = "Media downloads and syncing", Accent = "#148EFF", Processes = new() { "MediaLibrary.exe" } },
            new AppProfile { Name = "Custom application", Subtitle = "An executable selected by the user", Accent = "#7AC943", Processes = new() { "CustomApplication.exe" }, IsCustom = true }
        };
        foreach (var profile in profiles) Profiles.Add(profile);
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
            if (_boosting)
            {
                await RefreshServiceStatusAsync();
                await VerifyBoostHealthAsync();
                UpdateBoostRates(elapsedSeconds);
            }
            else
            {
                var links = EthernetLinks.Concat(WifiLinks).ToArray();
                var ids = links.Select(x => x.Id).ToArray();
                var rates = await Task.Run(() => NetworkDiscovery.CaptureRates(ids));
                ApplyDeviceRates(links, rates, elapsedSeconds);
            }
            RecordTrafficSample();
            OnPropertyChanged(nameof(ActiveConnections));
            OnPropertyChanged(nameof(CombinedSpeedText));
            OnPropertyChanged(nameof(CombinedUploadSpeedText));
            OnPropertyChanged(nameof(RouteHealthText));
            OnPropertyChanged(nameof(CompatibilityGuardText));
            OnPropertyChanged(nameof(EthernetQualityText));
            OnPropertyChanged(nameof(WifiQualityText));
            RefreshBoostContributionProperties();
            OnPropertyChanged(nameof(SelectedAppsText));
            if (now >= _nextProcessScanUtc)
            {
                await UpdateRunningProfilesAsync();
                _nextProcessScanUtc = now.AddSeconds(_boosting ? 3 : IsVisible ? 4 : 10);
            }

            var selected = Profiles.Where(x => x.IsSelected).ToList();
            var shouldBoost = _armed && selected.Count > 0 && (!AutoBoost || selected.Any(x => x.IsRunning) || _boosting && ActiveConnections > 0);
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
                StatusText = shouldBoost && retrySeconds > 0 ? $"Retry in {retrySeconds}s" : "Waiting for app";
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 184, 77));
                UpdateTray();
            }
            _timer.Interval = TimeSpan.FromSeconds(IsVisible || _boosting ? 1 : 5);
            UpdateTray();
        }
        catch (Exception ex)
        {
            Log($"Controller error: {ex.Message}");
            if (_serviceClient is { IsConnected: false })
            {
                _armed = false;
                UpdateButton();
                ActionHint = "Try Stop again";
            }
            try
            {
                await StopBoostAsync("Safety stop");
                if (_armed) ScheduleStartRetry();
            }
            catch (Exception restoreError)
            {
                Log($"Automatic restore needs attention: {restoreError.Message}");
                StatusText = "Couldn't stop";
                ActionHint = "Try Stop again";
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

    private void ApplyDeviceRates(LinkInfo[] links, IReadOnlyDictionary<string, (long Received, long Sent)> rates, double elapsedSeconds)
    {
        if (_resetDeviceRateBaseline)
        {
            foreach (var link in links)
                if (rates.TryGetValue(link.Id, out var sample))
                {
                    link.LastReceivedBytes = sample.Received;
                    link.LastSentBytes = sample.Sent;
                }
            _resetDeviceRateBaseline = false;
        }
        NetworkDiscovery.ApplyRates(links, rates, elapsedSeconds);
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
            var y = 26d - Math.Clamp(selector(samples[i]) / maximum, 0d, 1d) * 24d;
            points.Add(new System.Windows.Point(x, y));
        }
        return points;
    }

    private async Task VerifyBoostHealthAsync()
    {
        if (_serviceStatus is null) throw new DualLinkServiceException("The helper disconnected.");
        switch (_sessionRecovery.Evaluate(_serviceStatus))
        {
            case RecoveryAction.None:
                UpdateActiveRouteStatus();
                break;
            case RecoveryAction.Wait:
                StatusText = "Reconnecting";
                StatusColor = (Brush)FindResource("WarningBrush");
                break;
            case RecoveryAction.Restart:
                SetBoosting(false);
                ScheduleStartRetry();
                break;
            case RecoveryAction.Restore:
                await StopBoostAsync("Helper session ended; routing restored");
                if (_armed) ScheduleStartRetry();
                break;
        }
    }

    private async Task RefreshServiceStatusAsync()
    {
        if (_serviceClient is null) return;
        _serviceStatus = await _serviceClient.GetStatusAsync();
        OnPropertyChanged(nameof(ActiveConnections));
        OnPropertyChanged(nameof(RouteHealthText));
        OnPropertyChanged(nameof(CompatibilityGuardText));
        OnPropertyChanged(nameof(EthernetQualityText));
        OnPropertyChanged(nameof(WifiQualityText));
        RefreshBoostContributionProperties();
        OnPropertyChanged(nameof(RecoveryText));
    }

    private async Task UpdateRunningProfilesAsync()
    {
        if (Interlocked.Exchange(ref _processScanInProgress, 1) != 0) return;
        try
        {
            var pathProcessNames = Profiles.SelectMany(x => x.ExecutablePaths.Concat(x.Processes))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var snapshot = await Task.Run(() => CaptureRunningProcesses(pathProcessNames));
            foreach (var profile in Profiles)
                profile.IsRunning = ApplicationProfileDiscovery.IsRunning(profile, snapshot.Names, snapshot.Paths, snapshot.UnreadableNames);
            var iconRequests = Profiles.Where(x => x.Icon is null).Select(profile =>
                (Profile: profile, Path: profile.ExecutablePaths.FirstOrDefault(File.Exists) ??
                    profile.Processes.Select(name => snapshot.Executables.GetValueOrDefault(name)).FirstOrDefault(x => x is not null)))
                .Where(x => x.Path is not null).ToArray();
            var icons = await Task.Run(() => iconRequests.Select(x => (x.Profile, Icon: AppIconLoader.Load(x.Path!))).ToArray());
            foreach (var (profile, icon) in icons) profile.Icon = icon;
        }
        catch { }
        finally { Interlocked.Exchange(ref _processScanInProgress, 0); }
    }

    private static RunningProcessSnapshot CaptureRunningProcesses(IReadOnlySet<string> pathProcessNames)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                string processName;
                try { processName = process.ProcessName + ".exe"; }
                catch { continue; }
                names.Add(processName);
                if (!pathProcessNames.Contains(processName)) continue;
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(Path.GetFullPath(path));
                        executables.TryAdd(processName, Path.GetFullPath(path));
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) { unreadableNames.Add(processName); }
                catch { }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return new RunningProcessSnapshot(names, paths, executables, unreadableNames);
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
        try
        {
            var processMatchers = selected.SelectMany(x => x.ProcessMatchers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var service = await EnsureServiceClientAsync();
            _serviceStatus = await service.StartAsync(new ServiceProtocol.StartSessionRequest(
                ToServiceRoutes(routes),
                ToServiceMode(SelectedRoutingModeOption?.Mode ?? RoutingMode.Smart),
                processMatchers,
                0,
                _proxyCredentials.Username,
                _proxyCredentials.Password));
            if (!_serviceStatus.IsRunning || !_serviceStatus.FilterRunning)
                throw new DualLinkServiceException("The helper did not confirm a running session.");
            SetBoosting(true);
            ResetBoostRateBaselines();
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
            if (_serviceClient is not null)
            {
                try { _serviceStatus = await _serviceClient.StopAsync(); }
                catch { }
            }
            throw;
        }
    }

    private async Task StopBoostAsync(string reason)
    {
        if (_boosting)
        {
            var confirmedStopped = false;
            Exception? stopError = null;
            try
            {
                if (_serviceClient is not null)
                {
                    _serviceStatus = await _serviceClient.StopAsync();
                    confirmedStopped = !_serviceStatus.IsRunning &&
                        !_serviceStatus.FilterRunning &&
                        string.IsNullOrWhiteSpace(_serviceStatus.Failure);
                }
            }
            catch (Exception ex)
            {
                stopError = ex;
                try
                {
                    if (_serviceClient is not null)
                    {
                        _serviceStatus = await _serviceClient.GetStatusAsync();
                        confirmedStopped = !_serviceStatus.IsRunning &&
                            !_serviceStatus.FilterRunning &&
                            string.IsNullOrWhiteSpace(_serviceStatus.Failure);
                    }
                }
                catch { }
            }
            if (!confirmedStopped)
            {
                StatusText = "Stopping…";
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 184, 77));
                UpdateTray();
                throw new DualLinkServiceException(
                    "DualLink could not yet confirm that normal routing was restored. The local helper and watchdog are still retrying.",
                    stopError ?? new InvalidOperationException("No stop confirmation was received."));
            }

            SetBoosting(false);
            _routeTrafficBaselines.Clear();
            OnPropertyChanged(nameof(TrafficScopeText));
            OnPropertyChanged(nameof(TrafficHistoryToolTip));
            RefreshBoostContributionProperties();
            Log(reason);
        }
        StatusText = _armed ? "Waiting for app" : "Off";
        StatusColor = new SolidColorBrush(_armed ? Color.FromRgb(255, 184, 77) : Color.FromRgb(140, 150, 165));
        UpdateTray();
    }

    private async Task<DualLinkServiceClient> EnsureServiceClientAsync()
    {
        if (_serviceClient is not null)
        {
            try
            {
                _serviceStatus = await _serviceClient.GetStatusAsync();
                return _serviceClient;
            }
            catch
            {
                await _serviceClient.DisposeAsync();
                _serviceClient = null;
                _serviceStatus = null;
            }
        }

        var helper = Path.Combine(AppContext.BaseDirectory, "DualLink.Service.exe");
        StatusText = "Permission required…";
        _serviceClient = await DualLinkServiceClient.StartElevatedAsync(helper, TimeSpan.FromSeconds(25));
        _serviceStatus = await _serviceClient.GetStatusAsync();
        return _serviceClient;
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
            _settingsWriteTask = PersistSettingsAsync(serialized);
        }
        catch (Exception ex) { Log($"Settings failed: {ex.Message}"); }
    }

    private async Task PersistSettingsAsync(string serialized)
    {
        await _settingsWriteGate.WaitAsync();
        var temporaryPath = _settingsPath + ".new";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, serialized);
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Settings could not be saved: {ex.Message}");
            ActionHint = "Settings couldn't save";
        }
        finally { _settingsWriteGate.Release(); }
    }

    private void Log(string message)
    {
        if (!_previewMode) _ = Task.Run(() => _diagnosticLog?.Write(message));
        Dispatcher.Invoke(() =>
        {
            Activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (Activity.Count > 80) Activity.RemoveAt(Activity.Count - 1);
        });
    }

    private void UpdateButton()
    {
        if (BoostButton is null) return;
        var active = _armed || _boosting;
        BoostButton.Content = active ? "Stop" : "Start";
        BoostButton.IsEnabled = !IsBusy;
        System.Windows.Automation.AutomationProperties.SetName(BoostButton, BoostButton.Content.ToString());
        BoostButton.Tag = active ? null : "Primary";
        BoostButton.Background = new SolidColorBrush(active ? Color.FromRgb(72, 73, 82) : Color.FromRgb(125, 143, 255));
        BoostButton.BorderBrush = new SolidColorBrush(active ? Color.FromRgb(92, 93, 104) : Color.FromRgb(125, 143, 255));
        BoostButton.Foreground = new SolidColorBrush(active ? Color.FromRgb(245, 245, 247) : Color.FromRgb(16, 17, 22));
        UpdateTray();
    }

    private void UpdateTray()
    {
        _tray?.Update(new TraySnapshot(
            _armed,
            _boosting,
            (SelectedEthernet?.DownloadMbps ?? 0) + (SelectedWifi?.DownloadMbps ?? 0),
            (SelectedEthernet?.UploadMbps ?? 0) + (SelectedWifi?.UploadMbps ?? 0),
            ActiveConnections,
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

    private async void RefreshAdapters_Click(object sender, RoutedEventArgs e)
    {
        if (_previewMode || _exitRequested) return;
        await _controllerGate.WaitAsync();
        try
        {
            await RefreshAdaptersAsync();
            if (_boosting && SelectedEthernet is not { Weight: > 0 } && SelectedWifi is not { Weight: > 0 })
            {
                await StopBoostAsync("No links available");
                if (_armed) ScheduleStartRetry();
                return;
            }
            await ApplyRouteMixCoreAsync();
        }
        catch (Exception ex) { Log($"Refresh failed: {ex.Message}"); ActionHint = "Refresh failed · try again"; }
        finally { _controllerGate.Release(); }
    }
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
        ApplyRouteMix();
    }
    private async void ProfileSelection_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_boosting) await ApplySelectionChangeAsync();
    }
    private async void AutoBoost_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_armed) await ApplySelectionChangeAsync();
    }

    private async Task ApplySelectionChangeAsync()
    {
        await _controllerGate.WaitAsync();
        try
        {
            await UpdateRunningProfilesAsync();
            var selected = Profiles.Where(x => x.IsSelected).ToList();
            var shouldBoost = _armed && selected.Count > 0 && (!AutoBoost || selected.Any(x => x.IsRunning) || _boosting && ActiveConnections > 0);
            if (_boosting && shouldBoost)
            {
                StatusText = "Updating apps…";
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 184, 77));
                UpdateTray();
                var processMatchers = selected.SelectMany(x => x.ProcessMatchers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (_serviceClient is null || _serviceStatus is null)
                    throw new InvalidOperationException("The local network component is not connected.");
                if (_serviceStatus.ProcessMatchers is null || !_serviceStatus.ProcessMatchers.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(processMatchers))
                    _serviceStatus = await _serviceClient.UpdateTargetsAsync(new ServiceProtocol.UpdateTargetsRequest(
                        processMatchers, _serviceStatus.BoundPort, _proxyCredentials.Username, _proxyCredentials.Password));
                ActionHint = string.Empty;
                OnPropertyChanged(nameof(SelectedAppsText));
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
            if (_serviceStatus?.ProcessMatchers is { } confirmed)
            {
                var matchers = confirmed.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var profile in Profiles) profile.IsSelected = profile.ProcessMatchers.Any(matchers.Contains);
                SaveSettings();
                OnPropertyChanged(nameof(SelectedAppsText));
            }
            ActionHint = "App change failed · try again";
            try { await StopBoostAsync("Safety stop"); }
            catch (Exception restoreError)
            {
                Log($"Restore needs attention: {restoreError.Message}");
                StatusText = "Couldn't stop";
                ActionHint = "Try Stop again";
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
        if (IsBusy || _exitRequested || _previewMode) return;
        IsBusy = true;
        await _controllerGate.WaitAsync();
        try
        {
            ActionHint = string.Empty;
            if (!_armed && !_boosting && !Profiles.Any(x => x.IsSelected))
            {
                ActionHint = "Pick an app";
                return;
            }
            if (!_armed && _boosting)
            {
                await EnsureServiceClientAsync();
                await StopBoostAsync("Stopped by user");
                return;
            }
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
                    StatusText = "Couldn't stop";
                    ActionHint = "Try Stop again";
                    StatusColor = new SolidColorBrush(Color.FromRgb(240, 108, 123));
                    UpdateTray();
                }
            }
            else
            {
                await UpdateRunningProfilesAsync();
                var selected = Profiles.Where(x => x.IsSelected).ToArray();
                if (!AutoBoost || selected.Any(x => x.IsRunning)) await StartBoostAsync(selected);
                else StatusText = "Waiting for app";
            }
        }
        catch (Exception ex)
        {
            Log($"Start failed: {ex.Message}");
            _armed = false;
            StatusText = "Couldn't start";
            StatusColor = (Brush)FindResource("DangerBrush");
            ActionHint = PrerequisiteText == "Needs setup" ? "Run setup again" : "Try Start again";
        }
        finally { _controllerGate.Release(); IsBusy = false; UpdateButton(); }
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
        if (disabled.RouteControlMbps > 0) _enabledRouteLimits[disabled.Id] = disabled.RouteControlMbps;
        if (enabled.Weight == 0) enabled.RouteControlMbps = _enabledRouteLimits.GetValueOrDefault(enabled.Id, LinkInfo.FullSpeedControlMbps);
        disabled.Weight = 0;
        ApplyRouteMix();
    }

    private void BothLinks_Click(object sender, RoutedEventArgs e)
    {
        foreach (var link in new[] { SelectedEthernet, SelectedWifi }.OfType<LinkInfo>())
            if (link.Weight == 0) link.RouteControlMbps = _enabledRouteLimits.GetValueOrDefault(link.Id, LinkInfo.FullSpeedControlMbps);
        ApplyRouteMix();
    }

    private async void ApplyRouteMix()
    {
        if (_loadingSettings || _previewMode || _exitRequested) return;
        var version = ++_routeUpdateVersion;
        // Coalesce rapid picker changes before taking the lifecycle gate.
        await Task.Delay(120);
        await _controllerGate.WaitAsync();
        try
        {
            if (version != _routeUpdateVersion || _exitRequested) return;
            await ApplyRouteMixCoreAsync();
            SaveSettings();
        }
        catch (Exception ex)
        {
            Log($"Connection update failed: {ex.Message}");
            RestoreConfirmedPolicy();
            ActionHint = "Change failed · try again";
        }
        finally { _controllerGate.Release(); }
    }

    private async Task ApplyRouteMixCoreAsync()
    {
        if (!RouterIsRunning || _serviceClient is null) return;
        var routes = BuildRouteDefinitions();
        _serviceStatus = await _serviceClient.UpdateRoutesAsync(new ServiceProtocol.UpdateRoutesRequest(
            ToServiceRoutes(routes), ToServiceMode(SelectedRoutingModeOption?.Mode ?? RoutingMode.Balanced)));
        ActionHint = string.Empty;
        await VerifyBoostHealthAsync();
        RefreshBoostContributionProperties();
        OnPropertyChanged(nameof(CompatibilityGuardText));
    }

    private void RestoreConfirmedPolicy()
    {
        if (_serviceStatus is null) return;
        _loadingSettings = true;
        try
        {
            var active = _serviceStatus.Routes.Where(x => x.AcceptingNewConnections).ToArray();
            _selectedEthernet = EthernetLinks.FirstOrDefault(x => active.Any(r => r.Address == x.Address)) ?? _selectedEthernet;
            _selectedWifi = WifiLinks.FirstOrDefault(x => active.Any(r => r.Address == x.Address)) ?? _selectedWifi;
            foreach (var link in new[] { SelectedEthernet, SelectedWifi }.OfType<LinkInfo>())
            {
                var confirmed = active.FirstOrDefault(x => x.Address == link.Address);
                link.RouteControlMbps = confirmed.Address is null ? 0 : confirmed.SpeedLimitMbps == 0
                    ? LinkInfo.FullSpeedControlMbps : confirmed.SpeedLimitMbps;
            }
            SelectedRoutingModeOption = RoutingModeOptions.First(x => (int)x.Mode == (int)_serviceStatus.Mode);
            OnPropertyChanged(nameof(SelectedEthernet));
            OnPropertyChanged(nameof(SelectedWifi));
        }
        finally { _loadingSettings = false; }
        SaveSettings();
    }

    private RouteDefinition[] BuildRouteDefinitions()
    {
        var routes = new List<RouteDefinition>(2);
        if (SelectedEthernet is { Weight: > 0 } ethernet)
            routes.Add(new RouteDefinition(ethernet.Address, 1, true, "Ethernet", ethernet.SpeedLimitMbps));
        if (SelectedWifi is { Weight: > 0 } wifi)
            routes.Add(new RouteDefinition(wifi.Address, 1, routes.Count == 0, "Wi-Fi", wifi.SpeedLimitMbps));
        if (routes.Count == 0)
            throw new InvalidOperationException("Keep at least one connected route enabled.");
        return routes.ToArray();
    }

    private static ServiceProtocol.RouteDefinition[] ToServiceRoutes(IEnumerable<RouteDefinition> routes) =>
        routes.Select(route => new ServiceProtocol.RouteDefinition(
            route.Address,
            route.Weight,
            route.IsPrimary,
            route.Name,
            route.SpeedLimitMbps)).ToArray();

    private static ServiceProtocol.RoutingMode ToServiceMode(RoutingMode mode) => mode switch
    {
        RoutingMode.Balanced => ServiceProtocol.RoutingMode.Balanced,
        RoutingMode.Failover => ServiceProtocol.RoutingMode.Failover,
        _ => ServiceProtocol.RoutingMode.Smart
    };

    private string GetQualityText(LinkInfo? link)
    {
        if (link is null) return "Disconnected";
        var status = CurrentRouteStatuses.FirstOrDefault(x => x.Address.Equals(link.Address, StringComparison.OrdinalIgnoreCase));
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
        var status = CurrentRouteStatuses.FirstOrDefault(x => x.Address.Equals(link.Address, StringComparison.OrdinalIgnoreCase));
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
        OnPropertyChanged(nameof(EthernetLinkState));
        OnPropertyChanged(nameof(WifiLinkState));
        OnPropertyChanged(nameof(EthernetUsageText));
        OnPropertyChanged(nameof(WifiUsageText));
        OnPropertyChanged(nameof(SessionTrafficText));
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
            await RefreshAdaptersAsync(logDiscovery: false);
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
            if (!RouterIsRunning || _serviceClient is null) return;
            try
            {
                var routes = BuildRouteDefinitions();
                _serviceStatus = await _serviceClient.UpdateRoutesAsync(new ServiceProtocol.UpdateRoutesRequest(
                    ToServiceRoutes(routes),
                    ToServiceMode(SelectedRoutingModeOption?.Mode ?? RoutingMode.Smart)));
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
        var ethernetEnabled = SelectedEthernet is { Weight: > 0 } &&
            CurrentRouteStatuses.Any(x => x.Address == SelectedEthernet.Address && x.AcceptingNewConnections && x.IsHealthy);
        var wifiEnabled = SelectedWifi is { Weight: > 0 } &&
            CurrentRouteStatuses.Any(x => x.Address == SelectedWifi.Address && x.AcceptingNewConnections && x.IsHealthy);
        StatusText = ethernetEnabled && !wifiEnabled ? "Ethernet only" :
            wifiEnabled && !ethernetEnabled ? "Wi-Fi only" :
            !ethernetEnabled && !wifiEnabled ? "Reconnecting" : ActiveConnections == 0 ? "Waiting for traffic" : "On";
        StatusColor = (Brush)FindResource(!ethernetEnabled && !wifiEnabled ? "WarningBrush" : "SuccessBrush");
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
        foreach (var status in CurrentRouteStatuses)
            _routeTrafficBaselines[status.Address] = new RouteTrafficBaseline(status.DownloadedBytes, status.UploadedBytes);
    }

    private void UpdateBoostRates(double elapsedSeconds)
    {
        var sampledAt = _serviceStatus?.SampledAtUtc;
        if (sampledAt is not null && _lastServiceSampleUtc is not null)
            elapsedSeconds = Math.Max(0.001, (sampledAt.Value - _lastServiceSampleUtc.Value).TotalSeconds);
        _lastServiceSampleUtc = sampledAt;
        var statuses = CurrentRouteStatuses.ToDictionary(x => x.Address, StringComparer.OrdinalIgnoreCase);
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
        ToggleDrawer(DetailsDrawer);
    }

    private void Activity_Click(object sender, RoutedEventArgs e)
    {
        var showActivity = ActivityPanel.Visibility != Visibility.Visible;
        ActivityPanel.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticPanel.Visibility = showActivity ? Visibility.Collapsed : Visibility.Visible;
        ActivityToggleButton.Content = showActivity ? "Results" : "Log";
    }

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        _diagnosticsCts?.Cancel();
        _diagnosticsCts?.Dispose();
        _diagnosticsCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = _diagnosticsCts.Token;
        Diagnostics.Clear();
        DiagnosticsSummaryText = "Checking…";
        try
        {
            var prerequisite = await _proxiFyre.CheckPrerequisitesAsync();
            Diagnostics.Add(new ConnectionCheckResult(
                "Filter",
                prerequisite.Installed ? "Ready" : "Run setup again",
                prerequisite.Installed ? DiagnosticState.Good : DiagnosticState.Problem));

            var linksAreIndependent = SelectedEthernet is not null && SelectedWifi is not null &&
                !SelectedEthernet.Id.Equals(SelectedWifi.Id, StringComparison.OrdinalIgnoreCase) &&
                !SelectedEthernet.Address.Equals(SelectedWifi.Address, StringComparison.OrdinalIgnoreCase);
            Diagnostics.Add(new ConnectionCheckResult(
                "Links",
                linksAreIndependent ? "2 connected" : "Connect a second link",
                linksAreIndependent ? DiagnosticState.Good : DiagnosticState.Notice));

            var connectivity = await Task.WhenAll(
                NetworkDiscovery.CheckConnectivityAsync(SelectedEthernet, token),
                NetworkDiscovery.CheckConnectivityAsync(SelectedWifi, token),
                NetworkDiscovery.CheckDnsAsync(token));
            foreach (var check in connectivity) Diagnostics.Add(check);

            if (_boosting)
            {
                var routeStatuses = CurrentRouteStatuses;
                var enabledRouteCount = routeStatuses.Count(x => x.AcceptingNewConnections);
                var contributing = routeStatuses.Where(x => x.SuccessfulConnections > 0).ToArray();
                var routeMessage = enabledRouteCount == 1 && contributing.Length == 1
                    ? $"{contributing[0].Name} used"
                    : contributing.Length >= 2
                        ? "Both links used"
                        : contributing.Length == 1
                            ? $"{contributing[0].Name} used"
                            : "No app traffic yet";
                var routeState = (enabledRouteCount == 1 && contributing.Length == 1) || contributing.Length >= 2
                    ? DiagnosticState.Good
                    : DiagnosticState.Notice;
                Diagnostics.Add(new ConnectionCheckResult(
                    "App traffic",
                    routeMessage,
                    routeState));
            }
            else
            {
                Diagnostics.Add(new ConnectionCheckResult("App traffic", "Off", DiagnosticState.Notice));
            }

            var problems = Diagnostics.Count(x => x.State == DiagnosticState.Problem);
            var notices = Diagnostics.Count(x => x.State == DiagnosticState.Notice);
            DiagnosticsSummaryText = problems > 0
                ? $"{problems} issue{(problems == 1 ? string.Empty : "s")}" : notices > 0 ? "Check results" : "Ready";
        }
        catch (OperationCanceledException)
        {
            DiagnosticsSummaryText = "Cancelled";
        }
        catch (Exception ex)
        {
            DiagnosticsSummaryText = "Couldn't check";
            Log($"Check failed: {ex.Message}");
            Diagnostics.Add(new ConnectionCheckResult("Check", "Try again", DiagnosticState.Problem));
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
        UpdateStatusText = "Checking…";
        try
        {
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _updateCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await UpdateChecker.CheckAsync(
                SelectedUpdateChannelOption?.Channel ?? UpdateChannel.Stable,
                _updateCts.Token);
            UpdateStatusText = result.IsAvailable ? "Update ready" : "No updates";
            _availableUpdate = result.IsAvailable ? result : null;
            OnPropertyChanged(nameof(UpdateActionText));
        }
        catch
        {
            UpdateStatusText = "Couldn't check · try again";
        }
        finally { UpdateCheckButton.IsEnabled = true; }
    }

    private async Task InstallAvailableUpdateAsync(UpdateCheckResult update)
    {
        var answer = MessageBox.Show(
            $"Install {update.Version}? DualLink will close.",
            "Update",
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
            UpdateStatusText = "Installing…";

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
                "Update failed. Try again.",
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
        ToggleDrawer(SettingsDrawer);
    }

    public void ShowSettingsPreview() => SettingsDrawer.Visibility = Visibility.Visible;
    public void ShowLimitsPreview() { ShowSettingsPreview(); SpeedLimits.IsExpanded = true; }
    public async Task ShowInstalledAppsPreviewAsync()
    {
        var apps = await Task.Run(() =>
        {
            var found = InstalledAppDiscovery.Scan();
            foreach (var app in found) app.Icon = AppIconLoader.Load(app.ExecutablePaths[0]);
            return found;
        });
        Profiles.Clear();
        foreach (var app in apps) Profiles.Add(app);
        await UpdateRunningProfilesAsync();
    }
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
        CloseDrawers();
    }

    private System.Windows.IInputElement? _drawerPreviousFocus;
    private System.Windows.Controls.Border[] Drawers() => [DetailsDrawer, SettingsDrawer, AddAppDrawer, WifiNetworksDrawer];
    private void CloseDrawers()
    {
        foreach (var drawer in Drawers()) drawer.Visibility = Visibility.Collapsed;
        if (_drawerPreviousFocus is not null) System.Windows.Input.Keyboard.Focus(_drawerPreviousFocus);
    }
    private void OpenDrawer(System.Windows.Controls.Border drawer)
    {
        if (!Drawers().Any(x => x.Visibility == Visibility.Visible)) _drawerPreviousFocus = System.Windows.Input.Keyboard.FocusedElement;
        foreach (var other in Drawers()) other.Visibility = Visibility.Collapsed;
        drawer.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => drawer.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First)));
    }
    private void ToggleDrawer(System.Windows.Controls.Border drawer)
    {
        if (drawer.Visibility == Visibility.Visible) CloseDrawers();
        else OpenDrawer(drawer);
    }
    private void DrawerBackdrop_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => CloseDrawers();

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

    private async void AddExecutable_Click(object sender, RoutedEventArgs e)
    {
        OpenDrawer(AddAppDrawer);
        await RefreshRunningApplicationsAsync();
    }

    private async void WifiNetworks_Click(object sender, RoutedEventArgs e)
    {
        OpenDrawer(WifiNetworksDrawer);
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
            WifiNetworksStatusText = "Enter password in Windows";
            return;
        }

        _wifiConnectCts?.Cancel();
        _wifiConnectCts?.Dispose();
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
            await _controllerGate.WaitAsync();
            try { await RefreshAdaptersAsync(logDiscovery: false); await ApplyRouteMixCoreAsync(); }
            finally { _controllerGate.Release(); }
            await RefreshWifiNetworksAsync();
            Log($"Wi-Fi changed to {network.Name}");
        }
        catch (OperationCanceledException)
        {
            WifiNetworksStatusText = "Wi-Fi connection timed out";
        }
        catch (Exception ex) { WifiNetworksStatusText = "Couldn't connect"; Log($"Wi-Fi failed: {ex.Message}"); }
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
            CloseDrawers();
            return;
        }

        var discovered = await Task.Run(() => (Paths: ApplicationProfileDiscovery.ExpandExecutablePaths(executablePath),
            Icon: AppIconLoader.Load(executablePath), Name: GetExecutableDisplayName(executablePath)));
        var executablePaths = discovered.Paths;
        var profile = new AppProfile
        {
            Name = discovered.Name,
            Subtitle = executablePaths.Count > 1 ? "Application and its download engine" : "Custom application",
            Accent = "#B6C2D1",
            Processes = new List<string> { processName },
            ExecutablePaths = executablePaths,
            IsCustom = true,
            IsSelected = true,
            Icon = discovered.Icon
        };
        Profiles.Add(profile);
        SaveSettings();
        Log($"Added {processName}");
        CloseDrawers();
        if (_boosting) await ApplySelectionChangeAsync();
    }

    private async Task RefreshRunningApplicationsAsync()
    {
        if (_discoveringApps || _previewMode) return;
        _discoveringApps = true;
        OnPropertyChanged(nameof(RunningApplicationsStatusText));
        try
        {
            var applications = await Task.Run(DiscoverRunningApplications);
            if (_exitRequested) return;
            RunningApplications.Clear();
            foreach (var application in applications) RunningApplications.Add(application);
        }
        catch (Exception ex) { Log($"App scan failed: {ex.Message}"); }
        finally { _discoveringApps = false; OnPropertyChanged(nameof(RunningApplicationsStatusText)); }
    }

    private static RunningAppInfo[] DiscoverRunningApplications()
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

        return discovered.Values.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
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
        _updateCts?.Cancel();
        _updateCts?.Dispose();
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
                Log($"Foreground restore failed; the local helper will retry: {ex.Message}");
            }
            if (_serviceClient is not null)
            {
                await _serviceClient.DisposeAsync();
                _serviceClient = null;
                _serviceStatus = null;
            }
            SaveSettings();
            await _settingsWriteTask;
            _tray?.Dispose();
            _tray = null;
            _allowClose = true;
            Close();
        }
        finally { _controllerGate.Release(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(SelectedAppsText)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppScanText)));
    }

    private readonly record struct TrafficSample(DateTime TimestampUtc, double EthernetMbps, double WifiMbps);
    private sealed record RunningProcessSnapshot(HashSet<string> Names, HashSet<string> Paths,
        Dictionary<string, string> Executables, HashSet<string> UnreadableNames);
    private readonly record struct RouteTrafficBaseline(long DownloadedBytes, long UploadedBytes);
}
