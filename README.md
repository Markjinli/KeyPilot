# KeyPilot

KeyPilot 是仅面向 Windows 的本地按键采集、映射与动作工具。WinUI 3 界面以仓库根目录的
[`handheld-key-mapper-preview.html`](../handheld-key-mapper-preview.html) 为交互原型：采集页与映射页共用完整键盘、手柄和 10 个特殊键槽位，支持右键创建/编辑映射并突出显示已映射按键。

## 当前已实现

- 104 键键盘、14 个 XInput 数字按键、LT/RT、左右摇杆八方向、四个整圈手势和 10 个特殊键槽位的 WinUI 工作台。摇杆按下明确显示为 L3/R3；界面实时显示两个摇杆的角度、动态死区/中心漂移和实测有效角分辨率估算。每个方案还可选择左或右摇杆连续模拟相对鼠标移动，并独立设置速度与死区。
- 左右摇杆可从任意起始方向识别顺时针/逆时针 360°：一圈触发一次、连续两圈触发两次。识别包含半径门限、角度噪声过滤、跨 0/360、方向锁定与反转抵消，普通推动不会被当作整圈。
- 特殊槽位可右键录制完整 2 秒按键过程（最多 32 个按下/释放边沿）。全体按键存在共同按住区间时保存为无序组合，否则保存为带相对时间的有序序列；每个成员保留精确设备身份。两类逻辑输入都安全透传原始成员。
- Raw Input 键盘、限定范围的 Consumer Control/厂商 Raw HID 采集，以及 4 个 XInput 用户槽轮询。Raw HID 使用“变化 → 恢复 → 同一变化再次出现”的显式确认流程，不会因噪声自动占槽。
- 可显式启动一次 10 秒输入诊断，记录 XInput 原始按钮位/模拟阈值边沿、Raw Keyboard 物理码及 Raw HID 字节变化，用于区分固件映射、厂商 HID 与 OEM/ACPI 专用事件。诊断有 256 条/64 KiB 上限，设备路径会哈希脱敏，不记录翻译后的输入文字或完整 HID 报告。
- 配置保存到 `%LOCALAPPDATA%\KeyPilot\config.json`。保存采用临时文件、落盘刷新和原子替换；每次替换保留上一份 `config.json.bak`。schema 1/2/3/4/5 会无损迁移到支持外观主题的 schema 6；自动切换、摇杆鼠标在旧配置迁移后都保持关闭，外观默认「夜航」。缺失/未来版本及未知字段会被拒绝，损坏或不可读取时不会静默覆盖原文件。
- 侧栏可切换 8 套工作台外观（夜航、雾白、曜石、暮紫、海雾、赤霞、竹影、宣纸），选择立即生效并写入配置。
- 可复制、重命名并选择多个映射方案，也可一次设置“前台进程名称 → 方案”。启用后，ChatGPT、Chrome、游戏等软件成为前台时会自动应用对应方案；进程名匹配不区分大小写并兼容可选的 `.exe`，未匹配或读取失败时安全回到默认编辑方案。自动切换只替换内存中的生效方案，不随每次窗口变化写磁盘；旧前台上下文中排队或正在执行的动作会取消，迟到的抑制事件不能进入新方案。
- 顶栏全局映射开关会持久化；默认关闭。开启后才会执行映射并尝试启用与当前来源相符的原键抑制，关闭时立即回到放行状态。
- 单击、双击、长按、按下、释放触发状态机，以及串行、有界的动作执行队列。
- 八类映射动作：快捷键、按键/组合键、媒体控制、音量控制、应用/文件、网址/已注册协议、脚本/命令、手柄按键。快捷键可固定录制 2 秒；主页可复制不含设备路径的版本化按键信息并粘贴为键盘输出。媒体键使用 Windows 标准虚拟键，精确音量使用 Core Audio；裸域名自动补 `https://`。
- KMDF 键盘 upper-filter 的完整源码、v2 固定二进制协议、.NET 驱动客户端、策略模型和静态检查。
- 独立的提权 Driver Broker：普通 WinUI 保持 `asInvoker`；默认抑制通道通过受保护部署定位器连接 Broker，直连客户端只保留给测试/诊断。Broker 只持有驱动句柄、租约、规则和事件队列，不执行任何宏或外部程序。
- 测试签名、安装前只读检查、显式安装/卸载和恢复脚本。所有高风险步骤均与常规构建分离。
- 侧栏只有采集与映射。索尼 DualShock/DualSense HID 和 RC003 按键是同一画布上的一等节点；「手柄按键」在已安装 ViGEmBus 时输出虚拟 Xbox 360。不再 HWND 嵌入 DS4Windows 或无线麦。
- 「已连接设备的麦克风」只列出已经连上的 DualSense USB 麦和小米遥控器 2 Pro。遥控器的麦克风按真实设备显示，可手动命名，并一键设为 Windows 录音设备。笔记本阵列和 CABLE Output 名称不会出现在这个列表里。

