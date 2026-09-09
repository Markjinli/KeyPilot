# KeyPilot 架构

## 运行时边界

```text
物理键盘 ──> KMDF upper-filter ── 未映射/失联 ──> kbdclass / Windows
                    │
                    └─ 已映射且租约有效：抑制原记录 + 写入有界事件环
                                               │
                                               v
                                  提权 Driver Broker（仅驱动 I/O）
                                               │ 已认证命名管道
                                               v
Raw Input / XInput / Raw HID ─────────> WinUI asInvoker
                                               │
Windows 前台窗口进程 ───────> 选择内存中的生效方案
                                               │
                                  触发状态机与动作规划
                                               │
                             SendInput / 程序 / URL / 脚本

驱动不可用时的键盘兼容支路：
WH_KEYBOARD_LL ── 可表示且要求抑制的普通键盘扫描码 ──> WinUI 动作队列
             └─ 不确定/故障/未映射 ──> Windows（必要时回放后关闭全局映射）
```

驱动未安装或 Broker 不可用时，WinUI 仍可通过 Raw Input/XInput 采集并执行用户态映射；对所有键盘范围、可精确表示的扫描码映射可启用用户会话内的兼容抑制，但它不能提供内核级保证或设备级区分。驱动路径只接管已安全发布的键盘扫描码规则；厂商 HID、Consumer Control 和手柄继续走只读采集与用户态动作路径。

## 组件

### WinUI 工作台

- 采集和映射共用同一组节点：104 键键盘、14 个 XInput 数字按键、LT/RT、左右摇杆八方向、四个整圈手势、10 个特殊键槽位。模拟量方向使用滞回边沿；独立分析器估算角度、中心漂移、动态死区和有效角分辨率，并以带半径/噪声/反转门限的累计角算法产生顺逆时针 360° 边沿。
- 特殊槽位可在固定 2 秒窗口内保存最多 32 个原子边沿：全体成员存在共同按住区间时成为无序 `InputChord`，其余平衡边沿成为带相对时间的 `InputSequence`。成员各自保留 `ExactDevice`/`AnyOfKind` 语义；两类来源均不请求原键抑制。
- 映射可在首页通过右键菜单编辑；已映射节点在采集页和映射页使用不同强度的视觉提示。
- 方案管理抽屉可复制、重命名、选择默认编辑方案，并持久化不区分大小写的“进程名 → 方案 ID”绑定。WinUI 通过 `EVENT_SYSTEM_FOREGROUND` 立即读取前台窗口所属进程，并以 350ms 轮询兜底；每次前台进程上下文变化都会推进内存运行时 revision，并原子替换映射、Broker 规则和兼容抑制配置，读取失败或未匹配时回到持久化的 `ActiveProfileId`。
- UI 只管理配置、状态机和用户会话内动作，不以管理员身份常驻。
- 自身 `SendInput` 事件携带进程随机标记；合成 Raw Input 包即使 `hDevice == NULL` 也会保留该标记并在进入映射前被过滤。
- 顶栏全局映射开关默认关闭并持久化。启用时优先使用活动的内核规则；驱动不可用时才为符合边界的普通键盘规则启用 `WH_KEYBOARD_LL` 兼容抑制。
- 兼容钩子不接受精确设备规则，并固定放行左 Ctrl、左 Shift、F12、左 Alt 与 E0 Delete。队列、订阅者或动作完成失败时尝试回放原始边沿并持久化关闭全局映射。

### Core

- `InputSource` 由设备选择器和控制标识组成，可表示键盘扫描码、虚拟键、XInput 数字按钮、阈值化的扳机/摇杆方向、摇杆整圈、低置信度 HID 限定串、无序组合和有序输入模式。
- `MappingTriggerStateMachine` 处理单击、双击、长按、按下、释放、组合/序列匹配及重复边界。
- `MappingActionPlanner` 将快捷键、复制的逻辑按键、媒体/音量和宏展开为不可变操作序列，并规划异常时的反向按键释放。
- Core 不依赖 Win32、文件系统、进程或驱动。

### 配置持久化

- `LocalKeyPilotConfigurationStore` 使用 `%LOCALAPPDATA%\KeyPilot\config.json`。
- 单进程写入由信号量串行化；内容先写临时文件并 `Flush(true)`，再使用原子替换/移动提交。
- 只有最新保存请求的磁盘提交成功后才更新运行状态；原子替换保留上一份 `config.json.bak`。无效、未知/未来版本或不可读取的现有配置会阻止覆盖，避免把可恢复数据变成默认空配置。
- `WorkspaceConfigurationAdapter` 恢复活动方案、已知键盘/XInput 数字与模拟方向映射、10 个特殊键槽位和八类编辑器动作；UI 无法表示的高级字段按原值透传，而不是保存时丢弃。
- schema 4 保存自动切换开关与应用方案绑定；schema 5 为每个方案保存默认关闭的摇杆鼠标来源、速度与死区；schema 6 保存工作台外观主题标识（默认夜航）。schema 1/2/3/4/5 迁移时使用安全默认值。前台切换不修改持久化的 `ActiveProfileId`，也不触发磁盘写入。

### Windows 动作执行

- `WindowsSendInputBackend` 支持 Set-1 扫描码和 Windows 虚拟键；E0 正确设置扩展标志，Pause 使用 `VK_PAUSE`，歧义或其他 E1 输入被拒绝。
- `WindowsExternalActionBackend` 使用结构化 `ProcessStartInfo` 启动 EXE/文件；裸域名补为 HTTPS，自定义 URI 仅允许 Windows 已注册的 URL Protocol，并拒绝本地路径及脚本/命令型 scheme。
- `WindowsSystemControlBackend` 通过带递归标记的标准媒体/音量虚拟键执行相对控制，通过默认播放端点的 Core Audio 接口设置 0–100% 精确音量。
- 脚本仅接受存在的 BAT、CMD、PS1 和 EXE。PowerShell 使用 `-File`；BAT/CMD 拒绝命令元字符和控制字符，不接受任意 `-Command`/`cmd /c` 文本。
- 动作在有界 FIFO 中执行；计划预检失败不产生副作用，异常/取消时尽力释放已经按下的合成键。
- 用户态动作后端不负责原键抑制。手柄逻辑输出在 ViGEmBus 可用时由虚拟 Xbox 360 后端执行；虚拟 HID 仍未实现。

