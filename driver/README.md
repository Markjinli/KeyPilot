# KeyPilot KMDF keyboard filter

This folder contains a source-complete Windows keyboard upper-filter prototype.
It is intentionally **not installed** by a build. The kernel component only
matches keyboard scan records, passes or suppresses the original record, and
queues a fixed event. Programs, URLs, scripts, shortcut injection, UI and HID
output remain user-mode responsibilities.

## Fail-open contract

- Startup has no lease and passes every input.
- Suppression requires one exclusive administrator-owned handle, a live
  250–5000 ms lease, and a fully validated immutable ruleset generation.
- Rules are copied to an inactive fixed buffer and published in one locked
  swap. There is no partial live-table update and no allocation in the input
  callback.
- A first Down stores lease, generation, rule ID and suppression decision.
  Repeat and Up use that stored decision even after a normal ruleset swap.
- An expired heartbeat, client close, sleep/D0 exit, malformed policy message,
  stale generation, press-table exhaustion or event-ring overflow invalidates
  the lease. Current and subsequent input is passed through.
- A timer heartbeat cannot hide a blocked action loop. Every queued event starts
  a separate progress deadline. Only a monotonic ACK for events already read and
  successfully dispatched advances that deadline; stalled dispatch therefore
  fails open within one granted lease interval even if timer heartbeats continue.
- `KEYBOARD_OVERRUN_MAKE_CODE` or press-table exhaustion clears all tracked
  presses, exposes `InputTrackingLost`, fails open, and rejects new leases until
  that keyboard completes a D0 power cycle or is replugged.
- The event ring is bounded (1024 records). The driver never waits for user
  mode from the keyboard callback.
- Unknown and unmapped input always passes through.
- Suppressing left Ctrl, left Alt, E0 Delete, left Shift, or F12 is rejected.
  This preserves Ctrl+Alt+Delete and the independent emergency chord while
  right Ctrl/right Alt remain mappable.
- Holding left Ctrl + left Shift + F12 on one keyboard for two seconds clears
  the lease and keeps the driver fail-open while the chord remains held. The
  three chord keys are always passed through. Releasing any member unlocks new
  lease acquisition; it does not restore the already-cleared lease by itself.

An emergency transition may pass an orphan Up whose Down was suppressed. This
is deliberate: after loss of the safety lease, pass-through is more important
than retaining suppression. It cannot leave Windows believing that key is held,
because Windows never received the suppressed Down.

## Protocol

[`include/KeyPilotProtocol.h`](include/KeyPilotProtocol.h) is the ABI source of
truth. `DeviceHash == 00…00` is an explicit all-keyboards selector; non-zero
hashes are exact. Protocol v2 requires `RuleFlags == SuppressOriginal` and one
exact scan prefix: Normal `(Required=0, Ignored=E0|E1)`, E0
`(Required=E0, Ignored=E1)`, or E1 `(Required=E1, Ignored=E0)`. Flag wildcards
are rejected. User mode should use `KeyboardRule.ForEveryKeyboard` or
`KeyboardRule.ForDevice`, then call `KeyPilotDriverClient.ValidateRules`. The
control device is `\\.\KeyPilot`, exclusive, and ACL'd to Administrators and
SYSTEM. The reference .NET boundary is in `user/KeyPilot.DriverClient`.

## Build and verification

Required components:

- Visual Studio 2026 with C++ Spectre libraries and the
  `Component.Microsoft.Windows.DriverKit` integration
- pinned `Microsoft.Windows.WDK.x64`/`.ARM64` and matching
  `Microsoft.Windows.SDK.cpp.*` NuGet packages, version `10.0.28000.2526`
- .NET 10 SDK for the user boundary and policy-model tests

`Build-Driver.ps1` uses Visual Studio MSBuild, not `dotnet`, restores the driver
packages to `E:\KeyPilotTools\nuget-packages`, and parses `project.assets.json`
before compilation to reject any WDK/SDK version other than 28000.2526. The
local older Windows Kit is not accepted as a substitute. Release packages are
written to `KeyPilotFilter\<platform>\Release\KeyPilotFilter`; ordinary builds
explicitly disable signing, so the resulting SYS and CAT remain unsigned until
one of the separate, explicit signing workflows is used.

