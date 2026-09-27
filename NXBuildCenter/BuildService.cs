using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace NXBuildCenter;

public sealed class BuildService(GitHubService github)
{
    // Keep this deliberately short: Atmosphère's RomFS staging paths can otherwise
    // hit the legacy MAX_PATH limit used by some devkitPro helper executables.
    private readonly string _cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nxbuildcenter");
    public event Action<string>? Log;
    public event Action<double>? Progress;

    public DependencyState InspectDependencies()
    {
        var root = Environment.GetEnvironmentVariable("DEVKITPRO");
        if (string.IsNullOrWhiteSpace(root) || root.StartsWith('/')) root = @"C:\devkitPro";
        return new(
            FindOnPath("git.exe") is not null,
            File.Exists(Path.Combine(root, "msys2", "usr", "bin", "bash.exe")),
            Directory.Exists(Path.Combine(root, "devkitA64")),
            Directory.Exists(Path.Combine(root, "devkitARM")),
            File.Exists(Path.Combine(root, "msys2", "usr", "bin", "make.exe")),
            root);
    }

    public async Task<BuildResult> RunAsync(BuildRequest request, CancellationToken token)
    {
        Directory.CreateDirectory(_cacheRoot);
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var staging = Path.Combine(_cacheRoot, "staging", runId);
        Directory.CreateDirectory(staging);

        LogLine(request.Mode == PackageMode.SourceBuild
            ? "开始：拉取两个官方仓库默认分支的最新源码"
            : $"开始：Atmosphère {request.Atmosphere.TagName} + Hekate {request.Hekate.TagName}");
        SourceBuildInfo? sourceInfo = null;
        if (request.Mode == PackageMode.SourceBuild)
            sourceInfo = await BuildFromSourceAsync(request, staging, token);
        else
            await DownloadOfficialAsync(request, staging, token);

        AddFriendlyLayout(staging, request, sourceInfo);
        Progress?.Invoke(90);
        await MergeDirectoryAsync(staging, request.OutputDirectory, request.PreserveExistingConfig, token);

        var manifest = new BuildManifest
        {
            Mode = request.Mode == PackageMode.SourceBuild ? "source-build" : "official-release",
            AtmosphereVersion = sourceInfo is null ? request.Atmosphere.TagName : "default-branch HEAD",
            HekateVersion = sourceInfo is null ? request.Hekate.TagName : "default-branch HEAD",
            AtmosphereCommit = sourceInfo?.AtmosphereCommit,
            HekateCommit = sourceInfo?.HekateCommit,
            Files = Directory.EnumerateFiles(request.OutputDirectory, "*", SearchOption.AllDirectories)
                .Select(x => Path.GetRelativePath(request.OutputDirectory, x).Replace('\\', '/'))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        };
        var manifestPath = Path.Combine(request.OutputDirectory, "nx-build-info.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), token);

        string? zipPath = null;
        if (request.CreateZip)
        {
            var zipParent = Path.GetDirectoryName(request.OutputDirectory);
            if (string.IsNullOrWhiteSpace(zipParent))
            {
                zipParent = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }

            var zipName = sourceInfo is null
                ? $"NX-SD-{SafeTag(request.Atmosphere.TagName)}-{SafeTag(request.Hekate.TagName)}.zip"
                : $"NX-SD-latest-{sourceInfo.AtmosphereCommit[..8]}-{sourceInfo.HekateCommit[..8]}.zip";
            zipPath = Path.Combine(zipParent, zipName);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(request.OutputDirectory, zipPath, CompressionLevel.Optimal, false);
            LogLine($"已创建 ZIP：{zipPath}");
        }

        Progress?.Invoke(100);
        LogLine("完成。输出目录可直接合并到 SD 卡根目录。");
        return new(request.OutputDirectory, zipPath, manifestPath);
    }

    public async Task<string> DownloadDevkitProInstallerAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.Combine(_cacheRoot, "downloads"));
        var releases = await github.GetReleasesAsync("devkitPro/installer", token);
        var asset = releases.FirstOrDefault()?.Assets.FirstOrDefault(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("未找到 devkitPro 官方 Windows 安装程序。");
        var target = Path.Combine(_cacheRoot, "downloads", asset.Name);
        LogLine($"下载 devkitPro 安装程序：{asset.Name}");
        await github.DownloadAsync(asset.DownloadUrl, target, null, token);
        return target;
    }

