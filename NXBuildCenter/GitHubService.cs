using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace NXBuildCenter;

public sealed class GitHubService : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public GitHubService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NXBuildCenter", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.Timeout = TimeSpan.FromMinutes(15);
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(string repository, CancellationToken token)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{repository}/releases?per_page=30", token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, _json, token) ?? [];
        return releases.Where(x => !x.Draft && x.Assets.Count > 0).ToList();
    }

    public async Task DownloadAsync(string url, string destination, IProgress<double>? progress, CancellationToken token)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
        var buffer = new byte[1024 * 128];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            copied += read;
            if (total > 0) progress?.Report(copied * 100d / total.Value);
        }
    }

    public void Dispose() => _http.Dispose();
}
