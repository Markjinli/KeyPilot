# User-mode driver boundary

`KeyPilot.DriverClient` is the fixed binary boundary for the v2 kernel protocol. It opens the
administrator-only, exclusive `\\.\KeyPilot` device and sends only fixed-size records from
`include/KeyPilotProtocol.h`. It has no installation, test-signing, service, registry, action,
URL, script, or arbitrary-device-path feature.

Protocol v2 accepts suppression rules only. Build them with
`KeyboardRule.ForEveryKeyboard`/`ForDevice` and exactly one `KeyboardScanPrefix`. The client and
kernel reject wildcard prefixes and emergency-path suppression. `ValidateRules` works without a
driver.

Left Ctrl, left Shift, F12, left Alt and E0 Delete are reserved from suppression; right Ctrl and
right Alt remain representable. Holding left Ctrl + left Shift + F12 on one keyboard for two
seconds sets `EmergencyBypassActive`, clears the lease and keeps new lease acquisition blocked
while the chord is held. Releasing a member permits a fresh lease but never restores the old
lease or rules. UI policy must require an explicit re-enable/reconnect instead of silently
reacquiring suppression after this user safety action.

For a Raw Input exact-device selector, pass its `RIDI_DEVICENAME` value to
`KeyPilotDeviceHash.ResolveDeviceInterfaceHash` and then use `KeyboardRule.ForDevice`. The helper
resolves `DEVPKEY_Device_InstanceId` and reproduces the kernel hash byte-for-byte. Treat any
resolution error as an unsupported mapping; never substitute `ForEveryKeyboard`.

The normal `asInvoker` WinUI app must not open this administrator-only client directly in normal
operation. `KeyPilot.DriverBroker` is the small elevated lease/rule/event owner, while
`KeyPilot.DriverBroker.Protocol` is the authenticated UI-side IPC library. See
[`KeyPilot.DriverBroker/README.md`](KeyPilot.DriverBroker/README.md) for the progress-bound
heartbeat, failure behavior, tests, and protected Program Files launch requirement.

Only continuously completed action sequence numbers may advance the kernel ACK. Reading or
submitting an event is insufficient. A heartbeat may carry an unchanged ACK while the UI reports
real bounded progress, but the kernel progress deadline still expires if completed actions do not
advance; that intentionally returns the keyboard to fail-open pass-through.

The timer should call parameterless `Heartbeat()`, which sends the last successful ACK while
holding the same I/O lock. The action-completion path calls `Heartbeat(sequence)`. If a timer
captured an older value just before action completion, the client clamps it to the already
successful local ACK and never sends a regression.
