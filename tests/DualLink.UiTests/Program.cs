using DualLink;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO.Pipes;
using System.IO;
using System.Text;
using System.Text.Json;
using Protocol = DualLink.Service.Protocol;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new App(startWindow: false);
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var result = 1;
        application.Dispatcher.BeginInvoke(async () =>
        {
            var window = new MainWindow(previewMode: true);
            var fixtureDirectory = Path.Combine(Path.GetTempPath(), "DualLink-window-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureDirectory);
            try
            {
                // Use the real window controller with synthetic helper snapshots.
                // No elevated helper, packet filter, or adapter changes are needed.
                // Settings written by the rollback check stay in the temporary fixture.
                Set(window, "_previewMode", false);
                Set(window, "_diagnosticLog", null);
                Set(window, "_settingsPath", Path.Combine(fixtureDirectory, "settings.json"));
                Set(window, "_armed", true);
                Set(window, "_boosting", true);
                var inactive = new Protocol.SessionStatus(false, false, 0, 0, Protocol.RoutingMode.Balanced, [], false, false, 0);
                Set(window, "_serviceStatus", inactive);
                await (Task)Invoke(window, "VerifyBoostHealthAsync")!;
                if ((bool)Get(window, "_boosting")! || (DateTime)Get(window, "_nextStartAttemptUtc")! <= DateTime.UtcNow)
                    throw new Exception("Inactive helper stranded the window in Recovering.");
                Console.WriteLine("PASS: the window clears a stopped helper session and schedules recovery");

                var routes = new[]
                {
                    new Protocol.RouteStatus("127.0.0.1", "Ethernet", 0, false, 0, 0, DateTime.MinValue, null, null, 1, 1000, 0, 1),
                    new Protocol.RouteStatus("127.0.0.2", "Wi-Fi", 0, true, 1, 0, DateTime.MinValue, null, null, 1, 3000, 0, 1)
                };
                Set(window, "_serviceStatus", inactive with { IsRunning = true, FilterRunning = true, Routes = routes });
                Set(window, "_boosting", true);
                if (!window.BoostContributionSummary.StartsWith("4 KB") || window.SessionTrafficText != "4 KB")
                    throw new Exception("Session totals dropped the retired route's bytes.");
                Console.WriteLine("PASS: session totals retain disabled-route contribution");
                if (window.RecoveryText != "Off") throw new Exception("Recovery was shown without watchdog evidence.");
                Set(window, "_serviceStatus", inactive with { WatchdogRunning = true });
                if (window.RecoveryText != "Ready") throw new Exception("Confirmed watchdog status was lost.");
                Console.WriteLine("PASS: recovery display follows the helper's watchdog evidence");

                Invoke(window, "SetBoosting", false);
                var history = Get(window, "_trafficHistory")!;
                if ((int)history.GetType().GetProperty("Count")!.GetValue(history)! != 0 || window.TrafficScopeText != "Device speed")
                    throw new Exception("Device and app history were mixed after stopping.");
                Console.WriteLine("PASS: changing traffic scope clears old history");

                var deviceLink = window.SelectedEthernet!;
                var deviceRates = new Dictionary<string, (long Received, long Sent)> { [deviceLink.Id] = (50_000_000, 2_000_000) };
                Invoke(window, "ApplyDeviceRates", new[] { deviceLink }, deviceRates, 1d);
                if (deviceLink.DownloadMbps != 0 || deviceLink.UploadMbps != 0)
                    throw new Exception("Stopping reported the previous routed session as a device-speed spike.");
                deviceRates[deviceLink.Id] = (50_100_000, 2_000_000);
                Invoke(window, "ApplyDeviceRates", new[] { deviceLink }, deviceRates, 1d);
                if (Math.Abs(deviceLink.DownloadMbps - 0.8) > 0.001)
                    throw new Exception("Device speed did not resume from its new baseline.");
                Console.WriteLine("PASS: stopping resets device counters without a false speed spike");

                var icon = AppIconLoader.Load(Environment.ProcessPath!);
                if (icon is null || !icon.IsFrozen || !ReferenceEquals(icon, AppIconLoader.Load(Environment.ProcessPath!)))
                    throw new Exception("Installed executable icons were missing, mutable, or reloaded.");
                Console.WriteLine("PASS: device executable icons load once and are safe for the UI thread");

                await VerifyRejectedRouteChangeAsync(window, inactive);
                Console.WriteLine("PASS: rejected link changes restore confirmed limits and mode in the real window");
                Set(window, "_loadingSettings", true);
                Set(window, "_serviceClient", null);
                if (args.Length > 0)
                {
                    Directory.CreateDirectory(Path.GetFullPath(args[0]));
                    window.Width = 860;
                    window.Height = 580;
                    window.Show();
                    Set(window, "_armed", false);
                    Invoke(window, "SetBoosting", false);
                    SetProperty(window, "ActionHint", string.Empty);
                    SetProperty(window, "StatusColor", window.FindResource("SecondaryBrush"));
                    Set(window, "_serviceStatus", inactive);
                    SetProperty(window, "StatusText", "Off");
                    SaveSnapshot(window, Path.Combine(args[0], "duallink-off.png"));
                    Set(window, "_armed", true);
                    SetProperty(window, "StatusText", "Waiting for app");
                    SetProperty(window, "StatusColor", window.FindResource("WarningBrush"));
                    SaveSnapshot(window, Path.Combine(args[0], "duallink-waiting.png"));
                    SetProperty(window, "StatusText", "Couldn't start");
                    Set(window, "_armed", false);
                    SetProperty(window, "StatusColor", window.FindResource("DangerBrush"));
                    SetProperty(window, "ActionHint", "Run setup again");
                    SaveSnapshot(window, Path.Combine(args[0], "duallink-error.png"));
                    SetProperty(window, "ActionHint", string.Empty);
                    Set(window, "_boosting", true);
                    window.SelectedEthernet!.RouteControlMbps = LinkInfo.FullSpeedControlMbps;
                    window.SelectedWifi!.RouteControlMbps = 0;
                    Set(window, "_serviceStatus", inactive with { IsRunning = true, FilterRunning = true, Routes =
                        [new Protocol.RouteStatus(window.SelectedEthernet.Address, "Ethernet", 0, true, 1, 0, DateTime.MinValue, null, null, 1, 1024, 0, 1)] });
                    Invoke(window, "UpdateActiveRouteStatus");
                    SaveSnapshot(window, Path.Combine(args[0], "duallink-single-link.png"));
                    window.Profiles.Clear();
                    Set(window, "_boosting", false);
                    Set(window, "_armed", false);
                    SetProperty(window, "StatusText", "Off");
                    SaveSnapshot(window, Path.Combine(args[0], "duallink-empty.png"));
                }
                result = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                Set(window, "_previewMode", true);
                window.Close();
                await (Task)Get(window, "_settingsWriteTask")!;
                Directory.Delete(fixtureDirectory, recursive: true);
                application.Shutdown();
                application.Dispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        return result;
    }
    private static async Task VerifyRejectedRouteChangeAsync(MainWindow window, Protocol.SessionStatus inactive)
    {
        var confirmed = inactive with { IsRunning = true, FilterRunning = true, Routes =
        [
            new Protocol.RouteStatus(window.SelectedEthernet!.Address, "Ethernet", 100, true, 0, 0, DateTime.MinValue, null, null, 1, 0, 0, 0),
            new Protocol.RouteStatus(window.SelectedWifi!.Address, "Wi-Fi", 25, true, 0, 0, DateTime.MinValue, null, null, 1, 0, 0, 0)
        ] };
        var name = Protocol.DualLinkServiceProtocol.CreatePipeName();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responder = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                var request = JsonSerializer.Deserialize<Protocol.ServiceRequest>(line, Protocol.DualLinkServiceProtocol.JsonOptions)!;
                object payload = request.Command == "hello" ? new Protocol.HelloResponse(Protocol.DualLinkServiceProtocol.CurrentVersion, 0, "fixture", []) : "shutdown";
                var rejected = request.Command == "update-routes";
                var response = new Protocol.ServiceResponse(request.Id, Protocol.DualLinkServiceProtocol.CurrentVersion, !rejected,
                    rejected ? "Fixture rejects the change" : null, JsonSerializer.SerializeToElement(payload, Protocol.DualLinkServiceProtocol.JsonOptions));
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Protocol.DualLinkServiceProtocol.JsonOptions));
                if (request.Command == "shutdown") return;
            }
        }, deadline.Token);
        await using (var client = await DualLinkServiceClient.ConnectAsync(name, TimeSpan.FromSeconds(3), deadline.Token))
        {
            Set(window, "_serviceClient", client);
            Set(window, "_serviceStatus", confirmed);
            Set(window, "_boosting", true);
            window.SelectedWifi.RouteControlMbps = 500;
            window.SelectedRoutingModeOption = window.RoutingModeOptions.First(x => x.Mode == RoutingMode.Failover);
            while (!window.ActionHint.StartsWith("Change failed")) await Task.Delay(25, deadline.Token);
            if (window.SelectedEthernet.SpeedLimitMbps != 100 || window.SelectedWifi.SpeedLimitMbps != 25 ||
                window.SelectedRoutingModeOption?.Mode != RoutingMode.Balanced || !client.IsConnected)
                throw new Exception("Rejected route changes left requested policy on screen.");
        }
        await responder;
    }
    private static void SetProperty(MainWindow window, string name, object value) => typeof(MainWindow).GetProperty(name)!.SetValue(window, value);
    private static void SaveSnapshot(MainWindow window, string path)
    {
        Invoke(window, "UpdateButton");
        Invoke(window, "OnPropertyChanged", new object[] { null! });
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.GetFullPath(path));
        encoder.Save(output);
    }
    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Set(MainWindow window, string name, object? value) => Field(name).SetValue(window, value);
    private static object? Get(MainWindow window, string name) => Field(name).GetValue(window);
    private static object? Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, args);
}

