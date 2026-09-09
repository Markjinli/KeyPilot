using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Actions;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class WindowsActionBackendTests
{
    public static async Task ScanCodeSendInputIsExactAsync()
    {
        var native = new RecordingSendInputNative();
        var backend = new WindowsSendInputBackend(native);

        await backend.InjectAsync(
            Request(
                new InputControlId
                {
                    Kind = InputControlKind.KeyboardScanCode,
                    Code = 0x38,
                    IsExtended = true
                },
                InputInjectionPhase.Down,
                0x4B50544C),
            CancellationToken.None);

        Require(native.Inputs.Count == 1, "Exactly one native packet must be emitted.");
        var input = native.Inputs[0];
        Require(input.Type == KeyboardSendInputEncoder.InputKeyboard, "INPUT type must be keyboard.");
        Require(input.Union.Keyboard.VirtualKey == 0, "Scan-code mode must clear wVk.");
        Require(input.Union.Keyboard.ScanCode == 0x38, "The captured scan code must be retained.");
        Require(
            input.Union.Keyboard.Flags
            == (KeyboardSendInputEncoder.KeyEventScanCode
                | KeyboardSendInputEncoder.KeyEventExtendedKey),
            "An E0 scan code must use SCANCODE and EXTENDEDKEY.");
        Require(
            input.Union.Keyboard.ExtraInformation == (nuint)0x4B50544C,
            "dwExtraInfo must contain the process-local recursion marker.");

        var expectedSize = Environment.Is64BitProcess ? 40 : 28;
        Require(
            Marshal.SizeOf<NativeInput>() == expectedSize,
            "INPUT must retain the Win32 union size on this architecture.");
    }

    public static async Task VirtualKeySendInputIsExactAsync()
    {
        var native = new RecordingSendInputNative();
        var backend = new WindowsSendInputBackend(native);

        await backend.InjectAsync(
            Request(
                new InputControlId
                {
                    Kind = InputControlKind.VirtualKey,
                    Code = 0x41
                },
                InputInjectionPhase.Up,
                0x10203040),
            CancellationToken.None);

        var keyboard = native.Inputs.Single().Union.Keyboard;
        Require(keyboard.VirtualKey == 0x41, "Virtual-key mode must retain wVk.");
        Require(keyboard.ScanCode == 0, "Virtual-key mode must clear wScan.");
        Require(
            keyboard.Flags == KeyboardSendInputEncoder.KeyEventKeyUp,
            "Virtual-key release must set only KEYUP.");
    }

    public static async Task E1PauseUsesVirtualKeyAsync()
    {
        var native = new RecordingSendInputNative();
        var backend = new WindowsSendInputBackend(native);
        var pause = new InputControlId
        {
            Kind = InputControlKind.KeyboardScanCode,
            Code = 0x45,
            IsExtended = true,
            RawQualifier = "RAWKEYBOARD-V1;PREFIX=0004"
        };

        await backend.InjectAsync(
            Request(pause, InputInjectionPhase.Down, 0x11223344),
            CancellationToken.None);

        var keyboard = native.Inputs.Single().Union.Keyboard;
        Require(keyboard.VirtualKey == 0x13, "E1 Pause must be translated to VK_PAUSE.");
        Require(keyboard.ScanCode == 0, "E1 Pause must not be sent as scan code 0x45.");
        Require(
            (keyboard.Flags & KeyboardSendInputEncoder.KeyEventExtendedKey) == 0,
            "E1 Pause must never carry the E0-only EXTENDEDKEY flag.");
        Require(
            keyboard.ExtraInformation == (nuint)0x11223344,
            "Pause translation must retain the recursion marker.");

        var ambiguousPause = pause with { RawQualifier = null };
        await RequireThrowsAsync<NotSupportedException>(
            () => backend.InjectAsync(
                    Request(ambiguousPause, InputInjectionPhase.Down, 0x11223344),
                    CancellationToken.None)
                .AsTask(),
            "Ambiguous extended 0x45 must be rejected instead of emitted as E0.");
        Require(native.Inputs.Count == 1, "Rejected ambiguous Pause must not reach SendInput.");
    }

    public static async Task UnsupportedSendInputNeverCallsNativeAsync()
    {
        var native = new RecordingSendInputNative();
        var backend = new WindowsSendInputBackend(native);

        await RequireThrowsAsync<NotSupportedException>(
            () => backend.InjectAsync(
                    Request(
                        new InputControlId
                        {
                            Kind = InputControlKind.HidUsage,
                            Code = 1,
                            UsagePage = 0xFF00,
                            Usage = 1
                        },
                        InputInjectionPhase.Down,
                        1),
                    CancellationToken.None)
                .AsTask(),
            "Vendor HID must not be approximated as a keyboard event.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RequireThrowsAsync<OperationCanceledException>(
            () => backend.InjectAsync(
                    Request(
                        new InputControlId
                        {
                            Kind = InputControlKind.VirtualKey,
                            Code = 0x41
                        },
                        InputInjectionPhase.Down,
                        1),
                    cancellation.Token)
                .AsTask(),
            "Pre-cancelled injection must stop before native dispatch.");

        Require(native.Inputs.Count == 0, "Rejected or cancelled requests must have no native effect.");

        native.Result = new SendInputNativeResult(0, 5);
        await RequireThrowsAsync<Win32Exception>(
            () => backend.InjectAsync(
                    Request(
                        new InputControlId
                        {
                            Kind = InputControlKind.VirtualKey,
                            Code = 0x41
                        },
                        InputInjectionPhase.Down,
                        1),
                    CancellationToken.None)
                .AsTask(),
            "A short SendInput result must be surfaced as a Win32 error.");
    }

    public static async Task ExecutorCleanupUsesMarkedSendInputAsync()
    {
        var native = new RecordingSendInputNative();
        var backend = new WindowsSendInputBackend(native);
        var executor = new WindowsActionExecutor(
            backend,
            new RejectingExternalBackend(),
            new FailingDelayScheduler(),
            new InputInjectionMarker(0x55667788));
        var target = new InputControlId
        {
            Kind = InputControlKind.KeyboardScanCode,
            Code = 0x1D
        };
        var plan = new ActionPlan(new ActionPlanOperation[]
        {
            new InjectInputOperation(new ControlInjectionTarget(target), InputInjectionPhase.Down),
            new DelayOperation(1)
        });

        await RequireThrowsAsync<SyntheticActionFailureException>(
            () => executor.ExecuteAsync(plan),
            "The synthetic scheduler failure must reach the caller.");

        Require(native.Inputs.Count == 2, "Failure cleanup must emit one matching release.");
        Require(
            (native.Inputs[0].Union.Keyboard.Flags & KeyboardSendInputEncoder.KeyEventKeyUp) == 0,
            "The first native event must be key down.");
        Require(
            (native.Inputs[1].Union.Keyboard.Flags & KeyboardSendInputEncoder.KeyEventKeyUp) != 0,
            "The cleanup native event must be key up.");
        Require(
            native.Inputs.All(
                input => input.Union.Keyboard.ExtraInformation == (nuint)0x55667788),
            "Both normal and cleanup events must retain the recursion marker.");
    }

    public static async Task ExecutableArgumentsStaySeparatedAsync(string root)
    {
        var executable = await CreateFileAsync(root, "argument-app.exe");
        var starter = new RecordingProcessStarter();
        var backend = new WindowsExternalActionBackend(starter);

        await backend.LaunchProgramAsync(
            new LaunchProgramOperation(
                executable,
                "--name \"hello world\" \"\" \"quoted\\\"value\"",
                root),
            CancellationToken.None);

        var startInfo = starter.Starts.Single();
        Require(!startInfo.UseShellExecute, "EXE launch must bypass shell association.");
        Require(startInfo.FileName == executable, "EXE path must stay separate from arguments.");
        Require(
            startInfo.ArgumentList.SequenceEqual(
                new[] { "--name", "hello world", string.Empty, "quoted\"value" }),
            "Quoted user text must become discrete ArgumentList entries.");
        Require(string.IsNullOrEmpty(startInfo.Arguments), "The concatenated Arguments field must remain empty.");
    }

    public static async Task EnvironmentVariablePathsAreExpandedAsync(string root)
    {
        var executable = await CreateFileAsync(root, "environment-app.exe");
        var variableName = $"KEYPILOT_TEST_ROOT_{Guid.NewGuid():N}";
        var previous = Environment.GetEnvironmentVariable(variableName);
        try
        {
            Environment.SetEnvironmentVariable(variableName, root);
            var starter = new RecordingProcessStarter();
            var backend = new WindowsExternalActionBackend(starter);
            await backend.LaunchProgramAsync(
                new LaunchProgramOperation(
                    $"%{variableName}%\\environment-app.exe",
                    Arguments: null,
                    WorkingDirectory: null),
                CancellationToken.None);

            Require(
                string.Equals(starter.Starts.Single().FileName, executable, StringComparison.OrdinalIgnoreCase),
                "Environment variables must expand before absolute-path validation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    public static async Task FileOpenUsesAssociationWithoutArgumentsAsync(string root)
    {
        var document = await CreateFileAsync(root, "notes.txt");
        var starter = new RecordingProcessStarter();
        var backend = new WindowsExternalActionBackend(starter);

        await backend.LaunchProgramAsync(
            new LaunchProgramOperation(document, null, null),
            CancellationToken.None);

        var startInfo = starter.Starts.Single();
        Require(startInfo.UseShellExecute, "A document must use its Windows file association.");
        Require(startInfo.Verb == "open", "File association must use the explicit open verb.");
        Require(startInfo.ArgumentList.Count == 0, "Associated file launch must accept no arguments.");

        await RequireThrowsAsync<NotSupportedException>(
            () => backend.LaunchProgramAsync(
                    new LaunchProgramOperation(document, "unexpected", null),
                    CancellationToken.None)
                .AsTask(),
            "Arguments must not be smuggled through a file association.");
        Require(starter.Starts.Count == 1, "Rejected file arguments must not reach the process starter.");
    }

    public static Task ShellExecuteNullResultIsSuccessfulAsync()
    {
        var associated = new ProcessStartInfo
        {
            FileName = "https://example.invalid/",
            UseShellExecute = true
        };
        SystemProcessStarter.CompleteStart(associated, process: null);

        var direct = new ProcessStartInfo
        {
            FileName = @"C:\missing-test-only.exe",
            UseShellExecute = false
        };
        return RequireThrowsAsync<InvalidOperationException>(
            () =>
            {
                SystemProcessStarter.CompleteStart(direct, process: null);
                return Task.CompletedTask;
            },
            "A direct start without a process handle must still report failure.");
    }

    public static async Task UriOpenAcceptsOnlyHttpAndHttpsAsync()
    {
        var starter = new RecordingProcessStarter();
        var schemes = new StubUriSchemeRegistration("steam");
        var backend = new WindowsExternalActionBackend(starter, schemes);

        await backend.OpenUriAsync(
            new OpenUriOperation("https://example.invalid/path?q=1"),
            CancellationToken.None);

        await backend.OpenUriAsync(
            new OpenUriOperation("example.invalid/path?q=2"),
            CancellationToken.None);

        await backend.OpenUriAsync(
            new OpenUriOperation("steam://open/games"),
            CancellationToken.None);

        Require(starter.Starts.Count == 3, "valid web/custom addresses were not opened exactly once");
        Require(starter.Starts.All(info => info.UseShellExecute), "URIs must use registered shell associations");
        Require(starter.Starts.All(info => info.ArgumentList.Count == 0), "URI open must not carry process arguments");
        Require(
            starter.Starts[1].FileName == "https://example.invalid/path?q=2",
            "naked domain was not normalized to HTTPS");
        Require(starter.Starts[2].FileName == "steam://open/games", "registered protocol changed unexpectedly");

        foreach (var rejected in new[]
                 {
                     "file:///C:/Windows/System32/calc.exe",
                     @"C:\Windows\System32\calc.exe",
                     @"\\server\share\tool.exe",
                     "shell:AppsFolder",
                     "unregistered://open/value",
                     "relative/path"
                 })
        {
            await RequireThrowsAsync<ArgumentException>(
                () => backend.OpenUriAsync(new OpenUriOperation(rejected), CancellationToken.None).AsTask(),
                $"unsafe or unregistered URI was accepted: {rejected}");
        }
        Require(starter.Starts.Count == 3, "a rejected URI reached the process starter");
    }

    public static async Task ScriptsUseExplicitWindowsRunnersAsync(string root)
    {
        var powerShell = await CreateFileAsync(root, "safe.ps1");
        var batch = await CreateFileAsync(root, "safe.cmd");
        var executable = await CreateFileAsync(root, "safe.exe");
        var starter = new RecordingProcessStarter();
        var backend = new WindowsExternalActionBackend(starter);

        await backend.RunScriptAsync(
            new RunScriptOperation(powerShell, "-Mode \"test value\""),
            CancellationToken.None);
        await backend.RunScriptAsync(
            new RunScriptOperation(batch, "alpha \"beta value\""),
            CancellationToken.None);
        await backend.RunScriptAsync(
            new RunScriptOperation(executable, "--quiet"),
            CancellationToken.None);

        Require(starter.Starts.Count == 3, "All three explicit script kinds must be planned.");
        var ps = starter.Starts[0];
        Require(
            ps.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase),
            "PowerShell scripts must use the system PowerShell executable.");
        Require(!ps.UseShellExecute && ps.CreateNoWindow, "PowerShell runner must be direct and non-interactive.");
        Require(ps.ArgumentList.Contains("-File"), "PowerShell must use -File, not a command string.");
        Require(!ps.ArgumentList.Contains("-ExecutionPolicy"), "The backend must not bypass execution policy.");
        Require(ps.ArgumentList.Contains(powerShell), "The PS1 path must be one ArgumentList item.");

        var cmd = starter.Starts[1];
        Require(
            cmd.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase),
            "Batch files must use the system CMD executable.");
        Require(
            cmd.ArgumentList.Take(4).SequenceEqual(new[] { "/d", "/s", "/v:off", "/c" }),
            "CMD safety switches must precede /c as separate entries.");
        Require(cmd.ArgumentList.Count == 5, "CMD /c must receive exactly one command operand.");
        Require(
            cmd.ArgumentList[4] == $"\"\"{batch}\" \"alpha\" \"beta value\"\"",
            "The batch path and decoded arguments must be quoted inside one bounded command.");

        var exe = starter.Starts[2];
        Require(exe.FileName == executable, "EXE script action must launch the selected binary directly.");
        Require(exe.ArgumentList.SequenceEqual(new[] { "--quiet" }), "EXE arguments must stay separated.");
    }

    public static async Task BatchCommandWithSpacesIsBoundedAsync(string root)
    {
        var scriptDirectory = Path.Combine(root, "script folder with spaces");
        Directory.CreateDirectory(scriptDirectory);
        var batch = await CreateFileAsync(scriptDirectory, "safe script.cmd");
        var starter = new RecordingProcessStarter();
        var backend = new WindowsExternalActionBackend(starter);

        await backend.RunScriptAsync(
            new RunScriptOperation(batch, "alpha \"beta value\""),
            CancellationToken.None);

        var startInfo = starter.Starts.Single();
        Require(startInfo.ArgumentList.Count == 5, "Only one command may follow CMD /c.");
        Require(
            startInfo.ArgumentList[4] == $"\"\"{batch}\" \"alpha\" \"beta value\"\"",
            "Spaces must be preserved by per-token quotes plus the outer CMD quote pair.");

        await RequireThrowsAsync<ArgumentException>(
            () => backend.RunScriptAsync(
                    new RunScriptOperation(batch, "\\\"literal-quote"),
                    CancellationToken.None)
                .AsTask(),
            "A decoded literal quote cannot be represented safely in CMD and must be rejected.");
        await RequireThrowsAsync<ArgumentException>(
            () => backend.RunScriptAsync(
                    new RunScriptOperation(batch, "\"\""),
                    CancellationToken.None)
                .AsTask(),
            "An empty batch argument has ambiguous CMD behavior and must be rejected.");
        Require(starter.Starts.Count == 1, "Rejected CMD arguments must not reach the process starter.");
    }

    public static async Task UnsafeBatchTextIsRejectedAsync(string root)
    {
        var batch = await CreateFileAsync(root, "safe-batch.cmd");
        var starter = new RecordingProcessStarter();
        var backend = new WindowsExternalActionBackend(starter);

        await RequireThrowsAsync<ArgumentException>(
            () => backend.RunScriptAsync(
                    new RunScriptOperation(batch, "alpha & unexpected.exe"),
                    CancellationToken.None)
                .AsTask(),
            "CMD metacharacters must be rejected because batch execution necessarily uses a shell.");

        Require(starter.Starts.Count == 0, "Unsafe batch text must not reach the process starter.");
    }

    public static async Task ExecutorPreflightsAllExternalActionsBeforeDispatchAsync(string root)
    {
        var executable = await CreateFileAsync(root, "preflight-app.exe");
        var batch = await CreateFileAsync(root, "preflight-script.cmd");
        var document = await CreateFileAsync(root, "preflight-document.txt");
        var missingFile = Path.GetFullPath(Path.Combine(root, "missing-preflight.exe"));
        var missingDirectory = Path.GetFullPath(Path.Combine(root, "missing-working-directory"));

        var invalidOperations = new ActionPlanOperation[]
        {
            new LaunchProgramOperation("relative-app.exe", null, null),
            new LaunchProgramOperation(missingFile, null, null),
            new LaunchProgramOperation(batch, null, null),
            new LaunchProgramOperation(document, "unexpected", null),
            new LaunchProgramOperation(executable, null, missingDirectory),
            new LaunchProgramOperation(executable, "\"unterminated", null),
            new OpenUriOperation("file:///C:/Windows/System32/notepad.exe"),
            new RunScriptOperation("relative-script.cmd", null),
            new RunScriptOperation(document, null),
            new RunScriptOperation(batch, "safe & unexpected.exe")
        };

        foreach (var invalidOperation in invalidOperations)
        {
            var native = new RecordingSendInputNative();
            var starter = new RecordingProcessStarter();
            var executor = new WindowsActionExecutor(
                new WindowsSendInputBackend(native),
                new WindowsExternalActionBackend(starter),
                new SystemActionDelayScheduler(),
                new InputInjectionMarker(0x4B50544C));
            var plan = new ActionPlan(new ActionPlanOperation[]
            {
                new InjectInputOperation(
                    new ControlInjectionTarget(
                        new InputControlId
                        {
                            Kind = InputControlKind.KeyboardScanCode,
                            Code = 0x1E
                        }),
                    InputInjectionPhase.Down),
                new OpenUriOperation("https://valid-before-invalid.example/"),
                invalidOperation
            });

            await RequireThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(plan),
                $"{invalidOperation.GetType().Name} must fail during whole-plan preflight.");

            Require(native.Inputs.Count == 0, "Preflight failure must occur before SendInput.");
            Require(starter.Starts.Count == 0, "Preflight failure must occur before any process or URI start.");
        }
    }

    public static async Task DelaySchedulerIsBoundedAndCancellableAsync()
    {
        var scheduler = new SystemActionDelayScheduler();
        await scheduler.DelayAsync(0, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RequireThrowsAsync<OperationCanceledException>(
            () => scheduler.DelayAsync(1_000, cancellation.Token).AsTask(),
            "A cancelled macro delay must stop immediately.");

        await RequireThrowsAsync<ArgumentOutOfRangeException>(
            () => scheduler.DelayAsync(
                    SystemActionDelayScheduler.MaximumDelayMilliseconds + 1,
                    CancellationToken.None)
                .AsTask(),
            "Delay values above the execution bound must be rejected.");
    }

    private static InputInjectionRequest Request(
        InputControlId control,
        InputInjectionPhase phase,
        uint marker) =>
        new(new ControlInjectionTarget(control), phase, new InputInjectionMarker(marker));

    private static async Task<string> CreateFileAsync(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        await File.WriteAllTextAsync(path, "KeyPilot test placeholder");
        return path;
    }

    private static async Task RequireThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class RecordingSendInputNative : ISendInputNative
{
    public List<NativeInput> Inputs { get; } = new();

    public SendInputNativeResult Result { get; set; } = new(1, 0);

    public SendInputNativeResult Send(NativeInput input)
    {
        Inputs.Add(input);
        return Result;
    }
}

internal sealed class RecordingProcessStarter : IProcessStarter
{
    public List<ProcessStartInfo> Starts { get; } = new();

    public void Start(ProcessStartInfo startInfo) => Starts.Add(startInfo);
}

internal sealed class StubUriSchemeRegistration(params string[] schemes) : IUriSchemeRegistration
{
    private readonly HashSet<string> _schemes = new(schemes, StringComparer.OrdinalIgnoreCase);

    public bool IsRegistered(string scheme) => _schemes.Contains(scheme);
}

internal sealed class RejectingExternalBackend : IExternalActionBackend
{
    public void ValidateLaunchProgram(LaunchProgramOperation operation)
    {
    }

    public void ValidateUri(OpenUriOperation operation)
    {
    }

    public void ValidateScript(RunScriptOperation operation)
    {
    }

    public ValueTask LaunchProgramAsync(
        LaunchProgramOperation operation,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("External execution was not expected.");

    public ValueTask OpenUriAsync(
        OpenUriOperation operation,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("External execution was not expected.");

    public ValueTask RunScriptAsync(
        RunScriptOperation operation,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("External execution was not expected.");
}

internal sealed class FailingDelayScheduler : IActionDelayScheduler
{
    public ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        throw new SyntheticActionFailureException();
}

internal sealed class SyntheticActionFailureException : Exception;
