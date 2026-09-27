using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NXBuildCenter;

public partial class MainWindow : Window
{
    private readonly GitHubService _github = new();
    private readonly BuildService _builder;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _builder = new BuildService(_github);
        _builder.Log += text => Dispatcher.Invoke(() => AppendLog(text));
        _builder.Progress += value => Dispatcher.Invoke(() => BuildProgress.Value = value);
        OutputPathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "NX-SD-Ready");
        Loaded += async (_, _) => { UpdateDependencyState(); await RefreshVersionsAsync(); };
        Closed += (_, _) => { _cts?.Cancel(); _github.Dispose(); };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshVersionsAsync();

    private async Task RefreshVersionsAsync()
    {
        if (_busy) return;
        SetBusy(true, "读取 GitHub 版本");
        try
        {
            AppendLog("正在从官方 GitHub 读取发行版本……");
            using var tokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var atmosphereTask = _github.GetReleasesAsync("Atmosphere-NX/Atmosphere", tokenSource.Token);
            var hekateTask = _github.GetReleasesAsync("CTCaer/hekate", tokenSource.Token);
            await Task.WhenAll(atmosphereTask, hekateTask);
            AtmosphereCombo.ItemsSource = atmosphereTask.Result;
            HekateCombo.ItemsSource = hekateTask.Result;
            AtmosphereCombo.SelectedIndex = 0;
            HekateCombo.SelectedIndex = 0;
            AppendLog($"已读取 {atmosphereTask.Result.Count} 个 Atmosphère 版本和 {hekateTask.Result.Count} 个 Hekate 版本。");
            UpdateModeUi();
            StatusText.Text = "  ·  版本已更新";
        }
        catch (Exception ex)
        {
            AppendLog($"读取版本失败：{ex.Message}");
            StatusText.Text = "  ·  网络错误";
            MessageBox.Show(this, "无法从 GitHub 读取版本。请检查网络、代理或 GitHub API 限额。\n\n" + ex.Message, "版本读取失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (AtmosphereCombo.SelectedItem is not GitHubRelease atmosphere || HekateCombo.SelectedItem is not GitHubRelease hekate)
        {
            MessageBox.Show(this, "请先刷新并选择两个项目的版本。", "缺少版本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var output = OutputPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            MessageBox.Show(this, "请选择输出目录。", "缺少输出目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { output = Path.GetFullPath(output); }
        catch { MessageBox.Show(this, "输出路径无效。", "路径错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var mode = SourceModeRadio.IsChecked == true ? PackageMode.SourceBuild : PackageMode.OfficialRelease;
        var request = new BuildRequest(atmosphere, hekate, output, mode, InstallDepsCheck.IsChecked == true,
            PreserveConfigCheck.IsChecked == true, CreateZipCheck.IsChecked == true);

        _cts = new CancellationTokenSource();
        LogBox.Clear();
        BuildProgress.Value = 0;
        SetBusy(true, mode == PackageMode.SourceBuild ? "正在编译" : "正在下载并打包");
        try
        {
            var result = await _builder.RunAsync(request, _cts.Token);
            StatusText.Text = "  ·  已完成";
            var answer = MessageBox.Show(this, $"SD 卡文件已生成。\n\n{result.OutputDirectory}\n\n是否立即打开？", "生成完成", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) OpenFolder(result.OutputDirectory);
        }
        catch (OperationCanceledException)
        {
            AppendLog("操作已取消。缓存与已生成文件保留，可稍后重试。");
            StatusText.Text = "  ·  已取消";
        }
        catch (DevkitProMissingException ex)
        {
            AppendLog(ex.Message);
            StatusText.Text = "  ·  缺少构建环境";
            MessageBox.Show(this, ex.Message, "需要 devkitPro", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("失败：" + ex);
            StatusText.Text = "  ·  构建失败";
            MessageBox.Show(this, "操作失败。日志中保留了详细信息。\n\n" + ex.Message, "构建失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            UpdateDependencyState();
        }
    }

    private async void InstallEnvironmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "下载 devkitPro 安装程序");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var installer = await _builder.DownloadDevkitProInstallerAsync(cts.Token);
            AppendLog("启动官方 devkitPro 安装程序。请选择 Switch Development（switch-dev）。");
            Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, Verb = "runas" });
            MessageBox.Show(this, "已启动 devkitPro 官方安装程序。\n\n请在组件页选择 Switch Development。安装完成后回到本程序，源码构建会自动补齐其余软件包。", "安装构建环境", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("安装程序下载/启动失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "无法启动安装程序", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); UpdateDependencyState(); }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择输出目录或 SD 卡根目录", Multiselect = false };
        if (Directory.Exists(OutputPathBox.Text)) dialog.InitialDirectory = OutputPathBox.Text;
        if (dialog.ShowDialog(this) == true) OutputPathBox.Text = dialog.FolderName;
    }

    private void DetectSdButton_Click(object sender, RoutedEventArgs e)
    {
        var removable = DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Removable).ToList();
        if (removable.Count == 0)
        {
            MessageBox.Show(this, "未检测到可移动磁盘。请插入 SD 卡，或手动选择目录。", "未找到 SD 卡", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (removable.Count == 1)
        {
            OutputPathBox.Text = removable[0].RootDirectory.FullName;
            AppendLog($"检测到 SD 卡：{removable[0].Name} {removable[0].VolumeLabel}，可用 {removable[0].AvailableFreeSpace / 1024d / 1024d / 1024d:F1} GB");
            return;
        }
        var list = string.Join("\n", removable.Select((x, i) => $"{i + 1}. {x.Name} {x.VolumeLabel}"));
        MessageBox.Show(this, "检测到多个可移动磁盘，请点击“浏览”并手动选择：\n\n" + list, "多个磁盘", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var path = OutputPathBox.Text.Trim();
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        OpenFolder(path);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (InstallDepsCheck is null) return;
        UpdateModeUi();
        UpdateDependencyState();
    }

    private void UpdateModeUi()
    {
        if (InstallDepsCheck is null || AtmosphereCombo is null || HekateCombo is null) return;
        var sourceMode = SourceModeRadio.IsChecked == true;
        InstallDepsCheck.IsEnabled = sourceMode;
        AtmosphereCombo.IsEnabled = !sourceMode;
        HekateCombo.IsEnabled = !sourceMode;
        RunButton.Content = sourceMode ? "拉取最新源码并编译" : "生成 SD 卡文件";
        if (sourceMode)
        {
            AtmosphereInfo.Text = "默认分支最新 HEAD · 构建时自动更新并记录提交哈希";
            HekateInfo.Text = "默认分支最新 HEAD · 构建时自动更新并记录提交哈希";
        }
        else
        {
            VersionCombo_SelectionChanged(this, null!);
        }
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceModeRadio?.IsChecked == true) return;
        if (AtmosphereCombo.SelectedItem is GitHubRelease atmosphere)
            AtmosphereInfo.Text = $"{atmosphere.Assets.Count} 个文件 · {(atmosphere.Prerelease ? "预发布版本" : "稳定版本")} · {atmosphere.PublishedAt:yyyy-MM-dd HH:mm}";
        if (HekateCombo.SelectedItem is GitHubRelease hekate)
            HekateInfo.Text = $"{hekate.Assets.Count} 个文件 · {(hekate.Prerelease ? "预发布版本" : "稳定版本")} · {hekate.PublishedAt:yyyy-MM-dd HH:mm}";
    }

    private void UpdateDependencyState()
    {
        if (_builder is null || DependencyText is null) return;
        var state = _builder.InspectDependencies();
        if (state.ReadyForSourceBuild)
        {
            DependencyText.Text = "devkitPro 就绪";
            DependencyText.Foreground = new SolidColorBrush(Color.FromRgb(85, 214, 190));
            InstallEnvironmentButton.Content = "更新环境";
        }
        else
        {
            DependencyText.Text = state.DevkitPro ? "需补齐组件" : "未安装 devkitPro";
            DependencyText.Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 112));
            InstallEnvironmentButton.Content = "安装构建环境";
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        RunButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        InstallEnvironmentButton.IsEnabled = !busy;
        CancelButton.Visibility = busy && _cts is not null ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null) StatusText.Text = "  ·  " + status;
    }

    private void AppendLog(string text)
    {
        LogBox.AppendText(text + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
}
