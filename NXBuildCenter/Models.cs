using System.Text.Json.Serialization;

namespace NXBuildCenter;

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = [];

    public string DisplayName => $"{TagName}{(Prerelease ? "  · 预发布" : "")}{(PublishedAt is null ? "" : $"  · {PublishedAt:yyyy-MM-dd}")}";

    public override string ToString() => DisplayName;
}

public sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public enum PackageMode
{
    OfficialRelease,
    SourceBuild
}

public sealed record BuildRequest(
    GitHubRelease Atmosphere,
    GitHubRelease Hekate,
    string OutputDirectory,
    PackageMode Mode,
    bool InstallDependencies,
    bool PreserveExistingConfig,
    bool CreateZip);

public sealed record DependencyState(
    bool Git,
    bool DevkitPro,
    bool DevkitA64,
    bool DevkitArm,
    bool Make,
    string DevkitProRoot)
{
    public bool ReadyForSourceBuild => Git && DevkitPro && DevkitA64 && DevkitArm && Make;
}

public sealed record BuildResult(string OutputDirectory, string? ZipPath, string ManifestPath);

public sealed class BuildManifest
{
    public string Tool { get; set; } = "NX Build Center 1.1.0";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Mode { get; set; } = "";
    public string AtmosphereVersion { get; set; } = "";
    public string HekateVersion { get; set; } = "";
    public string? AtmosphereCommit { get; set; }
    public string? HekateCommit { get; set; }
    public string AtmosphereRepository { get; set; } = "https://github.com/Atmosphere-NX/Atmosphere";
    public string HekateRepository { get; set; } = "https://github.com/CTCaer/hekate";
    public List<string> Files { get; set; } = [];
}

public sealed record SourceBuildInfo(string AtmosphereCommit, string HekateCommit);
