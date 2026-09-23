using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AXVideoPlayer;

internal static class UpdateCheckService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/XuzzEngineer/AXVideoPlayer/releases/latest";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    internal static async Task<(string Message, string? ReleaseUrl)> CheckAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("AXVideoPlayer/2.3");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument release = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        string tag = release.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        string? url = release.RootElement.GetProperty("html_url").GetString();
        Version current = typeof(App).Assembly.GetName().Version ?? new Version(2, 3, 0);
        var comparison = CompareRelease(tag, current);
        return comparison.Newer ? (comparison.Message, url) : (comparison.Message, null);
    }

    internal static (string Message, bool Newer) CompareRelease(string tag, Version current)
    {
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out Version? latest))
            throw new InvalidDataException("GitHub returned a release tag without a version number.");
        Version normalizedLatest = new(latest.Major, latest.Minor, Math.Max(0, latest.Build), Math.Max(0, latest.Revision));
        Version normalizedCurrent = new(current.Major, current.Minor, Math.Max(0, current.Build), Math.Max(0, current.Revision));
        return normalizedLatest.CompareTo(normalizedCurrent) > 0
            ? ($"Version {tag} is available. Installed: {current.ToString(3)}.", true)
            : ($"You have the latest release ({current.ToString(3)}).", false);
    }
}
