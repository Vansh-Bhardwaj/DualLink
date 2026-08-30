using System.Net.Http.Headers;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DualLink;

public sealed record UpdateCheckResult(
    bool IsAvailable,
    string Version,
    string PageUrl,
    string? InstallerUrl,
    string? ChecksumsUrl,
    string Message)
{
    public bool CanInstall => IsAvailable && InstallerUrl is not null && ChecksumsUrl is not null;
}

public static class UpdateChecker
{
    private const string Repository = "Vansh-Bhardwaj/DualLink";
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;

    public static string CurrentVersion
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return (informational ?? "0.0.0").Split('+')[0].TrimStart('v');
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken token)
    {
        using var client = CreateClient();
        if (channel == UpdateChannel.Stable)
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", token);
            if (!response.IsSuccessStatusCode) return NoPublishedVersion(channel);
            return EvaluateStableReleaseJson(await response.Content.ReadAsStringAsync(token), CurrentVersion);
        }

        using var tagsResponse = await client.GetAsync($"https://api.github.com/repos/{Repository}/tags?per_page=50", token);
        tagsResponse.EnsureSuccessStatusCode();
        return EvaluatePreviewTagsJson(await tagsResponse.Content.ReadAsStringAsync(token), CurrentVersion);
    }

    public static async Task<string> DownloadInstallerAsync(
        UpdateCheckResult update,
        IProgress<int>? progress,
        CancellationToken token)
    {
        if (!update.CanInstall || update.InstallerUrl is null || update.ChecksumsUrl is null)
            throw new InvalidOperationException("This update does not provide a verified installer.");
        EnsureTrustedGithubUrl(update.InstallerUrl);
        EnsureTrustedGithubUrl(update.ChecksumsUrl);

        using var client = CreateClient(TimeSpan.FromMinutes(10));
        var updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink", "Updates");
        return await DownloadInstallerAsync(update, progress, client, updateRoot, token);
    }

    internal static async Task<string> DownloadInstallerAsync(
        UpdateCheckResult update,
        IProgress<int>? progress,
        HttpClient client,
        string updateRoot,
        CancellationToken token)
    {
        if (!update.CanInstall || update.InstallerUrl is null || update.ChecksumsUrl is null)
            throw new InvalidOperationException("This update does not provide a verified installer.");
        var checksumText = await client.GetStringAsync(update.ChecksumsUrl, token);
        if (checksumText.Length > 1024 * 1024)
            throw new InvalidDataException("The checksum manifest is unexpectedly large.");
        var installerName = Path.GetFileName(new Uri(update.InstallerUrl).AbsolutePath);
        var expectedHash = FindChecksum(checksumText, installerName)
            ?? throw new InvalidDataException("The release checksum manifest does not contain this installer.");

        var safeVersion = string.Concat(update.Version.Where(character => char.IsLetterOrDigit(character) || character is '.' or '-'));
        var updateDirectory = Path.Combine(updateRoot, safeVersion);
        Directory.CreateDirectory(updateDirectory);
        var destination = Path.Combine(updateDirectory, installerName);
        var temporary = destination + ".download";

        try
        {
            using var response = await client.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length > MaximumInstallerBytes)
                throw new InvalidDataException("The update installer is unexpectedly large.");

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, token);
                    if (count == 0) break;
                    received += count;
                    if (received > MaximumInstallerBytes)
                        throw new InvalidDataException("The update installer exceeded the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    if (response.Content.Headers.ContentLength is > 0)
                        progress?.Report((int)Math.Clamp(received * 100 / response.Content.Headers.ContentLength.Value, 0, 100));
                }
            }

            string actualHash;
            await using (var installerStream = File.OpenRead(temporary))
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(installerStream, token)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(expectedHash)))
                throw new InvalidDataException("The downloaded installer did not match the published SHA-256 checksum.");
            File.Move(temporary, destination, true);
            progress?.Report(100);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static UpdateCheckResult EvaluateStableReleaseJson(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!ParsedVersion.TryParse(tag, out var version)) return NoPublishedVersion(UpdateChannel.Stable, currentVersion);
        var normalized = version.Original.TrimStart('v');
        var expectedInstaller = $"DualLink-{normalized}-Setup-x64.exe";
        string? installerUrl = null;
        string? checksumsUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (name?.Equals(expectedInstaller, StringComparison.OrdinalIgnoreCase) == true) installerUrl = url;
                if (name?.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) == true) checksumsUrl = url;
            }
        }
        var pageUrl = root.TryGetProperty("html_url", out var htmlUrl)
            ? htmlUrl.GetString() ?? $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}"
            : $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}";
        return EvaluateCandidate(new VersionCandidate(version, pageUrl, installerUrl, checksumsUrl), currentVersion, UpdateChannel.Stable);
    }

    internal static string? FindChecksum(string manifest, string fileName)
    {
        foreach (var line in manifest.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64 || line.Length <= 66) continue;
            var hash = line[..64];
            var name = line[(separator + 2)..].TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && hash.All(Uri.IsHexDigit))
                return hash.ToLowerInvariant();
        }
        return null;
    }

    private static UpdateCheckResult EvaluatePreviewTagsJson(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var candidates = new List<VersionCandidate>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var tag = item.GetProperty("name").GetString() ?? string.Empty;
            if (ParsedVersion.TryParse(tag, out var version))
                candidates.Add(new VersionCandidate(version, $"https://github.com/{Repository}/tree/{Uri.EscapeDataString(tag)}", null, null));
        }
        var latest = candidates.OrderByDescending(x => x.Version).FirstOrDefault();
        return latest is null ? NoPublishedVersion(UpdateChannel.Preview, currentVersion) : EvaluateCandidate(latest, currentVersion, UpdateChannel.Preview);
    }

    private static UpdateCheckResult EvaluateCandidate(VersionCandidate candidate, string currentVersion, UpdateChannel channel)
    {
        var current = ParsedVersion.Parse(currentVersion);
        if (candidate.Version.CompareTo(current) <= 0)
            return new UpdateCheckResult(false, currentVersion, string.Empty, null, null, $"{channel} is up to date.");
        var canInstall = candidate.InstallerUrl is not null && candidate.ChecksumsUrl is not null;
        var message = channel == UpdateChannel.Preview
            ? $"{candidate.Version.Original} is available to inspect. Installers are reserved for stable releases."
            : canInstall ? $"{candidate.Version.Original} is ready to install." : $"{candidate.Version.Original} is available, but its installer is not ready.";
        return new UpdateCheckResult(true, candidate.Version.Original, candidate.PageUrl, candidate.InstallerUrl, candidate.ChecksumsUrl, message);
    }

    private static UpdateCheckResult NoPublishedVersion(UpdateChannel channel, string? currentVersion = null) =>
        new(false, currentVersion ?? CurrentVersion, string.Empty, null, null, $"No published {channel.ToString().ToLowerInvariant()} version was found.");

    private static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DualLink", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static void EnsureTrustedGithubUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update link is not an expected GitHub release URL.");
    }

    private sealed record VersionCandidate(ParsedVersion Version, string PageUrl, string? InstallerUrl, string? ChecksumsUrl);

    private sealed record ParsedVersion(int Major, int Minor, int Patch, int PreviewNumber, string Original) : IComparable<ParsedVersion>
    {
        public static ParsedVersion Parse(string value) => TryParse(value, out var parsed) ? parsed : new ParsedVersion(0, 0, 0, 0, value);

        public static bool TryParse(string value, out ParsedVersion parsed)
        {
            var normalized = value.Trim().TrimStart('v');
            var parts = normalized.Split('-', 2);
            var numbers = parts[0].Split('.');
            if (numbers.Length < 3 || !int.TryParse(numbers[0], out var major) ||
                !int.TryParse(numbers[1], out var minor) || !int.TryParse(numbers[2], out var patch))
            {
                parsed = new ParsedVersion(0, 0, 0, 0, normalized);
                return false;
            }

            var preview = -1;
            if (parts.Length == 2)
            {
                var suffix = parts[1].Split('.');
                if (suffix.Length != 2 || !suffix[0].Equals("dev", StringComparison.OrdinalIgnoreCase) || !int.TryParse(suffix[1], out preview))
                {
                    parsed = new ParsedVersion(0, 0, 0, 0, normalized);
                    return false;
                }
            }
            parsed = new ParsedVersion(major, minor, patch, preview, normalized);
            return true;
        }

        public int CompareTo(ParsedVersion? other)
        {
            if (other is null) return 1;
            var result = Major.CompareTo(other.Major);
            if (result != 0) return result;
            result = Minor.CompareTo(other.Minor);
            if (result != 0) return result;
            result = Patch.CompareTo(other.Patch);
            if (result != 0) return result;
            if (PreviewNumber < 0 && other.PreviewNumber >= 0) return 1;
            if (PreviewNumber >= 0 && other.PreviewNumber < 0) return -1;
            return PreviewNumber.CompareTo(other.PreviewNumber);
        }
    }
}
