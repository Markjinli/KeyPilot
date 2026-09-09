# Local test-driver path (self-use only)

This path is deliberately separate from the production installer. It is for a
disposable test machine or a personally controlled handheld only. None of the
scripts enables TESTSIGNING, disables Secure Boot, trusts a certificate, or
installs a driver unless the final, explicitly named install script is invoked
with every confirmation.

## One-time manual preparation

1. Keep an external mouse/touchscreen and Windows recovery media available.
2. Generate a password-protected certificate without touching certificate
   stores:

   ```powershell
   .\scripts\New-KeyPilotTestCertificate.ps1
   ```

3. Using the Windows certificate UI, import only the generated `.cer` into the
   **Local Computer** `Trusted Root Certification Authorities` and
   `Trusted Publishers` stores. Never import or share the `.pfx` private key.
4. Manually disable Secure Boot in firmware and manually enable Windows test
   signing, then reboot. These security changes are intentionally outside this
   project and must be reversed after testing.

The generated files live under ignored `artifacts/test-signing/`; private keys
must never be committed.

## Build, sign, inspect, install

```powershell
.\scripts\Build-Driver.ps1 -Configuration Release -Platform x64
.\scripts\Sign-KeyPilotTestPackage.ps1 `
  -Platform x64 `
  -IUnderstandThisCreatesATestSignedKernelPackage
.\scripts\Test-TestInstallPrerequisites.ps1 `
  -Platform x64 `
  -IHaveExternalInputAndRecoveryMedia `
  -DangerConfirmation KEYPILOT-TEST-DRIVER-RISK
```

The driver build uses the pinned WDK/SDK NuGet 10.0.28000.2526 packages under
`E:\KeyPilotTools\nuget-packages`; it does not use `dotnet` or silently fall
back to an older machine-wide kit.

The preflight is read-only and fails closed unless all of these are proven:
administrator rights, TESTSIGNING enabled, Secure Boot disabled, valid trusted
test-certificate chain and Code Signing EKU, signer in Local Machine Trusted
Publishers, CAT membership for both INF and SYS, correct PE architecture, and
the recovery/external-input confirmations.

Only after reviewing its complete output, installation is an additional,
separate command:

```powershell
.\scripts\Install-KeyPilotTestDriver.ps1 `
  -Platform x64 `
  -IHaveExternalInputAndRecoveryMedia `
  -DangerConfirmation KEYPILOT-TEST-DRIVER-RISK `
  -IUnderstandThisInstallsATestSignedKernelDriver
```

The installer writes a durable recovery record before PnP mutation and accepts
only one new DISM-verified KeyPilot Extension `oemNN.inf`. It never changes BCD,
firmware, or certificate stores.

While testing, holding left Ctrl + left Shift + F12 on the same keyboard for two
seconds is the runtime emergency path. The chord is always passed through; the
driver clears its lease and remains fail-open while it is held. Releasing a key
does not restore the old lease, so explicitly disable/re-enable mappings or
restart KeyPilot before testing suppression again.

## Recovery and cleanup

Use the recorded package name, never a guess:

运行 `Recover-KeyPilotDriver.cmd`，按交互提示输入已验证的 `oemNN.inf` 与 `KEYPILOT-RECOVERY`；在线恢复时离线盘符留空。

From Windows Recovery, add the offline Windows root, for example `D:\`:

在 WinRE 中运行同一脚本，并在提示中输入离线 Windows 盘符根（例如 `D:\`）。脚本不接受命令行参数。

After driver removal and reboot, manually turn TESTSIGNING off, restore Secure
Boot, and remove the local test certificate from both machine stores.
