# KeyPilot 本地测试驱动流程（仅自有测试设备）

本目录已经包含编译好的 x64 未签名驱动。以下流程与正常 App/Broker 安装完全分离；任何脚本都不会自动关闭 Secure Boot、开启 TESTSIGNING、修改 BCD 或导入证书。

请先从原始、未修改的发布包完成 App/Broker 安装与 `检查发布包.cmd` 校验。测试签名会有意改变 SYS/CAT，因此签名后的工作副本不再匹配整包 `MANIFEST.sha256`，不要再用它更新 App/Broker；需要重验或重装时重新解压原始 ZIP。

## 风险准备

1. 准备始终可用的外接鼠标/触摸输入与 Windows 恢复介质。
2. 确认可以进入 Safe Mode/WinRE，并把 `Recover-KeyPilotDriver.cmd` 与已安装后生成的 `oemNN.inf` 记录放在离线可访问位置。
3. 只在个人控制、允许恢复的测试设备上继续。当前驱动尚未通过 HLK、Driver Verifier、睡眠/唤醒、热插拔或目标掌机压力验证。

## 创建并签名本地测试包

在本 `driver-package` 目录打开 PowerShell：

```powershell
.\scripts\New-KeyPilotTestCertificate.ps1
.\scripts\Sign-KeyPilotTestPackage.ps1 `
  -Platform x64 `
  -IUnderstandThisCreatesATestSignedKernelPackage
```

第一个脚本只在本目录的 `artifacts\test-signing` 创建受密码保护的 PFX/CER，不导入证书存储。签名脚本修改本目录
`KeyPilotFilter\x64\Release\KeyPilotFilter` 下的 SYS/CAT；请保留原发布 ZIP，以便恢复未修改副本。

签名/前置检查还需要 WDK NuGet 10.0.28000.2526 的 SignTool/Inf2Cat。脚本默认查找 `E:\KeyPilotTools\nuget-packages`；若工具缓存在其他位置，请显式传入 `-NuGetPackagesRoot`。

随后必须由用户自行完成并理解这些 Windows 测试条件：把 CER 导入本机受信任根和受信任发布者、在固件中关闭 Secure Boot、手动启用 TESTSIGNING 并重启。KeyPilot 不会代做这些系统安全变更。

## 只读前置检查

```powershell
.\scripts\Test-TestInstallPrerequisites.ps1 `
  -Platform x64 `
  -IHaveExternalInputAndRecoveryMedia `
  -DangerConfirmation KEYPILOT-TEST-DRIVER-RISK
```

检查必须证明：管理员权限、TESTSIGNING 已启用、Secure Boot 已关闭、受信证书链/代码签名 EKU/Trusted Publishers、SYS/CAT 签名状态、x64 PE 以及恢复确认。任一项不满足都会停止且不安装。INF/SYS 的 CAT 成员关系由 Windows 自带的 `PnPUtil /add-driver` 在真正安装时强制验证；前置检查不会以管理员权限运行 SDK/NuGet 目录中的 SignTool。

## 显式安装

只在完整审阅前置检查后执行：

```powershell
.\scripts\Install-KeyPilotTestDriver.ps1 `
  -Platform x64 `
  -IHaveExternalInputAndRecoveryMedia `
  -DangerConfirmation KEYPILOT-TEST-DRIVER-RISK `
  -IUnderstandThisInstallsATestSignedKernelDriver
```

安装器会把 INF/SYS/CAT 复制到管理员专用、拒绝继承写权限的 ProgramData 暂存目录，重做前置检查后才写持久恢复记录并调用 PnPUtil；最后只接受唯一、由系统 DISM 模块验证为 KeyPilot Extension 的新 `oemNN.inf`。

## 运行时紧急旁路

在同一键盘上保持左 Ctrl + 左 Shift + F12 两秒会清除内核租约并强制 fail-open。三个组合键始终透传，保持期间拒绝新租约；松开任一键只允许后续重新获取租约，不会恢复旧规则。要继续测试抑制，请显式关闭后重新开启全局映射；如状态未恢复，重启 KeyPilot。

## 恢复

只使用安装记录中的准确名称：

运行 `Recover-KeyPilotDriver.cmd`，再按提示输入安装记录中的 `oemNN.inf`、确认词 `KEYPILOT-RECOVERY`；在线恢复时第三项留空。

在 WinRE 中还要提供离线 Windows 根，例如：

运行同一脚本，并在第三项提示中只输入离线 Windows 盘符根，例如 `D:\`。恢复脚本故意忽略全部命令行参数，避免 CMD 参数注入。

移除后重启，并恢复 TESTSIGNING、Secure Boot 和本地测试证书设置。
