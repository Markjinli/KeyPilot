# 用户态动作执行边界

当前 Windows 用户态动作流水线由 `MappingActionPlanner`、`WindowsActionExecutor` 和三个真实后端组成。执行器串行运行已展开的动作计划，同一时刻只执行一个计划。

## 键盘注入

- `WindowsSendInputBackend` 仅接受键盘 Set-1 扫描码和 Windows 虚拟键。
- E0 扩展扫描码使用 `KEYEVENTF_EXTENDEDKEY`；带 `PREFIX=0004` 的 Pause 会改用 `VK_PAUSE`。其他 E1 或缺少前缀的歧义 `0x45` 会被拒绝，绝不伪装成 E0。
- 每个事件的 `dwExtraInfo` 都写入进程级随机标记，供 Raw Input 链路忽略自身注入。该标记只防止递归，不是安全凭证。
- 计划异常或取消后，执行器以不带取消令牌的方式反序尝试释放仍按下的键；释放失败不会掩盖原始异常。
- SendInput 不能可靠生成厂商 HID、Consumer Control 或 XInput 设备报告。这些目标会被明确拒绝，等待虚拟 HID/驱动后端。
- SendInput 受 Windows UIPI 完整性级别限制；普通权限进程不能保证向更高权限窗口注入。

## 程序、文件与网址

- 程序和文件路径必须是已存在的绝对本地或 UNC 文件路径；工作目录同样必须已存在且为绝对路径。
- `.exe` 直接启动并使用 `ProcessStartInfo.ArgumentList`；文件名不会与参数拼接。
- 普通文件只调用 Windows 的 `open` 文件关联，而且禁止附带参数。ShellExecute 成功转交给系统后可能不返回进程句柄，这不被误判为失败。
- 裸域名会自动补为 `https://`；HTTP(S) 以外只接受 Windows 当前用户或机器已注册的 URL Protocol。`file:`、本地/UNC 路径及 shell/命令/脚本类 scheme 会被拒绝。
- 取消只能阻止尚未开始的外部动作；操作系统成功创建进程或交给文件关联后，取消不会尝试终止用户程序。

## 脚本

- 只接受已存在的 `.bat`、`.cmd`、`.ps1` 和 `.exe`。
- PowerShell 使用系统 `powershell.exe -NoProfile -NonInteractive -File`，参数逐项加入 `ArgumentList`，不会绕过本机执行策略。
- BAT/CMD 必然需要命令解释器，因此额外拒绝 `& | < > ^ ( ) % !`、字面引号、控制字符和空参数。系统 `cmd.exe` 与固定开关分别加入 `ArgumentList`；`/c` 后只有一个经过逐项验证和引用的完整命令操作数。
- 不支持把任意命令文本交给 `cmd /c` 或 PowerShell `-Command`。

这些后端只负责执行用户已配置的动作，不负责阻止原始物理输入。强保证的原键抑制由具备超时失联放行机制的 fail-open 驱动完成。驱动不可用时，App 另有仅覆盖普通键盘扫描码的 `WH_KEYBOARD_LL` 兼容抑制：状态机在提交前明确拒绝时才回放原边沿；提交后的动作失败不会再回放当前原键，而会关闭未来抑制和全局映射。这样优先保证不会出现“动作已部分执行又输入原键”。它不覆盖厂商 HID、Consumer Control、手柄、安全桌面或所有完整性级别场景，不能视为驱动替代品。
