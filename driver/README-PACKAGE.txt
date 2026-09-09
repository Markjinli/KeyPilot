KeyPilotFilter x64 Release 本地驱动包

已验证的构建门：WDK/SDK NuGet 10.0.28000.2526、/W4、/WX、PREfast、Inf2Cat、InfVerif /w；SYS PE 架构为 x64 (0x8664)。
本包的内核、DriverClient 与 Broker 使用同一固定二进制协议 v2，不兼容旧协议记录。

驱动二进制位于：
  KeyPilotFilter\x64\Release\KeyPilotFilter\

重要：当前 SYS/CAT 未签名、未安装，也没有在目标掌机上通过 Driver Verifier、HLK、睡眠/唤醒、热插拔或压力测试。它不能在正常启用 Secure Boot 的 Windows 上直接加载。

“安装 KeyPilot.cmd”只把 App/Broker 安装到 Program Files，不会安装此驱动。没有安全安装驱动时，用户态动作仍可工作；仅符合条件的普通键盘扫描码可使用用户会话内的兼容抑制，厂商 HID、Consumer Control、手柄和其他不支持来源保持放行。兼容模式不等同内核驱动。

驱动启用后，同一键盘左 Ctrl + 左 Shift + F12 保持 2 秒会清除租约并强制 fail-open。组合键始终透传；保持期间拒绝新租约，松开后旧租约不会自动恢复，需要 App 显式重新启用或重启。

在考虑测试驱动前，必须阅读 TEST-SIGNING.md，准备外接鼠标/触摸、Safe Mode/WinRE 和 Recover-KeyPilotDriver.cmd。随包脚本不会自动关闭 Secure Boot、开启 TESTSIGNING、修改 BCD 或导入证书。
安装脚本会先把 INF/SYS/CAT 复制到管理员专用 ProgramData 暂存目录并重验，随后才把同一份不可由普通用户替换的副本交给 System32 PnPUtil；PnPUtil 负责强制校验 CAT 成员关系。