    private async Task DownloadOfficialAsync(BuildRequest request, string staging, CancellationToken token)
    {
        var atmosphereAsset = SelectZip(request.Atmosphere, "atmosphere");
        var hekateAsset = SelectZip(request.Hekate, "hekate");
        await DownloadAndExtractAsync(atmosphereAsset, staging, 0, 38, token);
        await DownloadAndExtractAsync(hekateAsset, staging, 38, 75, token);
        CopyFuseePayload(staging);
    }

    private async Task<SourceBuildInfo> BuildFromSourceAsync(BuildRequest request, string staging, CancellationToken token)
    {
        var deps = InspectDependencies();
        if (!deps.DevkitPro)
            throw new DevkitProMissingException("未检测到 devkitPro。请先点击“安装构建环境”，完成官方安装后重试。");

        if (request.InstallDependencies)
        {
            LogLine("检查并安装 devkitPro 构建包……");
            const string packages = "switch-dev switch-glm switch-libjpeg-turbo devkitARM devkitarm-rules hactool git make zip python python-pip gcc";
            const string requiredPackages = "devkitA64 devkitARM switch-glm switch-libjpeg-turbo hactool make zip python python-pip gcc";
            if (!await CheckMsysAsync(deps, $"pacman -Q {requiredPackages} >/dev/null 2>&1", token))
            {
                try
                {
                    // Prefer the local package database. This avoids a full update when
                    // only one small dependency is missing and also tolerates mirror hiccups.
                    await RunMsysAsync(deps, $"pacman -S --needed --noconfirm {packages}", null, token);
                }
                catch (InvalidOperationException)
                {
                    LogLine("本地软件包数据库不足，刷新 devkitPro/MSYS2……");
                    // MSYS2 may terminate its own shell while upgrading the runtime. Run the
                    // core upgrade and package installation in separate fresh shells.
                    await RunMsysWithRetriesAsync(deps, "pacman -Syu --noconfirm", 3, token);
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    await RunMsysWithRetriesAsync(deps, $"pacman -Syu --needed --noconfirm {packages}", 3, token);
                }
            }
            else LogLine("devkitPro 软件包已齐全，跳过系统更新。");
            // lz4 is required by Atmosphère. PyCryptodome is optional upstream, so
            // deliberately avoid forcing another native extension build here.
            await RunMsysAsync(deps, "python -m pip install --user --break-system-packages --disable-pip-version-check lz4", null, token);
        }

        var sources = Path.Combine(_cacheRoot, "sources");
        Directory.CreateDirectory(sources);
        var atmosphereSource = Path.Combine(sources, "Atmosphere-latest");
        var hekateSource = Path.Combine(sources, "hekate-latest");
        var atmosphereCommit = await UpdateLatestSourceAsync("https://github.com/Atmosphere-NX/Atmosphere.git", atmosphereSource, token);
        var hekateCommit = await UpdateLatestSourceAsync("https://github.com/CTCaer/hekate.git", hekateSource, token);
        LogLine($"Atmosphère HEAD：{atmosphereCommit}");
        LogLine($"Hekate HEAD：{hekateCommit}");

        DeleteBuildOutput(atmosphereSource, "out");
        DeleteBuildOutput(hekateSource, "output");
        // Hekate's incremental Makefile creates this directory only through a tool
        // prerequisite. On a repeat build that prerequisite can already be up to date.
        Directory.CreateDirectory(Path.Combine(hekateSource, "output"));

        LogLine("编译 Atmosphère（release）……");
        Progress?.Invoke(18);
        // Build the package3 dependency graph in parallel, then run distribution packaging
        // serially. Passing -j to dist itself can race RomFS collection on a fresh checkout.
        const string atmosphereVars = "ATMOSPHERE_MAKEFILE_TARGET=nx_release ATMOSPHERE_BUILD_NAME=release " +
            "ATMOSPHERE_BOARD=nx-hac-001 ATMOSPHERE_CPU=arm-cortex-a57";
        var atmosphereBuild = $"make -j$(nproc) -f atmosphere.mk package3 {atmosphereVars} && " +
            $"make -f atmosphere.mk --assume-old=package3 dist-no-debug {atmosphereVars}";
        await RunMsysAsync(deps, atmosphereBuild, atmosphereSource, token);
        var atmosphereZip = Directory.EnumerateFiles(Path.Combine(atmosphereSource, "out"), "atmosphere-*.zip", SearchOption.AllDirectories)
            .Where(x => !Path.GetFileName(x).Contains("debug", StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new FileNotFoundException("Atmosphère 编译完成，但未找到发行 ZIP。");
        ZipFile.ExtractToDirectory(atmosphereZip, staging, true);
        var builtFusee = Directory.EnumerateFiles(Path.Combine(atmosphereSource, "out"), "fusee.bin", SearchOption.AllDirectories).FirstOrDefault();
        if (builtFusee is not null) File.Copy(builtFusee, Path.Combine(staging, "fusee.bin"), true);

        LogLine("编译 Hekate / Nyx……");
        Progress?.Invoke(48);
        await RunMsysAsync(deps, "make -j$(nproc)", hekateSource, token);

        // Hekate's repository intentionally does not carry every distributable resource.
        // Start from the matching official bundle, then replace its core binaries with local builds.
        var hekateAsset = SelectZip(request.Hekate, "hekate");
        await DownloadAndExtractAsync(hekateAsset, staging, 58, 70, token);
        OverlayHekateBuild(hekateSource, staging, request.Hekate.TagName);
        CopyFuseePayload(staging);
        return new SourceBuildInfo(atmosphereCommit, hekateCommit);
    }

    private async Task<string> UpdateLatestSourceAsync(string url, string destination, CancellationToken token)
    {
        if (Directory.Exists(Path.Combine(destination, ".git")))
        {
            LogLine($"更新最新源码：{Path.GetFileName(destination)}");
            await RunProcessAsync("git", ["-C", destination, "fetch", "--depth", "1", "--recurse-submodules=no", "origin", "HEAD"], null, token);
            await RunProcessAsync("git", ["-C", destination, "reset", "--hard", "FETCH_HEAD"], null, token);
            await RunProcessAsync("git", ["-C", destination, "submodule", "sync", "--recursive"], null, token);
            await RunProcessAsync("git", ["-C", destination, "submodule", "update", "--init", "--recursive", "--depth", "1"], null, token);
        }
        else
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            LogLine($"克隆默认分支最新源码：{url}");
            await RunProcessAsync("git", ["clone", "--depth", "1", "--recursive", url, destination], null, token);
        }

        return (await RunProcessCaptureAsync("git", ["-C", destination, "rev-parse", "HEAD"], null, token)).Trim();
    }

    private static void DeleteBuildOutput(string source, string relativePath)
    {
        var path = Path.Combine(source, relativePath);
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private async Task DownloadAndExtractAsync(GitHubAsset asset, string staging, double start, double end, CancellationToken token)
    {
        var downloadDir = Path.Combine(_cacheRoot, "downloads");
        Directory.CreateDirectory(downloadDir);
        var archive = Path.Combine(downloadDir, asset.Name);
        if (!File.Exists(archive) || new FileInfo(archive).Length != asset.Size)
        {
            LogLine($"下载：{asset.Name}（{asset.Size / 1024d / 1024d:F1} MB）");
            await github.DownloadAsync(asset.DownloadUrl, archive, new Progress<double>(x => Progress?.Invoke(start + x / 100d * (end - start))), token);
        }
        else
        {
            LogLine($"使用下载缓存：{asset.Name}");
        }
        ZipFile.ExtractToDirectory(archive, staging, true);
        Progress?.Invoke(end);
    }

    private static GitHubAsset SelectZip(GitHubRelease release, string project)
    {
        var candidates = release.Assets.Where(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        var selected = candidates.FirstOrDefault(x => x.Name.Contains(project, StringComparison.OrdinalIgnoreCase)
            && !x.Name.Contains("debug", StringComparison.OrdinalIgnoreCase)
            && !x.Name.Contains("symbols", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
        return selected ?? throw new InvalidOperationException($"{release.TagName} 没有可用的 ZIP 发布包。");
    }

    private static void OverlayHekateBuild(string source, string staging, string tag)
    {
        var output = Path.Combine(source, "output");
        var payload = Path.Combine(output, "hekate.bin");
        if (!File.Exists(payload)) throw new FileNotFoundException("Hekate 编译完成，但未找到 output/hekate.bin。", payload);
        var rootPayload = Directory.EnumerateFiles(staging, "hekate*.bin", SearchOption.TopDirectoryOnly).FirstOrDefault()
                          ?? Path.Combine(staging, $"hekate_ctcaer_{SafeTag(tag)}.bin");
        File.Copy(payload, rootPayload, true);
        var sys = Path.Combine(staging, "bootloader", "sys");
        Directory.CreateDirectory(sys);
        File.Copy(payload, Path.Combine(staging, "bootloader", "update.bin"), true);
        foreach (var name in new[] { "nyx.bin", "libsys_lp0.bso", "libsys_minerva.bso" })
        {
            var file = Path.Combine(output, name);
            if (File.Exists(file)) File.Copy(file, Path.Combine(sys, name), true);
        }
    }

    private static void CopyFuseePayload(string staging)
    {
        var fusee = Directory.EnumerateFiles(staging, "fusee.bin", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (fusee is null) return;
        var payloads = Path.Combine(staging, "bootloader", "payloads");
        Directory.CreateDirectory(payloads);
        File.Copy(fusee, Path.Combine(payloads, "fusee.bin"), true);
    }

    private static void AddFriendlyLayout(string staging, BuildRequest request, SourceBuildInfo? sourceInfo)
    {
        var bootloader = Path.Combine(staging, "bootloader");
        Directory.CreateDirectory(bootloader);
        var ini = Path.Combine(bootloader, "hekate_ipl.ini");
        if (!File.Exists(ini))
        {
            File.WriteAllText(ini, """
[config]
autoboot=0
autoboot_list=0
bootwait=3
backlight=100
autohosoff=1
autonogc=1

[Atmosphere - emuMMC]
pkg3=atmosphere/package3
emummcforce=1
icon=bootloader/res/icon_payload.bmp

[Atmosphere - sysMMC]
pkg3=atmosphere/package3
emummc_force_disable=1
icon=bootloader/res/icon_payload.bmp

[Stock - sysMMC]
pkg3=atmosphere/package3
stock=1
emummc_force_disable=1
""", new UTF8Encoding(false));
        }
        var versions = sourceInfo is null
            ? $"Atmosphère: {request.Atmosphere.TagName}\r\nHekate: {request.Hekate.TagName}"
            : $"Atmosphère: 默认分支 HEAD {sourceInfo.AtmosphereCommit}\r\nHekate: 默认分支 HEAD {sourceInfo.HekateCommit}";
        File.WriteAllText(Path.Combine(staging, "README-NX-BUILD.txt"),
            $"NX Build Center 输出\r\n{versions}\r\n生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n\r\n将本目录内容合并复制到 FAT32/exFAT SD 卡根目录。程序不会提供密钥、签名补丁或游戏内容。\r\n",
            new UTF8Encoding(false));
    }

    private static async Task MergeDirectoryAsync(string source, string destination, bool preserveConfig, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            if (preserveConfig && File.Exists(target) && relative.Replace('\\', '/').Equals("bootloader/hekate_ipl.ini", StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.OpenRead(file);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
            await input.CopyToAsync(output, token);
        }
    }

    private async Task RunMsysAsync(DependencyState deps, string command, string? workingDirectory, CancellationToken token)
    {
        var bash = Path.Combine(deps.DevkitProRoot, "msys2", "usr", "bin", "bash.exe");
        var prefix = workingDirectory is null ? "" : $"cd \"$(cygpath -u '{workingDirectory.Replace("'", "'\\''")}')\" && ";
        var env = $"export DEVKITPRO=/opt/devkitpro DEVKITARM=/opt/devkitpro/devkitARM DEVKITA64=/opt/devkitpro/devkitA64; {prefix}{command}";
        await RunProcessAsync(bash, ["-lc", env], null, token);
    }

    private async Task RunMsysWithRetriesAsync(DependencyState deps, string command, int attempts, CancellationToken token)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await RunMsysAsync(deps, command, null, token);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (attempt >= attempts) break;
                LogLine($"依赖服务器连接失败，将重试（{attempt}/{attempts}）……");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), token);
            }
        }
        throw new InvalidOperationException("多次连接 devkitPro 软件源失败。请检查网络/代理，稍后重试；已下载的源码和文件会保留。", last);
    }

    private async Task<bool> CheckMsysAsync(DependencyState deps, string command, CancellationToken token)
    {
        try { await RunMsysAsync(deps, command, null, token); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private async Task RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken token)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) LogLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) LogLine(e.Data); };
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {fileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new InvalidOperationException($"命令执行失败（退出码 {process.ExitCode}）：{fileName}");
    }

    private async Task<string> RunProcessCaptureAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken token)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {fileName}");
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"命令执行失败（退出码 {process.ExitCode}）：{fileName}\n{error}");
        return output;
    }

    private void LogLine(string text) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {text}");
    private static string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(x => Path.Combine(x, name)).FirstOrDefault(File.Exists);
    private static string SafeTag(string tag) => string.Concat(tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

public sealed class DevkitProMissingException(string message) : Exception(message);
