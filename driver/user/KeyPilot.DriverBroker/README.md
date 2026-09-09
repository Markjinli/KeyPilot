# KeyPilot elevated driver broker

`KeyPilot.DriverBroker.exe` is the only elevated user-mode process. The WinUI app remains
`asInvoker`. The broker owns only the exclusive `KeyPilotDriverClient`, its lease, the fixed
keyboard suppression rules, and the driver event queue. It cannot execute shortcuts, programs,
URLs, scripts, or accept a device path.

## Security and fail-open contract

- The executable manifest is `requireAdministrator` and `WinExe` (no console window).
- The server creates one random named-pipe instance with `PipeOptions.CurrentUserOnly`.
- A 32-byte random token authenticates the first frame. `GetNamedPipeClientProcessId` must also
  equal the original UI PID, and the PID start time must match, so another same-user process
  cannot win a connection race.
- Frames have a fixed magic/version/header, a 32 KiB payload ceiling, known message types,
  exact record sizes, zero reserved fields, at most 512 rules and 64 events per batch.
- Parent exit, pipe disconnect, malformed/unknown messages, blocked writes, stale challenges,
  missing ACKs, driver state loss, queue overflow or progress timeout all leave the session.
  `KeyPilotDriverClient.Dispose()` closes the exclusive handle and releases the kernel lease
  before IPC cleanup.
- The kernel's two-second left Ctrl + left Shift + F12 emergency hold reports
  `EmergencyBypassActive`, clears the lease and is treated as fail-open. The broker does not
  automatically reacquire after this explicit user safety action; releasing the chord only
  permits a later UI-requested fresh session.
- Every driver heartbeat requires a fresh `HealthChallenge` response. The UI must report the
  exact `LastCommittedEventSequence`, a healthy action queue, its continuous
  `LastCompletedActionSequence`, and real action progress. Only
  `LastCompletedActionSequence` is acknowledged to the kernel. A progress counter proves that a
  bounded action is moving but never advances the kernel ACK; the kernel progress deadline can
  therefore still force fail-open during a long/stalled macro.

## Protected launch requirement

Never automatically elevate a broker from the repository, Downloads, Documents, `%TEMP%`, or
another user-writable directory. This development build is unsigned and is intentionally not
started by the tests.

Production packaging must place the complete broker output at
`<Program Files>\KeyPilot\broker\KeyPilot.DriverBroker.exe` with a sibling
`KeyPilot.DriverBroker.sha256` containing exactly 64 hexadecimal characters. An App-local
`broker\` location is allowed only when the resolved App root is itself under Program Files. The
UI should embed/receive the expected SHA-256 for that exact broker version.
`BrokerProcessLauncher.StartElevated` refuses paths outside Program Files and refuses a hash
mismatch before invoking `runas`. A signed release can add Authenticode verification as a second
check; it does not replace the protected directory requirement.

## Minimal UI integration

Reference:

```xml
<ProjectReference Include="..\..\driver\user\KeyPilot.DriverBroker.Protocol\KeyPilot.DriverBroker.Protocol.csproj" />
```

Then:

1. Create `BrokerLaunchParameters.CreateForCurrentProcess()`.
2. Start the installed broker with
   `BrokerProcessLauncher.StartElevated(path, expectedSha256, parameters)` only after an operation
   needs suppression.
3. Call `KeyPilotBrokerConnection.ConnectAsync(parameters, timeout)`.
4. Consume the single-reader `ReadMessageAsync` stream. It yields only `Status`, `EventBatch`, and
   `HealthChallenge`; request `Ack` frames are correlated internally.
5. Apply policy with `ConfigureRulesAsync(generation, rules)`.
6. Answer a challenge only after the mapping state machine has committed every event through its
   sequence. Advance the completed-action watermark for no-action events immediately; otherwise
   advance it only after actions finish. Increment `ActionProgressCounter` only for real macro
   steps/delay completions, never from a timer.
7. On cancellation or shutdown, dispose the connection. `ShutdownAsync` is optional courtesy;
   disconnect alone must always be safe.

`BrokerStatusCode` provides `Starting`, `LeaseActive`, `RulesActive`, `FailOpen`, `ShuttingDown`,
and `Fatal`. Callers should separately classify `UnauthorizedAccessException` (authentication or
protected-path failure), `BrokerProtocolException` (malformed/inconsistent peer),
`TimeoutException` (no drain/ACK), `EndOfStreamException` (disconnect/parent exit), and driver
`Win32Exception` surfaced through a `FailOpen` status.

## Build and tests

```powershell
dotnet build .\driver\user\KeyPilot.DriverBroker\KeyPilot.DriverBroker.csproj -c Release
dotnet run --project .\driver\tests\KeyPilot.DriverBroker.Tests\KeyPilot.DriverBroker.Tests.csproj -c Release
```

The tests use fake driver and in-memory transports. They never open `\\.\KeyPilot`, request UAC,
install a driver, or enable test signing.
