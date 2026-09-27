# NX Build Center

面向 Windows 10/11 的 Atmosphère + Hekate 下载、编译与 SD 卡打包工具。

## 功能

- 从两个项目的官方 GitHub Releases 获取版本和发布时间。
- “官方发布包”模式：无需编译环境，下载并合并成可直接复制到 SD 卡根目录的结构。
- “编译最新源码（HEAD）”模式：每次从两个官方仓库的默认分支拉取最新提交，调用 devkitPro/MSYS2 完整编译 Atmosphère、Hekate 和 Nyx，并把精确提交哈希写入输出清单。
- 检测 devkitPro；可下载并启动官方 Windows 安装程序；自动安装缺少的 pacman/pip 包。
- 自动放置 `fusee.bin`、生成基础 `hekate_ipl.ini`，并可保留 SD 卡上已有配置。
- 生成 `nx-build-info.json` 版本清单和可选 ZIP。

本项目只使用上游官方仓库，不提供密钥、签名补丁、游戏或其他受版权保护的固件内容。

## 使用

下载 `NXBuildCenter.exe` 后直接运行。程序默认选择“编译最新源码（HEAD）”；若只想快速使用稳定版本，可切换到“官方发布包”。

源码构建首次使用时：

1. 点击“安装构建环境”。
2. 在 devkitPro 官方安装器中选择 **Switch Development**。
3. 回到本程序点击“拉取最新源码并编译”。版本下拉框仅用于“官方发布包”模式。

## 本地构建

```powershell
dotnet build NXBuildCenter.slnx -c Release
dotnet publish NXBuildCenter\NXBuildCenter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

目标为 .NET 8 WPF；发布产物是自包含单文件 Windows x64 EXE。

## 上游项目

- [Atmosphère](https://github.com/Atmosphere-NX/Atmosphere) — GPL-2.0
- [Hekate](https://github.com/CTCaer/hekate) — GPL-2.0
- [devkitPro](https://devkitpro.org/wiki/Getting_Started)

NX Build Center 本身不修改或重新授权上游代码；下载和构建所得文件分别受各自上游许可证约束。
