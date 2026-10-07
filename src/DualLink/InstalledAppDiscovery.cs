using Microsoft.Win32;
using System.Diagnostics;

namespace DualLink;

/// <summary>Finds supported desktop apps from Windows registrations, known folders, and running apps.
/// Does not walk drives or execute installer commands.</summary>
public static class InstalledAppDiscovery
{
    private sealed record SupportedApp(string Name, string Accent, string[] Executables, string[] Folders);
    private static readonly SupportedApp[] Catalog =
    [
        new("Steam", "#66C0F4", ["steam.exe"], ["Steam"]),
        new("Epic Games", "#49B8FF", ["EpicGamesLauncher.exe", "EpicOnlineServicesInstallHelper.exe"],
            [@"Epic Games\Launcher\Portal\Binaries\Win64", @"Epic Games\Launcher\Portal\Binaries\Win32"]),
        new("Riot Games", "#FF4655", ["RiotClientServices.exe", "RiotClientUx.exe"], [@"Riot Games\Riot Client"]),
        new("Battle.net", "#148EFF", ["Battle.net.exe", "Agent.exe"], ["Battle.net"]),
        new("EA app", "#FF6A2A", ["EADesktop.exe", "EABackgroundService.exe"], [@"Electronic Arts\EA Desktop\EA Desktop"]),
        new("JDownloader 2", "#7FC241", ["JDownloader2.exe", "JDownloader.exe"], ["JDownloader 2"]),
        new("Chrome", "#A78BFA", ["chrome.exe"], [@"Google\Chrome\Application"]),
        new("Edge", "#A78BFA", ["msedge.exe"], [@"Microsoft\Edge\Application"]),
        new("Firefox", "#A78BFA", ["firefox.exe"], ["Mozilla Firefox"]),
        new("Brave", "#A78BFA", ["brave.exe"], [@"BraveSoftware\Brave-Browser\Application"]),
        new("Opera", "#A78BFA", ["opera.exe"], [@"Programs\Opera", "Opera"]),
        new("Internet Download Manager", "#7FC241", ["IDMan.exe"], ["Internet Download Manager"]),
        new("Free Download Manager", "#7FC241", ["fdm.exe"], ["Free Download Manager"])
    ];

    public static IReadOnlyList<AppProfile> Scan()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetPathRoot(Environment.SystemDirectory)!
        };
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)))
        foreach (var app in Catalog)
        foreach (var folder in app.Folders) AddFolder(Path.Combine(root, folder));

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                foreach (var executable in Catalog.SelectMany(x => x.Executables).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    using var key = appPaths?.OpenSubKey(executable);
                    AddFile(key?.GetValue(null) as string);
                }
                using var steam = registry.OpenSubKey(@"Software\Valve\Steam");
                AddFolder(steam?.GetValue("SteamPath") as string);
                AddFolder(steam?.GetValue("InstallPath") as string);
                using var installed = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in installed?.GetSubKeyNames() ?? [])
                {
                    using var key = installed?.OpenSubKey(name);
                    var title = key?.GetValue("DisplayName") as string;
                    if (!Catalog.Any(app => MatchesTitle(app, title))) continue;
                    var location = key?.GetValue("InstallLocation") as string;
                    AddFolder(location);
                    var icon = ParseIconPath(key?.GetValue("DisplayIcon") as string);
                    AddFile(icon);
                    if (icon is not null) AddFolder(Path.GetDirectoryName(icon));
                    if (title?.Contains("Opera", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        foreach (var root in new[] { location, icon is null ? null : Path.GetDirectoryName(icon) }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            if (!Directory.Exists(root)) continue;
                            foreach (var folder in Directory.EnumerateDirectories(root).Where(x => char.IsDigit(Path.GetFileName(x).FirstOrDefault())).Take(32)) AddFolder(folder);
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        foreach (var relative in new[] { @"Portal\Binaries\Win64", @"Portal\Binaries\Win32", "Riot Client", "EA Desktop" })
                            AddFolder(Path.Combine(location, relative));
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
        }

        // Battle.net updates keep the downloader in a versioned folder.
        var agentRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent");
        try
        {
            if (Directory.Exists(agentRoot))
                foreach (var folder in Directory.EnumerateDirectories(agentRoot, "Agent.*").Take(32)) AddFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var defaultBrowser = BrowserDiscovery.FindDefaultBrowser();
        AddFile(defaultBrowser?.ExecutablePath);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (Catalog.Any(app => Path.GetFileNameWithoutExtension(app.Executables[0]).Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase)))
                        AddFile(process.MainModule?.FileName);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return FromExecutablePaths(paths);

        void AddFolder(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            try
            {
                foreach (var executable in Catalog.SelectMany(x => x.Executables)) AddFile(Path.Combine(folder, executable));
            }
            catch (ArgumentException) { }
        }
        void AddFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (Path.IsPathFullyQualified(path) && File.Exists(path)) paths.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
    }

    internal static IReadOnlyList<AppProfile> FromExecutablePaths(IEnumerable<string> executablePaths)
    {
        var paths = executablePaths.Where(path => Path.IsPathFullyQualified(path) && File.Exists(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var profiles = new List<AppProfile>();
        foreach (var app in Catalog)
        {
            var launcher = paths.FirstOrDefault(path => Path.GetFileName(path).Equals(app.Executables[0], StringComparison.OrdinalIgnoreCase));
            if (launcher is null && app.Name == "JDownloader 2")
                launcher = paths.FirstOrDefault(path => Path.GetFileName(path).Equals("JDownloader.exe", StringComparison.OrdinalIgnoreCase));
            if (launcher is null) continue;
            var folder = Path.GetDirectoryName(launcher)!;
            var related = paths.Where(path => app.Executables.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) &&
                (Path.GetDirectoryName(path)!.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                 app.Name == "Battle.net" && path.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var path in ApplicationProfileDiscovery.ExpandExecutablePaths(launcher))
                if (!related.Contains(path, StringComparer.OrdinalIgnoreCase)) related.Add(path);
            profiles.Add(new AppProfile
            {
                Name = app.Name, Subtitle = app.Name, Accent = app.Accent,
                Processes = app.Executables.ToList(), ExecutablePaths = related, IsSystemDetected = true
            });
        }
        return profiles.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool MatchesTitle(SupportedApp app, string? title) => !string.IsNullOrWhiteSpace(title) &&
        (title.Contains(app.Name, StringComparison.OrdinalIgnoreCase) || app.Executables.Any(exe =>
            title.Contains(Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase)) ||
         app.Name == "EA app" && title.StartsWith("EA Desktop", StringComparison.OrdinalIgnoreCase));

    private static string? ParseIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = Environment.ExpandEnvironmentVariables(value.Trim());
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        var comma = value.LastIndexOf(',');
        return comma > 0 && int.TryParse(value[(comma + 1)..], out _) ? value[..comma].Trim() : value;
    }
}