## 原键抑制与 fail-open

用户态 `SendInput` 无法可靠吞掉物理按键。KeyPilot 的强保证路径由 KMDF 键盘过滤器负责：未知/未映射输入默认放行；只有有效独占租约和完整规则代存在时才抑制匹配扫描码并投递事件。

以下情况都会释放或失效租约并恢复原始输入：父 UI 退出、Broker 断管、协议错误、心跳/动作完成进度超时、事件队列溢出、按键跟踪丢失、睡眠电源转换或客户端关闭。Broker 只把“动作已连续完成”的事件序号 ACK 给内核；仅收到事件或把动作放入队列不算完成。

驱动不可用时，App 会对“所有键盘 + 可精确表示的扫描码 + 要求屏蔽原输入”的映射启用 `WH_KEYBOARD_LL` 兼容抑制。它是用户会话内的尽力而为模式，不等同内核驱动，不能区分同一扫描码来自哪一把键盘，也不能覆盖安全桌面、任意厂商 HID、Consumer Control 或手柄。状态机明确拒绝提交时会回放原边沿；一旦提交，后续动作失败也绝不回放当前原键，只关闭未来抑制并持久化关闭全局映射，避免“宏 + 原键”双触发。关联 token 跨方案切换保留并有界过期，长宏不会堵住后续输入事件泵。

内核固定保留左 Ctrl、左 Shift、F12、左 Alt 与 E0 Delete，不允许规则抑制。把**同一键盘**的左 Ctrl + 左 Shift + F12 保持两秒会清除租约并强制 fail-open；三个组合键始终透传，保持期间拒绝新租约。松开任一键只解除物理旁路锁定，不会恢复旧租约；当前 App 不会自动重租，需显式关闭后重新开启全局映射，必要时重启 App。

