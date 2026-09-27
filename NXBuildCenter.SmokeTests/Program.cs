using NXBuildCenter;

var output = Path.Combine(Path.GetTempPath(), "NXBuildCenterSmoke-" + Guid.NewGuid().ToString("N"));
using var github = new GitHubService();
var builder = new BuildService(github);
builder.Log += Console.WriteLine;

try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
    var atmosphere = await github.GetReleasesAsync("Atmosphere-NX/Atmosphere", cts.Token);
    var hekate = await github.GetReleasesAsync("CTCaer/hekate", cts.Token);
    if (atmosphere.Count == 0 || hekate.Count == 0) throw new Exception("GitHub returned no releases.");
    Console.WriteLine($"Latest: Atmosphere {atmosphere[0].TagName}, Hekate {hekate[0].TagName}");
    var sourceMode = args.Contains("--source", StringComparer.OrdinalIgnoreCase);
    Console.WriteLine($"Dependencies: {builder.InspectDependencies()}");
    var result = await builder.RunAsync(new BuildRequest(atmosphere[0], hekate[0], output,
        sourceMode ? PackageMode.SourceBuild : PackageMode.OfficialRelease, sourceMode, true, false), cts.Token);

    var required = new[]
    {
        Path.Combine(output, "atmosphere", "package3"),
        Path.Combine(output, "bootloader", "update.bin"),
        Path.Combine(output, "bootloader", "hekate_ipl.ini"),
        Path.Combine(output, "nx-build-info.json")
    };
    foreach (var file in required)
        if (!File.Exists(file)) throw new FileNotFoundException("Missing expected SD card file", file);

    if (sourceMode)
    {
        var manifestText = await File.ReadAllTextAsync(result.ManifestPath, cts.Token);
        if (!manifestText.Contains("AtmosphereCommit", StringComparison.Ordinal) ||
            !manifestText.Contains("HekateCommit", StringComparison.Ordinal))
            throw new Exception("Source manifest did not record commit hashes.");
    }

    Console.WriteLine($"PASS: {result.OutputDirectory}");
}
finally
{
    if (Directory.Exists(output)) Directory.Delete(output, true);
}