Commands run no installation:

```powershell
.\scripts\Build-Driver.ps1 -Configuration Release -Platform x64
dotnet run --project .\tests\PolicyModelTests\PolicyModelTests.csproj
.\tests\Test-DriverStaticModel.ps1
.\scripts\Test-InstallPrerequisites.ps1 -PackageDirectory <signed-package>
```

Run Driver Verifier and Static Driver Verifier in a disposable test machine
before any physical handheld. The checked-in INF is a demand-start Extension
INF, so it augments rather than replaces the inbox keyboard package, and uses
the Windows 10 1903+ declarative `AddFilter` model instead of editing
`UpperFilters` directly. It
supports x64/ARM64 HID system keyboards plus legacy `*PNP0303`; it still requires a
proper Microsoft-accepted production/attestation signature on normal secured
Windows.

For a self-use test-signed build, follow [`TEST-SIGNING.md`](TEST-SIGNING.md).
That path is intentionally separate and requires TESTSIGNING to have been
enabled and Secure Boot disabled manually. No KeyPilot script changes either
setting or imports a certificate.

## Installation and recovery boundary

No script enables TESTSIGNING, disables Secure Boot, changes code-integrity
policy, or installs by default. `Install-KeyPilotDriver.ps1` refuses to proceed
without an explicit switch, a valid kernel-policy CAT signature, and a confirmed
disabled TESTSIGNING state. The fixed System32 PnPUtil call enforces CAT membership
for INF and SYS during installation. It records an
`oemNN.inf` only when exactly one new DISM-verified package appears after
installation. Keep an external mouse/touchscreen and
`scripts/Recover-KeyPilotDriver.cmd` available before testing.

Installation first writes a durable `Pending` recovery record; after PnPUtil it
records only a DISM-verified KeyPilot Extension `oemNN.inf`. Uninstall/recovery
re-verifies the original INF, provider and class and requires an explicit token,
so an arbitrary OEM package name is never deleted merely because it matches the
filename pattern. In normal/Safe Mode or Windows Recovery, run
`Recover-KeyPilotDriver.cmd` without arguments and enter the verified `oemNN.inf`,
confirmation token, and optional offline Windows drive root at its prompts. The
batch intentionally ignores every command-line argument to remove CMD parameter
injection. Offline mode uses DISM `/Image` rather than modifying WinRE itself.
Reboot after removal.

Recovery records are written with create-new temporary files and write-through
flushes under `%ProgramData%\KeyPilot\Recovery`; reparse-point paths are
rejected. WinRE inspection uses a private temporary directory under
`%SystemRoot%\System32\config`, not the caller-controlled `%TEMP%` directory.

## Known limits

- The x64 Release package passes WDK 10.0.28000.2526 compilation with `/W4`,
  warnings-as-errors and PREfast, plus Inf2Cat with no warnings and InfVerif
  `/w`. It has not yet passed SDV, HLK, sleep/resume, hot-plug, Secure Attention
  Sequence, Driver Verifier, or physical-device stress testing.
- It filters keyboard-class scan input only. Vendor HID/consumer-control and
  gamepad suppression require separately audited filters; they must not be
  inferred from this driver.
- Protocol v2 intentionally rejects observation-only rules. Every published
  rule suppresses its match and therefore must produce a queued event first.
  This avoids committing a user-visible event before kbdclass consumes a
  pass-through packet.
- The per-device hash is derived from the stable PnP instance ID when available,
  with the PDO name only as a fail-safe fallback. User mode reconstructs the
  normal path safely with `KeyPilotDeviceHash.ResolveDeviceInterfaceHash`, which
  resolves a Raw Input interface through Configuration Manager and hashes the
  exact property bytes used by the kernel. Resolution failure must reject an
  exact-device rule; it must never widen that rule to all keyboards.
- Rule and pressed-key lookup is allocation-free and linearly bounded at 512
  entries per packet. WDK analysis and physical high-rate stress testing remain
  required before increasing that limit.
- Production installation remains subject to Windows driver-signing policy.
- The placeholder ExtensionId must be registered to the publisher before a
  Hardware Dev Center submission; changing it after release would create a
  second independent extension package.
