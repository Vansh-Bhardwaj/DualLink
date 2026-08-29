using System.Net.Http.Headers;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace DualLink;

public sealed record UpdateCheckResult(bool IsAvailable, string Version, string PageUrl, string Message);

public static class UpdateChecker
{
    private const string Repository = "Vansh-Bhardwaj/DualLink";

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
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DualLink", CurrentVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var candidates = channel == UpdateChannel.Stable
            ? await ReadStableReleaseAsync(client, token)
            : await ReadTagsAsync(client, token);
        var current = ParsedVersion.Parse(CurrentVersion);
        var latest = candidates
            .Where(x => channel == UpdateChannel.Preview || !x.Version.IsPreview)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();

        if (latest is null)
            return new UpdateCheckResult(false, CurrentVersion, string.Empty, "No published version was found.");
        if (latest.Version.CompareTo(current) <= 0)
            return new UpdateCheckResult(false, CurrentVersion, string.Empty, $"{channel} is up to date.");
        return new UpdateCheckResult(true, latest.Version.Original, latest.Url, $"{latest.Version.Original} is available.");
    }

    private static async Task<List<VersionCandidate>> ReadStableReleaseAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", token);
        if (!response.IsSuccessStatusCode) return new List<VersionCandidate>();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var url = $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}";
        return ParsedVersion.TryParse(tag, out var version)
            ? new List<VersionCandidate> { new(version, url) }
            : new List<VersionCandidate>();
    }

    private static async Task<List<VersionCandidate>> ReadTagsAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/tags?per_page=50", token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var result = new List<VersionCandidate>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var tag = item.GetProperty("name").GetString() ?? string.Empty;
            if (ParsedVersion.TryParse(tag, out var version))
                result.Add(new VersionCandidate(version, $"https://github.com/{Repository}/tree/{Uri.EscapeDataString(tag)}"));
        }
        return result;
    }

    private sealed record VersionCandidate(ParsedVersion Version, string Url);

    private sealed record ParsedVersion(int Major, int Minor, int Patch, int PreviewNumber, string Original) : IComparable<ParsedVersion>
    {
        public bool IsPreview => PreviewNumber >= 0;

        public static ParsedVersion Parse(string value) =>
            TryParse(value, out var parsed) ? parsed : new ParsedVersion(0, 0, 0, 0, value);

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
                if (suffix.Length != 2 || !suffix[0].Equals("dev", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(suffix[1], out preview))
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
