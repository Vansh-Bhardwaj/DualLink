using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Reflection;
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
    private const int SocksPort = 18080;
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly ProxiFyreManager _proxiFyre;
    private readonly Socks5Balancer _balancer;
    private readonly BoostHealthMonitor _healthMonitor;
    private readonly DispatcherTimer _timer;
    private readonly bool _previewMode;
    private readonly SemaphoreSlim _controllerGate = new(1, 1);
    private TrayManager? _tray;
    private UserSettings _settings = new();
    private LinkInfo? _selectedEthernet;
    private LinkInfo? _selectedWifi;
    private BandwidthOption? _selectedBandwidthOption;
    private bool _autoBoost = true;
    private bool _armed;
    private bool _boosting;
    private DateTime _nextHealthCheckUtc = DateTime.MinValue;
    private bool _allowClose;
    private bool _exitRequested;
    private bool _closeToTray = true;
    private bool _loadingSettings;
    private string _statusText = "Ready";
    private Brush _statusColor = new SolidColorBrush(Color.FromRgb(140, 150, 165));
    private string _prerequisiteText = "Checking";

    public MainWindow(bool previewMode = false)
    {
        InitializeComponent();
        DataContext = this;
        _previewMode = previewMode;

        _settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink");
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        Directory.CreateDirectory(_settingsDirectory);
        _proxiFyre = new ProxiFyreManager(Log);
        _balancer = new Socks5Balancer(SocksPort, Log);
        _healthMonitor = new BoostHealthMonitor(
            () => _balancer.IsRunning,
            _proxiFyre.IsServiceRunningAsync,
            _proxiFyre.EnsureServiceRunningAsync);

        LoadProfilesAndSettings();
        RefreshAdapters();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        if (!_previewMode)
        {
            _tray = new TrayManager(
                () => Dispatcher.BeginInvoke(ShowFromTray),
                () => Dispatcher.BeginInvoke(async () => await ToggleArmedAsync()),
                () => Dispatcher.BeginInvoke(ExitFromTray));
            UpdateButton();
            StartWatchdog();
            _timer.Start();
            Loaded += async (_, _) => await RefreshPrerequisitesAsync();
        }
        else
        {
            PrerequisiteText = "Ready";
            UpdateRunningProfiles();
            StatusText = "Preview";
            StatusColor = new SolidColorBrush(Color.FromRgb(69, 198, 255));
        }
        Closing += MainWindow_Closing;
        Log(_previewMode ? "Read-only design preview" : "DualLink ready — normal routing is active");
    }

    public ObservableCollection<AppProfile> Profiles { get; } = new();
    public ObservableCollection<LinkInfo> EthernetLinks { get; } = new();
    public ObservableCollection<LinkInfo> WifiLinks { get; } = new();
    public ObservableCollection<string> Activity { get; } = new();
    public ObservableCollection<BandwidthOption> BandwidthOptions { get; } = new()
    {
        new BandwidthOption { Mbps = 0, DisplayName = "No limit" },
        new BandwidthOption { Mbps = 25, DisplayName = "25 Mbps" },
        new BandwidthOption { Mbps = 50, DisplayName = "50 Mbps" },
        new BandwidthOption { Mbps = 100, DisplayName = "100 Mbps" },
        new BandwidthOption { Mbps = 200, DisplayName = "200 Mbps" },
        new BandwidthOption { Mbps = 300, DisplayName = "300 Mbps" }
    };

    public LinkInfo? SelectedEthernet
    {
        get => _selectedEthernet;
        set { if (_selectedEthernet != value) { _selectedEthernet = value; OnPropertyChanged(); OnPropertyChanged(nameof(CombinedSpeedText)); OnPropertyChanged(nameof(CombinedUploadSpeedText)); SaveSettings(); } }
    }

    public LinkInfo? SelectedWifi
    {
        get => _selectedWifi;
        set { if (_selectedWifi != value) { _selectedWifi = value; OnPropertyChanged(); OnPropertyChanged(nameof(CombinedSpeedText)); OnPropertyChanged(nameof(CombinedUploadSpeedText)); SaveSettings(); } }
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

    public BandwidthOption? SelectedBandwidthOption
    {
        get => _selectedBandwidthOption;
        set
        {
            if (_selectedBandwidthOption == value) return;
            _selectedBandwidthOption = value;
            OnPropertyChanged();
            _balancer.SetDownloadLimit(value?.Mbps ?? 0);
            SaveSettings();
        }
    }

    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }
    public Brush StatusColor { get => _statusColor; private set { _statusColor = value; OnPropertyChanged(); } }
    public string PrerequisiteText { get => _prerequisiteText; private set { _prerequisiteText = value; OnPropertyChanged(); } }
    public int ActiveConnections => _balancer.ActiveConnections;
    public string CombinedSpeedText => $"{(SelectedEthernet?.DownloadMbps ?? 0) + (SelectedWifi?.DownloadMbps ?? 0):0.0} Mbps";
    public string CombinedUploadSpeedText => $"{(SelectedEthernet?.UploadMbps ?? 0) + (SelectedWifi?.UploadMbps ?? 0):0.0} Mbps";
    public string VersionText => $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0"}";

    private void LoadProfilesAndSettings()
    {
        _loadingSettings = true;
        try
        {
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_settingsPath)) ?? new UserSettings();
        }
        catch { _settings = new UserSettings(); }

        AutoBoost = _settings.AutoBoost || !File.Exists(_settingsPath);
        CloseToTray = _settings.CloseToTray;
        _armed = _settings.Armed;
        SelectedBandwidthOption = BandwidthOptions.FirstOrDefault(x => x.Mbps == _settings.DownloadLimitMbps) ?? BandwidthOptions[0];
        var defaults = new List<AppProfile>
        {
            new AppProfile { Name="Epic Games", Subtitle="Epic and EOS game downloads", Accent="#49B8FF", Processes=new(){"EpicGamesLauncher.exe","EpicOnlineServicesInstallHelper.exe"} },
            new AppProfile { Name="Steam", Subtitle="Steam library downloads and updates", Accent="#66C0F4", Processes=new(){"steam.exe"} },
            new AppProfile { Name="Riot Games", Subtitle="Riot client and Valorant updates", Accent="#FF4655", Processes=new(){"RiotClientServices.exe","RiotClientUx.exe"} },
            new AppProfile { Name="Battle.net", Subtitle="Blizzard downloads and Agent updates", Accent="#148EFF", Processes=new(){"Battle.net.exe","Agent.exe"} },
            new AppProfile { Name="EA app", Subtitle="EA downloads and background updater", Accent="#FF6A2A", Processes=new(){"EADesktop.exe","EABackgroundService.exe"} }
        };
        var browser = BrowserDiscovery.FindDefaultBrowser();
        if (browser is not null)
        {
            defaults.Insert(0, new AppProfile
            {
                Name = "Default browser",
                Subtitle = browser.DisplayName,
                Accent = "#A78BFA",
                Processes = new() { browser.ProcessName },
                IsSystemDetected = true
            });
        }
        foreach (var profile in defaults.Concat(_settings.CustomProfiles ?? new List<AppProfile>()))
        {
            profile.IsSelected = _settings.SelectedProfiles.Contains(profile.Name, StringComparer.OrdinalIgnoreCase);
            Profiles.Add(profile);
        }
        _loadingSettings = false;
    }

    private void RefreshAdapters()
    {
        var discovered = NetworkDiscovery.FindInternetLinks();
        EthernetLinks.Clear();
        WifiLinks.Clear();
        foreach (var link in discovered.Where(x => x.Kind == "Ethernet")) EthernetLinks.Add(link);
        foreach (var link in discovered.Where(x => x.Kind == "Wi-Fi")) WifiLinks.Add(link);

        SelectedEthernet = EthernetLinks.FirstOrDefault(x => x.Id == _settings.EthernetId) ?? EthernetLinks.FirstOrDefault();
        SelectedWifi = WifiLinks.FirstOrDefault(x => x.Id == _settings.WifiId) ?? WifiLinks.FirstOrDefault();
        if (SelectedEthernet is not null) SelectedEthernet.Weight = _settings.EthernetWeight;
        if (SelectedWifi is not null) SelectedWifi.Weight = _settings.WifiWeight;
        if (SelectedEthernet?.Weight == 0 && SelectedWifi?.Weight == 0 && SelectedEthernet is not null)
            SelectedEthernet.Weight = 1;
        Log($"Detected {EthernetLinks.Count} Ethernet and {WifiLinks.Count} Wi-Fi internet link(s)");
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
            NetworkDiscovery.UpdateRates(EthernetLinks.Concat(WifiLinks), 1);
            OnPropertyChanged(nameof(ActiveConnections));
            OnPropertyChanged(nameof(CombinedSpeedText));
            OnPropertyChanged(nameof(CombinedUploadSpeedText));
            UpdateRunningProfiles();

            var selected = Profiles.Where(x => x.IsSelected).ToList();
            var shouldBoost = _armed && selected.Count > 0 && (!AutoBoost || selected.Any(x => x.IsRunning));
            if (shouldBoost && !_boosting) await StartBoostAsync(selected);
            else if (!shouldBoost && _boosting) await StopBoostAsync("No selected target is running");
            else if (shouldBoost && _boosting && DateTime.UtcNow >= _nextHealthCheckUtc)
            {
                _nextHealthCheckUtc = DateTime.UtcNow.AddSeconds(2);
                await VerifyBoostHealthAsync();
            }
            else if (_armed && AutoBoost && !_boosting)
            {
                StatusText = "Waiting";
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 184, 77));
                _tray?.Update(true, false);
            }
        }
        catch (Exception ex)
        {
            Log($"Controller error: {ex.Message}");
            await StopBoostAsync("Safety stop");
        }
        finally { _controllerGate.Release(); }
    }

    private async Task VerifyBoostHealthAsync()
    {
        if (!await _proxiFyre.IsServiceRunningAsync())
        {
            StatusText = "Recovering…";
            StatusColor = new SolidColorBrush(Color.FromRgb(255, 184, 77));
            _tray?.Update(true, false);
        }
        if (!await _healthMonitor.CheckAndRecoverAsync()) return;
        UpdateActiveRouteStatus();
        _tray?.Update(true, true);
        Log("Filter service recovered");
    }

    private void UpdateRunningProfiles()
    {
        HashSet<string> names;
        try { names = Process.GetProcesses().Select(x => x.ProcessName + ".exe").ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch { return; }
        foreach (var profile in Profiles)
            profile.IsRunning = profile.Processes.Any(names.Contains);
    }

    private async Task StartBoostAsync(IReadOnlyCollection<AppProfile> selected)
    {
        if (SelectedEthernet is null || SelectedWifi is null)
            throw new InvalidOperationException("Select one Ethernet and one Wi-Fi link.");
        var prerequisite = await _proxiFyre.CheckPrerequisitesAsync();
        if (!prerequisite.Installed)
        {
            PrerequisiteText = "Needs setup";
            _armed = false;
            UpdateButton();
            throw new InvalidOperationException(prerequisite.Message);
        }

        StatusText = "Starting…";
        await _balancer.StartAsync(new[]
        {
            (SelectedEthernet.Address, SelectedEthernet.Weight),
            (SelectedWifi.Address, SelectedWifi.Weight)
        });
        try
        {
            var processNames = selected.SelectMany(x => x.Processes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await _proxiFyre.StartAsync(processNames, SocksPort);
            _boosting = true;
            UpdateActiveRouteStatus();
            _tray?.Update(true, true);
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
            Log(reason);
        }
        StatusText = _armed ? "Waiting" : "Ready";
        StatusColor = new SolidColorBrush(_armed ? Color.FromRgb(255, 184, 77) : Color.FromRgb(140, 150, 165));
        _tray?.Update(_armed, false);
    }

    private void StartWatchdog()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = $"--watchdog {Environment.ProcessId}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private void SaveSettings()
    {
        if (!IsInitialized || _loadingSettings) return;
        try
        {
            _settings.AutoBoost = AutoBoost;
            _settings.Armed = _armed;
            _settings.EthernetId = SelectedEthernet?.Id;
            _settings.WifiId = SelectedWifi?.Id;
            _settings.EthernetWeight = SelectedEthernet?.Weight ?? 2;
            _settings.WifiWeight = SelectedWifi?.Weight ?? 5;
            _settings.CloseToTray = CloseToTray;
            _settings.DownloadLimitMbps = SelectedBandwidthOption?.Mbps ?? 0;
            _settings.SelectedProfiles = Profiles.Where(x => x.IsSelected).Select(x => x.Name).ToList();
            _settings.CustomProfiles = Profiles.Where(x => x.IsCustom).ToList();
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
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
        _tray?.Update(_armed, _boosting);
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
    private async void ProfileSelection_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_boosting) await RestartForSelectionChangeAsync();
    }
    private void AutoBoost_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private async Task RestartForSelectionChangeAsync()
    {
        await _controllerGate.WaitAsync();
        try
        {
            await StopBoostAsync("Target selection changed");
            UpdateRunningProfiles();
            var selected = Profiles.Where(x => x.IsSelected).ToList();
            var shouldBoost = _armed && selected.Count > 0 && (!AutoBoost || selected.Any(x => x.IsRunning));
            if (shouldBoost) await StartBoostAsync(selected);
        }
        catch (Exception ex)
        {
            Log($"Target update failed: {ex.Message}");
            await StopBoostAsync("Safety stop");
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
            UpdateButton();
            SaveSettings();
            if (!_armed) await StopBoostAsync("Boost disabled by user");
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
        if (!_balancer.IsRunning || SelectedEthernet is null || SelectedWifi is null) return;
        _balancer.UpdateSources(new[]
        {
            (SelectedEthernet.Address, SelectedEthernet.Weight),
            (SelectedWifi.Address, SelectedWifi.Weight)
        });
        UpdateActiveRouteStatus();
    }

    private void UpdateActiveRouteStatus()
    {
        StatusText = SelectedEthernet?.Weight == 0 ? "Wi-Fi only" : SelectedWifi?.Weight == 0 ? "Ethernet only" : "Boosting";
        StatusColor = new SolidColorBrush(Color.FromRgb(85, 230, 165));
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        SettingsDrawer.Visibility = Visibility.Collapsed;
        DetailsDrawer.Visibility = DetailsDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        DetailsDrawer.Visibility = Visibility.Collapsed;
        SettingsDrawer.Visibility = SettingsDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowSettingsPreview() => SettingsDrawer.Visibility = Visibility.Visible;
    public void ShowNetworkPickerPreview() => WifiAdapterPicker.IsDropDownOpen = true;

    private void CloseDrawer_Click(object sender, RoutedEventArgs e)
    {
        DetailsDrawer.Visibility = Visibility.Collapsed;
        SettingsDrawer.Visibility = Visibility.Collapsed;
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
        var dialog = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = "Choose an application to boost" };
        if (dialog.ShowDialog(this) != true) return;
        var processName = Path.GetFileName(dialog.FileName);
        var displayName = FileVersionInfo.GetVersionInfo(dialog.FileName).FileDescription;
        if (string.IsNullOrWhiteSpace(displayName)) displayName = Path.GetFileNameWithoutExtension(dialog.FileName);
        var profile = new AppProfile
        {
            Name = displayName,
            Subtitle = "Custom application",
            Accent = "#B6C2D1",
            Processes = new List<string> { processName },
            IsCustom = true,
            IsSelected = true
        };
        Profiles.Add(profile);
        SaveSettings();
        Log($"Added {processName}");
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
        await _controllerGate.WaitAsync();
        try
        {
            _armed = false;
            UpdateButton();
            await StopBoostAsync("DualLink closed — normal routing restored");
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
}
