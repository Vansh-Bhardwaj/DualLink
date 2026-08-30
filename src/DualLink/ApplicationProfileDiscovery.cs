namespace DualLink;

public static class ApplicationProfileDiscovery
{
    public static AppProfile? FindJDownloader()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "JDownloader 2", "JDownloader2.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JDownloader 2", "JDownloader2.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "JDownloader 2", "JDownloader2.exe")
        };
        var launcher = candidates.FirstOrDefault(File.Exists);
        if (launcher is null) return null;
        return new AppProfile
        {
            Name = "JDownloader 2",
            Subtitle = "JDownloader and its Java download engine",
            Accent = "#7FC241",
            Processes = new List<string> { Path.GetFileName(launcher) },
            ExecutablePaths = ExpandExecutablePaths(launcher),
            IsSystemDetected = true
        };
    }

    public static List<string> ExpandExecutablePaths(string executablePath)
    {
        var normalized = Path.GetFullPath(executablePath);
        var paths = new List<string> { normalized };
        if (!IsJDownloaderLauncher(normalized)) return paths;

        var directory = Path.GetDirectoryName(normalized)!;
        foreach (var candidate in new[]
        {
            Path.Combine(directory, "jre", "bin", "javaw.exe"),
            Path.Combine(directory, "jre", "bin", "java.exe"),
            Path.Combine(directory, "runtime", "bin", "javaw.exe"),
            Path.Combine(directory, "runtime", "bin", "java.exe")
        })
        {
            if (File.Exists(candidate)) paths.Add(Path.GetFullPath(candidate));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void EnrichKnownApplication(AppProfile profile)
    {
        var launcher = profile.ExecutablePaths.FirstOrDefault(IsJDownloaderLauncher);
        if (launcher is null) return;
        foreach (var path in ExpandExecutablePaths(launcher))
            if (!profile.ExecutablePaths.Contains(path, StringComparer.OrdinalIgnoreCase)) profile.ExecutablePaths.Add(path);
    }

    private static bool IsJDownloaderLauncher(string path) =>
        Path.GetFileName(path).Equals("JDownloader2.exe", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("JDownloader.exe", StringComparison.OrdinalIgnoreCase);
}
