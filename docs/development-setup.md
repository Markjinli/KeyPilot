# 开发与验证环境

## 用户态构建要求

- Windows 10 1809 或更高版本；当前 App 目标为 `net10.0-windows10.0.22621.0`、`win-x64`。
- .NET 10 SDK。仅安装 .NET Runtime 不足以构建项目。
- WinUI App 需要 Windows App SDK 2.3.1 NuGet 包；首次还原可能需要网络或已填充的 NuGet 缓存。
- Visual Studio/Build Tools 需包含 Windows 应用开发组件，才能获得完整 WinUI/XAML 本机构建体验。

构建脚本按以下顺序寻找 SDK：`-DotnetPath`、`KEYPILOT_DOTNET`、PATH 中的 `dotnet`、`E:\KeyPilotTools\dotnet\dotnet.exe`、标准 Program Files 路径，并确认候选项确实包含 SDK。

## 常规 Release

```powershell
.\build.cmd
```

常规构建执行：

1. XML 与 UTF-8 源码检查。
2. Core 测试。
3. Windows Platform 测试（使用 fake 端口，不注入真实按键、不启动程序）。
4. 驱动 Policy Model 测试。
5. Broker 协议/状态机测试（fake driver、内存 transport，不请求 UAC）。
6. 驱动静态模型检查。
7. Release Broker 与 WinUI App build。

可显式指定 SDK：

```powershell
.\build.cmd -DotnetPath E:\KeyPilotTools\dotnet\dotnet.exe
```

可用 `-NuGetPackagesRoot` 或 `KEYPILOT_NUGET_PACKAGES` 指定离线/共享包缓存；若
`E:\KeyPilotTools\nuget-packages` 已存在，脚本会自动使用它。自包含 Broker 和 WinUI 的首次还原仍要求相应 runtime pack 已在缓存中或网络可用。

构建会打印开发 Broker 的 SHA-256，供后续打包流程使用；它不是可信安装清单。不得从仓库或其他用户可写路径自动提权该二进制。生产安装器必须把完整 Broker 输出放到
`<Program Files>\KeyPilot\broker\KeyPilot.DriverBroker.exe`，并在同目录写入
`KeyPilot.DriverBroker.sha256`（恰好 64 个十六进制字符）。也可随 App 放入 `broker\`，但 App 安装根本身必须位于 Program Files。清单和二进制都必须由管理员保护；更高保证的发布流程还应把预期哈希嵌入 App 或验证签名清单。

`--ignore-failed-sources` 允许已完整缓存时离线构建，脚本只隐藏这类 `NU1801` 源不可达提示；缺失必需包仍会以还原错误终止。

## KMDF 工具链

本机已验证 Visual Studio Community 2026 `18.8.2`、C++/DriverKit 组件与项目固定的 WDK/SDK NuGet `10.0.28000.2526`。当前 x64 Release 已完成 `/W4 /WX`、PREfast、Inf2Cat、PE `0x8664` 与 `InfVerif /w` 检查；构建过程强制 `SignMode=Off`，没有签名或安装驱动。

额外需要：

- Visual Studio 2026/Build Tools 的 C++ x64（ARM64 构建还需要 ARM64 工具）。
- Windows SDK 与 WDK，版本必须匹配。
- `WindowsKernelModeDriver10.0` MSBuild 集成。
- 当前驱动构建脚本固定 SDK/WDK NuGet `10.0.28000.2526` 和目标平台 `10.0.28000.0`。

检查 Visual Studio 是否完整可用：

```powershell
& "$env:ProgramFiles(x86)\Microsoft Visual Studio\Installer\vswhere.exe" `
  -latest -products * `
  -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Component.Microsoft.Windows.DriverKit `
  -property installationPath
```

空结果、`isComplete=false` 或找不到 WDK toolset 都表示工具链尚不可用于驱动构建。下载了 SDK/WDK 安装器或 NuGet 包，也不等于已经完成 WDK C 编译。

明确请求驱动编译时运行：

```powershell
.\build.cmd -BuildDriver
```

`Build-Driver.ps1` 会固定还原匹配架构的 SDK/WDK 包、构建 KMDF solution、检查 PE machine，并运行 `InfVerif /w`。该操作不会安装/启动驱动、不会签名证书、不会修改 BCD。

## 测试签名与恢复

测试签名流程见 [`../driver/TEST-SIGNING.md`](../driver/TEST-SIGNING.md)。相关脚本包括：

- `New-KeyPilotTestCertificate.ps1`：创建测试证书材料。
- `Sign-KeyPilotTestPackage.ps1`：签名测试包。
- `Test-TestInstallPrerequisites.ps1`：只读检查测试安装条件。
- `Install-KeyPilotTestDriver.ps1`：需要显式危险确认参数的测试安装入口。
- `Test-InstallPrerequisites.ps1`：检查 CAT 签名状态、平台和恢复脚本，不安装任何内容；INF/SYS 的 CAT 成员关系由固定的 System32 `PnPUtil /add-driver` 在真正安装时强制验证，管理员阶段不会执行可写 SDK/NuGet 目录中的 SignTool。
- `Install-KeyPilotDriver.ps1` / `Uninstall-KeyPilotDriver.ps1`：显式安装/卸载并记录经过 DISM 验证的 `oemNN.inf`。
- `Recover-KeyPilotDriver.cmd`：正常系统、Safe Mode 或 WinRE 中的恢复入口。

常规构建不会调用这些脚本。任何安装前都必须准备外接鼠标/触摸输入、抄录恢复命令并确认可进入 Safe Mode/WinRE。KeyPilot 脚本不会自动启用 TESTSIGNING、关闭 Secure Boot、导入证书或修改代码完整性策略。

本地自用测试签名不能替代生产签名：正常启用 Secure Boot 的 Windows 需要微软接受的生产/证明签名流程与相应发布者资质，这些外部凭据和提交步骤无法由仓库构建自动完成。

## 证据边界

本机当前已经有 x64 WDK 编译与 `InfVerif /w` 成功证据；除此之外，除非对应命令在当前提交和目标机器上实际成功并保留日志，否则不得声称：

- 驱动已签名、安装、加载或能在安全启动环境工作。
- 已通过 SDV、Driver Verifier、HLK、SAS、睡眠恢复、热插拔或压力测试。
- 已验证目标掌机全部厂商特殊键。

策略模型、Broker fake 测试和静态检查只验证各自的用户态/模型边界，不能替代内核与硬件验证。
