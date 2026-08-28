using Microsoft.Win32;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DualLink;

public sealed record BrowserInfo(string DisplayName, string ExecutablePath, string ProcessName);

public static partial class BrowserDiscovery
{
    public static BrowserInfo? FindDefaultBrowser()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = choice?.GetValue("ProgId") as string;
            if (!string.IsNullOrWhiteSpace(progId))
            {
                using var commandKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                var command = commandKey?.GetValue(null) as string;
                var path = ParseExecutable(command);
                if (path is not null && File.Exists(path)) return CreateInfo(path);
            }
        }
        catch { }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Mozilla Firefox\firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"BraveSoftware\Brave-Browser\Application\brave.exe")
        };
        var fallback = candidates.FirstOrDefault(File.Exists);
        return fallback is null ? null : CreateInfo(fallback);
    }

    internal static string? ParseExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        var quoted = QuotedExecutableRegex().Match(command);
        if (quoted.Success) return quoted.Groups[1].Value;
        var bare = BareExecutableRegex().Match(command);
        return bare.Success ? bare.Groups[1].Value.Trim() : null;
    }

    private static BrowserInfo CreateInfo(string path)
    {
        var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
        if (string.IsNullOrWhiteSpace(description)) description = Path.GetFileNameWithoutExtension(path);
        return new BrowserInfo(description, path, Path.GetFileName(path));
    }

    [GeneratedRegex("^\\\"([^\\\"]+\\.exe)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedExecutableRegex();

    [GeneratedRegex("^(.+?\\.exe)(?:\\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BareExecutableRegex();
}
