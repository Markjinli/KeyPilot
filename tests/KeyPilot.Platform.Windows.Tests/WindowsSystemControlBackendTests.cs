using System.Runtime.InteropServices;
using KeyPilot.Core.Actions;
using KeyPilot.Platform.Windows.Actions;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class WindowsSystemControlBackendTests
{
    public static async Task MediaAndRelativeVolumeUseMarkedVirtualKeysAsync()
    {
        var input = new RecordingSystemInputBackend();
        var audio = new RecordingCoreAudioEndpointVolume();
        var backend = new WindowsSystemControlBackend(input, audio);
        var marker = new InputInjectionMarker(0x4B50544C);
        var expected = new (MediaControlOperation Operation, int VirtualKey)[]
        {
            (new MediaControlOperation(MediaControlCommand.PlayPause),
                WindowsSystemControlBackend.VirtualKeyMediaPlayPause),
            (new MediaControlOperation(MediaControlCommand.Stop),
                WindowsSystemControlBackend.VirtualKeyMediaStop),
            (new MediaControlOperation(MediaControlCommand.PreviousTrack),
                WindowsSystemControlBackend.VirtualKeyMediaPreviousTrack),
            (new MediaControlOperation(MediaControlCommand.NextTrack),
                WindowsSystemControlBackend.VirtualKeyMediaNextTrack)
        };

        foreach (var item in expected)
        {
            await backend.ExecuteMediaControlAsync(item.Operation, marker, CancellationToken.None);
            AssertPress(input.Requests.TakeLast(2), item.VirtualKey, marker);
        }

        var volumeCommands = new (VolumeControlOperation Operation, int VirtualKey)[]
        {
            (new VolumeControlOperation(VolumeControlCommand.Increase, null),
                WindowsSystemControlBackend.VirtualKeyVolumeUp),
            (new VolumeControlOperation(VolumeControlCommand.Decrease, null),
                WindowsSystemControlBackend.VirtualKeyVolumeDown),
            (new VolumeControlOperation(VolumeControlCommand.ToggleMute, null),
                WindowsSystemControlBackend.VirtualKeyVolumeMute)
        };
        foreach (var item in volumeCommands)
        {
            await backend.ExecuteVolumeControlAsync(item.Operation, marker, CancellationToken.None);
            AssertPress(input.Requests.TakeLast(2), item.VirtualKey, marker);
        }

        Assert(audio.Scalars.Count == 0,
            "Media and relative volume commands must not touch Core Audio scalar volume.");
    }

    public static async Task ExactVolumeUsesCoreAudioScalarAsync()
    {
        var input = new RecordingSystemInputBackend();
        var audio = new RecordingCoreAudioEndpointVolume();
        var backend = new WindowsSystemControlBackend(input, audio);

        foreach (var level in new[] { 0, 42, 100 })
        {
            await backend.ExecuteVolumeControlAsync(
                new VolumeControlOperation(VolumeControlCommand.SetLevelPercent, level),
                new InputInjectionMarker(1),
                CancellationToken.None);
        }

        Assert(audio.Scalars.Count == 3, "Every exact-volume command must reach Core Audio once.");
        AssertNear(0f, audio.Scalars[0]);
        AssertNear(0.42f, audio.Scalars[1]);
        AssertNear(1f, audio.Scalars[2]);
        Assert(input.Requests.Count == 0,
            "Exact volume must not approximate a requested percentage with repeated volume keys.");
    }

    public static async Task ExactVolumeFailureExplainsCoreAudioCapabilityAsync()
    {
        var audio = new RecordingCoreAudioEndpointVolume
        {
            Failure = new COMException("endpoint unavailable", unchecked((int)0x80070490))
        };
        var backend = new WindowsSystemControlBackend(
            new RecordingSystemInputBackend(),
            audio);

        try
        {
            await backend.ExecuteVolumeControlAsync(
                new VolumeControlOperation(VolumeControlCommand.SetLevelPercent, 63),
                new InputInjectionMarker(1),
                CancellationToken.None);
            throw new InvalidOperationException("A Core Audio failure must reach the caller.");
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is COMException)
        {
            Assert(exception.Message.Contains("63%", StringComparison.Ordinal),
                "The failure must identify the requested exact level.");
            Assert(exception.Message.Contains("Core Audio", StringComparison.OrdinalIgnoreCase),
                "The failure must identify the unavailable Windows capability.");
        }
    }

    public static async Task ExecutorPreflightsAndDispatchesSystemOperationsAsync()
    {
        var input = new RecordingSystemInputBackend();
        var system = new RecordingSystemControlBackend();
        var executor = new WindowsActionExecutor(
            input,
            new NoOpExternalActionBackend(),
            new NoOpActionDelayScheduler(),
            system,
            new InputInjectionMarker(0x11223344));
        var valid = new ActionPlan(new ActionPlanOperation[]
        {
            new MediaControlOperation(MediaControlCommand.NextTrack),
            new VolumeControlOperation(VolumeControlCommand.SetLevelPercent, 25)
        });

        await executor.ExecuteAsync(valid);
        Assert(system.Events.SequenceEqual(
            ["media:NextTrack:11223344", "volume:SetLevelPercent:25:11223344"]),
            "The executor must preserve semantic system operations and its recursion marker.");

        var invalid = new ActionPlan(new ActionPlanOperation[]
        {
            new InjectInputOperation(
                new ControlInjectionTarget(new KeyPilot.Core.Input.InputControlId
                {
                    Kind = KeyPilot.Core.Input.InputControlKind.VirtualKey,
                    Code = 0x41
                }),
                InputInjectionPhase.Down),
            new VolumeControlOperation(VolumeControlCommand.SetLevelPercent, null)
        });
        try
        {
            await executor.ExecuteAsync(invalid);
            throw new InvalidOperationException("Invalid system control must fail preflight.");
        }
        catch (InvalidOperationException exception)
            when (exception.Message.StartsWith("Invalid action plan", StringComparison.Ordinal))
        {
            // Expected before the leading input operation can run.
        }

        Assert(input.Requests.Count == 0,
            "Whole-plan system preflight must run before earlier input side effects.");
    }

    private static void AssertPress(
        IEnumerable<InputInjectionRequest> requests,
        int virtualKey,
        InputInjectionMarker marker)
    {
        var pair = requests.ToArray();
        Assert(pair.Length == 2, "A system virtual key must have one down and one up request.");
        Assert(pair[0].Phase == InputInjectionPhase.Down && pair[1].Phase == InputInjectionPhase.Up,
            "A system virtual key must be released after it is pressed.");
        Assert(pair.All(request => request.OriginMarker == marker),
            "System virtual keys must retain the executor recursion marker.");
        Assert(pair.All(request => request.Target is ControlInjectionTarget
            {
                Control.Kind: KeyPilot.Core.Input.InputControlKind.VirtualKey,
                Control.Code: var code
            } && code == virtualKey),
            $"Expected system virtual key 0x{virtualKey:X2}.");
    }

    private static void AssertNear(float expected, float actual)
    {
        if (Math.Abs(expected - actual) > 0.0001f)
        {
            throw new InvalidOperationException(
                $"Expected scalar {expected:F4}, actual {actual:F4}.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class RecordingSystemInputBackend : IInputInjectionBackend
{
    public List<InputInjectionRequest> Requests { get; } = new();

    public ValueTask InjectAsync(
        InputInjectionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingCoreAudioEndpointVolume : ICoreAudioEndpointVolume
{
    public List<float> Scalars { get; } = new();

    public Exception? Failure { get; init; }

    public void SetMasterVolumeScalar(float scalar)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Scalars.Add(scalar);
    }
}

internal sealed class RecordingSystemControlBackend : ISystemControlBackend
{
    public List<string> Events { get; } = new();

    public void ValidateMediaControl(MediaControlOperation operation)
    {
        if (!Enum.IsDefined(operation.Command))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    public void ValidateVolumeControl(VolumeControlOperation operation)
    {
        if (!Enum.IsDefined(operation.Command)
            || (operation.Command == VolumeControlCommand.SetLevelPercent
                && (!operation.LevelPercent.HasValue
                    || operation.LevelPercent is < 0 or > 100)))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    public ValueTask ExecuteMediaControlAsync(
        MediaControlOperation operation,
        InputInjectionMarker originMarker,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add($"media:{operation.Command}:{originMarker.Value:X8}");
        return ValueTask.CompletedTask;
    }

    public ValueTask ExecuteVolumeControlAsync(
        VolumeControlOperation operation,
        InputInjectionMarker originMarker,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add(
            $"volume:{operation.Command}:{operation.LevelPercent?.ToString() ?? "-"}:" +
            $"{originMarker.Value:X8}");
        return ValueTask.CompletedTask;
    }
}

internal sealed class NoOpExternalActionBackend : IExternalActionBackend
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
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask OpenUriAsync(
        OpenUriOperation operation,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask RunScriptAsync(
        RunScriptOperation operation,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class NoOpActionDelayScheduler : IActionDelayScheduler
{
    public ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