Broker 只能从管理员保护的 Program Files 安装目录启动，并要求可信 SHA-256。默认部署约定为
`<Program Files>\KeyPilot\broker\KeyPilot.DriverBroker.exe` 和同目录的
`KeyPilot.DriverBroker.sha256`（恰好 64 个十六进制字符）；App 自身安装目录下的 `broker\` 仅在整个 App 同样位于 Program Files 时才允许。仓库、Documents、Downloads 或临时目录中的开发构建不会被自动提权运行。

## 当前限制

- x64 Release 驱动已实际通过 WDK/SDK NuGet `10.0.28000.2526` 编译、`/W4 /WX`、PREfast、Inf2Cat 和 `InfVerif /w`，PE 架构为 `0x8664`；但 SYS/CAT 仍未签名，驱动未安装、未加载，也未获得微软签名。正常安全启动环境的生产部署需要外部的微软接受签名流程；本地测试签名则需要用户自行准备证书、关闭 Secure Boot 并启用 TESTSIGNING。
- 尚未完成 Static Driver Verifier、Driver Verifier、HLK、睡眠/热插拔、SAS 或掌机实机压力验证；这些仍需隔离测试机与目标硬件。
- 当前过滤器只处理键盘 class 扫描码。厂商 HID、Consumer Control 和手柄的原输入抑制仍未实现。
- 组合键和有序输入模式不会请求原键抑制。现有驱动在单个扫描码到达时就必须决定放行/丢弃，无法在模式完成后无损回放；因此像固件输出的 `Ctrl+1` 可以被识别并执行新动作，但原快捷键是否同时生效仍取决于 OEM 是否可改写为 F13–F24/厂商 HID。
- 没有虚拟 HID 输出。映射到普通键盘目标可使用 `SendInput`；映射到手柄按键在已安装 ViGEmBus 时走虚拟 Xbox 360。无法表示的特殊 HID 目标会明确失败，不会伪装成其他输入。
- Raw HID 字节差分是低置信度模式，并未完整解析 HID descriptor、`ReportId`、`DataIndex` 或 `LinkCollection`。
- `SendInput` 受 UIPI 完整性级别限制，普通权限 UI 不能保证向更高权限窗口注入。
- 未安装并安全启用驱动时，用户态动作仍可执行；只有上述普通键盘兼容模式会尽力抑制可表示的键盘来源，其余来源保持放行。
- 本地发布包的 CMD/PowerShell 安装引导未做商业代码签名。它会校验整包并在 Program Files 中复验/保护目标，但不声称抵抗同一账户恶意进程在 UAC 前后竞态替换解压目录；严格覆盖该威胁模型需要外部签名的 MSI/MSIX/Bootstrapper。

## 构建

常规 Release 构建不会安装、启动或签名驱动：

```powershell
.\build.cmd
```

它依次执行源码编码/XML 检查、Core/Platform/Policy/Broker 测试、驱动静态模型检查，并构建 Broker 与 WinUI App。可通过 `-DotnetPath` 或 `KEYPILOT_DOTNET` 指定 .NET SDK。

只有工具链完整、并且明确需要验证 KMDF 编译时才添加：

```powershell
.\build.cmd -BuildDriver
```

该开关只构建并运行 INF 检查，不安装驱动、不修改启动配置。开发环境与危险操作边界见 [`docs/development-setup.md`](docs/development-setup.md)，驱动设计与恢复流程见 [`driver/README.md`](driver/README.md)。

## 可重复发布

生成经过整包校验的 Windows x64 自包含目录、ZIP 与 ZIP SHA-256：

```powershell
.\publish.cmd -BuildDriver
```

发布命令先执行全部构建/测试，再分别 `dotnet publish` App 与 Broker、编译驱动、生成 Broker 固定哈希、`release.json` 和覆盖每个负载文件的 `MANIFEST.sha256`。发布包仅保留英文 `en-us` 与简体中文 `zh-CN` 的 WinUI 语言资源，并包含嵌入 EXE、窗口和开始菜单快捷方式的多尺寸 `KeyPilot.ico`。它先在临时目录完成验证，成功后才替换 `artifacts\KeyPilot-win-x64`，防止失败发布破坏上一个成品。`tools\Test-ReleasePackage.ps1` 可独立重验解压后的目录。

发布包提供两个互不混淆的入口：`启动 KeyPilot.cmd` 可免安装使用用户态功能；`安装 KeyPilot.cmd` 经 UAC 把完整 App/Broker 原子部署到受保护的 `Program Files\KeyPilot`、重置并检查 ACL、验证 SHA-256 并创建开始菜单快捷方式。后者仍然不会安装驱动或修改任何系统安全设置。

最终交付前可附加 `-RuntimeSmoke`：它只启动本次 staging 中的 App，等待真实可见窗口，发送 `WM_CLOSE` 并确认进程在限时内以 0 退出，同时确认既有 `startup-error.log` 没有变化；若退出失败会报告新进程 PID而不会强杀。

## 目录

```text
KeyPilot/
  src/KeyPilot.App/                  WinUI 3 工作台与交互接线
  src/KeyPilot.Core/                 配置、输入、触发与动作模型
  src/KeyPilot.Platform.Windows/     Raw Input、XInput、存储和真实动作后端
  driver/KeyPilotFilter/             KMDF 键盘 upper-filter 源码与 INF
  driver/user/                       驱动客户端、安全 Broker 与 IPC 协议
  driver/scripts/                    构建、签名、安装、卸载和恢复工具
  tests/                              Core 与 Windows 平台测试
  docs/                               原型契约、架构和开发说明
```