### KMDF 键盘过滤器

- Extension INF 将 KeyPilot 作为键盘设备的 upper filter；内核只匹配扫描记录、放行或抑制，并向固定事件环写入事件。
- 默认无租约、无规则并完全放行。规则使用固定大小双缓冲并一次性切换，输入回调不分配内存、不等待用户态。
- Down 保存租约、generation、rule ID 和抑制决策；Repeat/Up 沿用同一按压决策，避免规则切换造成半次按压。
- 租约为 250–5000 ms；事件环最多 1024 条，规则最多 512 条。左 Ctrl、左 Shift、F12、左 Alt 和 E0 Delete 的抑制规则会被拒绝，右 Ctrl/右 Alt 仍可映射。
- v2 心跳同时携带单调完成 ACK。租约心跳不能掩盖未完成动作：未推进完成水位时，独立 progress deadline 仍会触发 fail-open。
- 同一键盘的左 Ctrl + 左 Shift + F12 保持两秒会清除全局租约；三个组合键始终透传，保持期间内核拒绝新租约。松开只允许后续重新获取租约，旧租约和规则不会自动恢复。

### 提权 Broker

- `KeyPilot.DriverBroker.exe` 为独立 `requireAdministrator`、`WinExe` 进程；只创建固定 `KeyPilotDriverClient()`，不接受 device path、程序、URL、脚本或命令。
- 管道名和 32 字节 token 使用强随机数；服务端限制单客户端，并启用 `PipeOptions.CurrentUserOnly` 与 `FirstPipeInstance`。
- 握手同时校验 token、父进程 PID/启动时间和 `GetNamedPipeClientProcessId` 返回的真实客户端 PID。
- UI 在连接后还用 `GetNamedPipeServerProcessId` 校验管道服务端就是本次由它启动的 Broker PID，拒绝同用户进程抢建的假管道。
- 协议固定 magic/version/消息类型/记录长度，payload 最大 32 KiB，单批最多 64 个事件。
- 每次内核 heartbeat 前必须收到当前 Health challenge 的 ACK。`LastCommittedEventSequence` 表示 UI 状态机已接收；只有连续 `LastCompletedActionSequence` 会提交给内核。动作 progress counter 只能证明有界工作仍在前进，不能伪造完成。
- 父进程退出、断管、写阻塞、陈旧 ACK、协议错误或驱动异常时，Broker 先关闭驱动客户端释放租约，再尝试报告诊断。
- 生产启动必须来自管理员保护的 Program Files 目录，并与受信任的构建期 SHA-256 匹配。开发目录的 Broker 不应自动 `runas`。
- 默认定位器检查 `<Program Files>\KeyPilot\broker\KeyPilot.DriverBroker.exe` 与同目录
  `KeyPilot.DriverBroker.sha256`（64 个十六进制字符）；也可检查 App 安装目录的 `broker\`，但最终解析路径仍必须位于 Program Files。找不到或校验失败时保持 fail-open，不回退到开发目录或自动管理员直连。

## 配置到抑制规则

只有全局开关与当前生效方案均启用、要求 `SuppressOriginal`、可精确表示为键盘扫描码和 Normal/E0/E1 前缀的映射才进入驱动规则表。当前生效方案可能来自持久化的默认方案，也可能来自前台进程绑定。`AnyOfKind` 使用全零设备哈希；精确键盘来源必须把 Raw Input 接口解析为与内核一致的 PnP 实例哈希，解析失败即拒绝，不会扩大成所有键盘。无法安全表达的 HID、手柄、歧义前缀、超限规则或保留键会被跳过并保留用户态映射。

规则被内核原子接受后，匹配输入只从驱动事件通道执行，Raw Input 副本不会再次执行。配置切换会重置映射状态机并取消旧前台上下文的排队/运行中动作；驱动、Broker、兼容钩子和映射运行时共同校验 revision。旧 policy/generation 的迟到事件不会 ACK 或进入新方案，而是使抑制链路 fail-open；兼容钩子按原顺序回放尚未提交的旧边沿。

## 尚未验证或未实现

- 不存在可声称已经安装、生产签名或获得微软签名的驱动包。
- x64 Release 已实际通过 WDK `10.0.28000.2526`、PREfast、Inf2Cat 和 `InfVerif /w`；这仍不能替代 SDV、HLK、Driver Verifier 或实机验证。
- 当前内核过滤范围仅为键盘 class；Consumer Control、厂商 HID 和 XInput 抑制需要独立设计与审计。
- 虚拟 HID 输出尚未实现。虚拟 Xbox 360 输出在 ViGEmBus 存在时可用，并排除自己的虚垫以免递归采集。
- 组合来源固定保持原始成员透传；当前逐扫描码驱动无法为未完成组合安全恢复已吞输入。
- 用户态兼容抑制不能替代驱动：它不区分物理键盘，受用户会话、完整性级别与安全桌面边界限制，并只覆盖可表示的普通键盘扫描码。
- 原始 HID 采集仍是字节差分启发式，必须在目标掌机上校准描述符和报告语义。
- Secure Attention Sequence、睡眠恢复、热插拔、高速重复和设备卸载路径仍需隔离测试机验证。
