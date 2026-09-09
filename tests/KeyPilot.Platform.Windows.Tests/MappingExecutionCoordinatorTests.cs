using System.Collections.Concurrent;
using KeyPilot.App.Presentation;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class MappingExecutionCoordinatorTests
{
    private static long _sequence;

    public static async Task InjectedInputIsRejectedBeforeRecognitionAsync()
    {
        var executions = 0;
        await using var coordinator = CreateCoordinator((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(Source("DEVICE-A", 0x1E), MappingTriggerKind.KeyDown, 0x41)));

        var accepted = coordinator.TrySubmit(Event(
            Source("DEVICE-A", 0x1E),
            InputEventPhase.Pressed,
            isInjected: true));
        await Task.Delay(50);

        Require(!accepted, "Injected input must be rejected at the coordinator boundary.");
        Require(executions == 0, "Injected input must not reach planning or execution.");
    }

    public static Task RawDownPacketsBecomePressedThenRepeatedAsync()
    {
        var tracker = new InputEventPhaseTracker();
        var source = Source("DEVICE-A", 0x1E);

        Require(
            tracker.Observe(source, isPressed: true) == InputEventPhase.Pressed,
            "The first Raw Input down packet must be Pressed.");
        Require(
            tracker.Observe(source, isPressed: true) == InputEventPhase.Repeated,
            "A continuous Raw Input down packet must be Repeated.");
        Require(
            tracker.Observe(source, isPressed: false) == InputEventPhase.Released,
            "Raw Input key-up must be Released.");
        Require(
            tracker.Observe(source, isPressed: true) == InputEventPhase.Pressed,
            "A down after release must begin a new press.");
        return Task.CompletedTask;
    }

    public static async Task ActiveProfileAndExactDeviceAreHonoredAsync()
    {
        var plans = new ConcurrentQueue<ActionPlan>();
        await using var coordinator = CreateCoordinator((plan, _) =>
        {
            plans.Enqueue(plan);
            return Task.CompletedTask;
        });
        var selected = Mapping(Source("DEVICE-A", 0x1E), MappingTriggerKind.SinglePress, 0x41);
        var inactive = Mapping(Source("DEVICE-A", 0x1E), MappingTriggerKind.SinglePress, 0x42);
        coordinator.TryApplyConfiguration(ConfigurationWithInactiveProfile(selected, inactive));

        SubmitPress(coordinator, Source("DEVICE-B", 0x1E));
        SubmitPress(coordinator, Source("DEVICE-A", 0x1E));
        await WaitUntilAsync(() => plans.Count == 1, "The exact-device mapping did not execute.");

        var target = plans.Single().Operations
            .OfType<InjectInputOperation>()
            .Select(operation => operation.Target)
            .OfType<ControlInjectionTarget>()
            .First().Control;
        Require(target.Code == 0x41, "Only the active profile's action may execute.");
    }

    public static async Task DuplicateDownAndRepeatFireOnlyOnceAsync()
    {
        var executions = 0;
        await using var coordinator = CreateCoordinator((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });
        var source = Source("DEVICE-A", 0x1E);
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(source, MappingTriggerKind.KeyDown, 0x41)));

        coordinator.TrySubmit(Event(source, InputEventPhase.Pressed));
        coordinator.TrySubmit(Event(source, InputEventPhase.Pressed));
        coordinator.TrySubmit(Event(source, InputEventPhase.Repeated));
        coordinator.TrySubmit(Event(source, InputEventPhase.Released));
        coordinator.TrySubmit(Event(source, InputEventPhase.Pressed));
        await WaitUntilAsync(() => Volatile.Read(ref executions) == 2, "Distinct presses did not execute.");
        await Task.Delay(30);

        Require(executions == 2, "Duplicate/repeated down packets must not retrigger a held key.");
    }

    public static async Task PassThroughInputRejectsMappingsThatRequireSuppressionAsync()
    {
        var plans = new ConcurrentQueue<ActionPlan>();
        await using var coordinator = CreateCoordinator((plan, _) =>
        {
            plans.Enqueue(plan);
            return Task.CompletedTask;
        });
        var source = Source("DEVICE-A", 0x1E);
        var requiresSuppression = Mapping(source, MappingTriggerKind.KeyDown, 0x41) with
        {
            SuppressOriginal = true
        };
        var retainsOriginal = Mapping(source, MappingTriggerKind.KeyDown, 0x42) with
        {
            SuppressOriginal = false
        };
        coordinator.TryApplyConfiguration(Configuration(requiresSuppression, retainsOriginal));

        Require(coordinator.TrySubmitPassThrough(Event(source, InputEventPhase.Pressed)),
            "The pass-through event must enter its restricted recognizer.");
        await WaitUntilAsync(() => plans.Count == 1, "The explicit retain-original mapping did not execute.");
        await Task.Delay(30);

        var target = plans.Single().Operations
            .OfType<InjectInputOperation>()
            .Select(operation => operation.Target)
            .OfType<ControlInjectionTarget>()
            .First().Control;
        Require(target.Code == 0x42,
            "An unsuppressed OS event must never execute a mapping that requires original-input suppression.");
        Require(MappingDispatchPolicy.HasEnabledSuppressedMapping(
                Configuration(requiresSuppression, retainsOriginal),
                source),
            "The UI policy must identify a rejected suppression-required match for diagnostics.");
    }

    public static async Task CrossBackendChordExecutesOnceAndResetClearsStateAsync()
    {
        var executions = 0;
        await using var coordinator = CreateCoordinator((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });
        var keyboard = Source("OEM-KEYBOARD", 0x1D);
        var gamepad = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Gamepad,
                MatchMode = DeviceMatchMode.ExactDevice,
                DeviceId = "XINPUT:USER:0"
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.GamepadButton,
                Code = 0x1000
            }
        };
        var chord = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Composite,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.InputChord,
                Code = 1
            },
            ChordMembers = [keyboard, gamepad]
        };
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(chord, MappingTriggerKind.SinglePress, 0x41) with
            {
                SuppressOriginal = false
            }));

        var start = DateTimeOffset.UtcNow;
        coordinator.TrySubmitPassThrough(Event(keyboard, InputEventPhase.Pressed, timestampUtc: start));
        coordinator.TrySubmitPassThrough(Event(gamepad, InputEventPhase.Pressed, timestampUtc: start.AddMilliseconds(10)));
        coordinator.TrySubmitPassThrough(Event(gamepad, InputEventPhase.Released, timestampUtc: start.AddMilliseconds(20)));
        coordinator.TrySubmitPassThrough(Event(keyboard, InputEventPhase.Released, timestampUtc: start.AddMilliseconds(30)));
        await WaitUntilAsync(() => Volatile.Read(ref executions) == 1,
            "跨键盘/手柄组合没有只执行一次。");

        coordinator.TrySubmitPassThrough(Event(keyboard, InputEventPhase.Pressed, timestampUtc: start.AddSeconds(1)));
        Require(coordinator.TryResetInputState(), "设备变化重置命令没有进入运行时。");
        coordinator.TrySubmitPassThrough(Event(gamepad, InputEventPhase.Pressed, timestampUtc: start.AddSeconds(1).AddMilliseconds(10)));
        coordinator.TrySubmitPassThrough(Event(gamepad, InputEventPhase.Released, timestampUtc: start.AddSeconds(1).AddMilliseconds(20)));
        coordinator.TrySubmitPassThrough(Event(keyboard, InputEventPhase.Released, timestampUtc: start.AddSeconds(1).AddMilliseconds(30)));
        await Task.Delay(80);
        Require(Volatile.Read(ref executions) == 1, "重置后的半套组合不应触发动作。");
    }

    public static async Task GamepadAndHidPassThroughAlsoRejectSuppressionAsync()
    {
        var executions = 0;
        await using var coordinator = CreateCoordinator((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });
        var gamepad = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Gamepad,
                MatchMode = DeviceMatchMode.ExactDevice,
                DeviceId = "XINPUT:USER:0"
            },
            Control = new InputControlId { Kind = InputControlKind.GamepadButton, Code = 0x1000 }
        };
        var hid = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Hid,
                MatchMode = DeviceMatchMode.ExactDevice,
                DeviceId = "HID-DEVICE-A"
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.RawCode,
                Code = 7,
                RawQualifier = "RAWHID-V1;REPORT=01;OFFSET=0007;MASK=01;ACTIVE=01"
            }
        };
        var configuration = Configuration(
            Mapping(gamepad, MappingTriggerKind.KeyDown, 0x41) with { SuppressOriginal = true },
            Mapping(hid, MappingTriggerKind.KeyDown, 0x42) with { SuppressOriginal = true });
        coordinator.TryApplyConfiguration(configuration);

        coordinator.TrySubmitPassThrough(Event(gamepad, InputEventPhase.Pressed));
        coordinator.TrySubmitPassThrough(Event(hid, InputEventPhase.Pressed));
        await Task.Delay(50);

        Require(executions == 0,
            "Gamepad and vendor-HID events must not bypass the suppression-required gate.");
        Require(MappingDispatchPolicy.HasEnabledSuppressedMapping(configuration, gamepad) &&
                MappingDispatchPolicy.HasEnabledSuppressedMapping(configuration, hid),
            "Both unsupported suppression backends must be diagnosable by the UI policy.");
    }

    public static async Task LongPressDeadlineFiresWithoutAnotherInputAsync()
    {
        var executions = 0;
        await using var coordinator = CreateCoordinator((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });
        var source = Source("DEVICE-A", 0x1E);
        var mapping = Mapping(source, MappingTriggerKind.LongPress, 0x41) with
        {
            Trigger = new MappingTrigger
            {
                Kind = MappingTriggerKind.LongPress,
                LongPressMilliseconds = 30
            }
        };
        coordinator.TryApplyConfiguration(Configuration(mapping));

        coordinator.TrySubmit(Event(source, InputEventPhase.Pressed));
        await WaitUntilAsync(
            () => Volatile.Read(ref executions) == 1,
            "Long press did not fire from its wall-clock deadline.");
        Require(executions == 1, "The deadline must activate the long press exactly once.");
    }

    public static async Task DoublePressWindowExpiresOnItsDeadlineAsync()
    {
        var executions = 0;
        await using var coordinator = CreateCoordinator((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });
        var source = Source("DEVICE-A", 0x1E);
        var mapping = Mapping(source, MappingTriggerKind.DoublePress, 0x41) with
        {
            Trigger = new MappingTrigger
            {
                Kind = MappingTriggerKind.DoublePress,
                DoublePressWindowMilliseconds = 30
            }
        };
        coordinator.TryApplyConfiguration(Configuration(mapping));
        var firstTimestamp = DateTimeOffset.UtcNow;
        coordinator.TrySubmit(Event(source, InputEventPhase.Pressed, timestampUtc: firstTimestamp));
        coordinator.TrySubmit(Event(source, InputEventPhase.Released, timestampUtc: firstTimestamp));

        await Task.Delay(80);
        var freshTimestamp = DateTimeOffset.UtcNow;
        coordinator.TrySubmit(Event(source, InputEventPhase.Pressed, timestampUtc: freshTimestamp));
        coordinator.TrySubmit(Event(source, InputEventPhase.Released, timestampUtc: freshTimestamp));
        await Task.Delay(5);
        Require(executions == 0, "An expired first click must not pair with a later fresh click.");

        coordinator.TrySubmit(Event(
            source,
            InputEventPhase.Pressed,
            timestampUtc: freshTimestamp.AddMilliseconds(1)));
        coordinator.TrySubmit(Event(
            source,
            InputEventPhase.Released,
            timestampUtc: freshTimestamp.AddMilliseconds(1)));
        await WaitUntilAsync(() => executions == 1, "A fresh double press did not activate.");
    }

    public static async Task ConfigurationReplacementResetsPendingTriggerStateAsync()
    {
        var targets = new ConcurrentQueue<int>();
        await using var coordinator = CreateCoordinator((plan, _) =>
        {
            targets.Enqueue(FirstTargetCode(plan));
            return Task.CompletedTask;
        });
        var oldSource = Source("DEVICE-A", 0x1E);
        var oldMapping = Mapping(oldSource, MappingTriggerKind.LongPress, 0x41) with
        {
            Trigger = new MappingTrigger
            {
                Kind = MappingTriggerKind.LongPress,
                LongPressMilliseconds = 80
            }
        };
        coordinator.TryApplyConfiguration(Configuration(oldMapping));
        coordinator.TrySubmit(Event(oldSource, InputEventPhase.Pressed));

        var newSource = Source("DEVICE-A", 0x30);
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(newSource, MappingTriggerKind.KeyDown, 0x42)));
        await Task.Delay(120);
        Require(targets.IsEmpty, "A pending trigger must not survive a configuration replacement.");

        coordinator.TrySubmit(Event(newSource, InputEventPhase.Pressed));
        await WaitUntilAsync(() => targets.Count == 1, "The replacement profile was not applied.");
        Require(targets.Single() == 0x42, "Only the replacement profile may activate.");
    }

    public static async Task HeldKeyDoesNotRetriggerAcrossProfileReplacementAsync()
    {
        var targets = new ConcurrentQueue<int>();
        await using var coordinator = CreateCoordinator((plan, _) =>
        {
            targets.Enqueue(FirstTargetCode(plan));
            return Task.CompletedTask;
        });
        var tracker = new InputEventPhaseTracker();
        var source = Source("DEVICE-A", 0x1E);
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(source, MappingTriggerKind.KeyDown, 0x41)));
        coordinator.TrySubmit(Event(source, tracker.Observe(source, isPressed: true)));
        await WaitUntilAsync(() => targets.Count == 1, "The original profile did not activate.");

        coordinator.TryApplyConfiguration(Configuration(
            Mapping(source, MappingTriggerKind.KeyDown, 0x42)));
        coordinator.TrySubmit(Event(source, tracker.Observe(source, isPressed: true)));
        coordinator.TrySubmit(Event(source, tracker.Observe(source, isPressed: false)));
        await Task.Delay(40);
        Require(
            targets.SequenceEqual(new[] { 0x41 }),
            "A held key repeat or release must not become a fresh press after a profile change.");

        coordinator.TrySubmit(Event(source, tracker.Observe(source, isPressed: true)));
        await WaitUntilAsync(() => targets.Count == 2, "A later physical press did not use the new profile.");
        Require(
            targets.SequenceEqual(new[] { 0x41, 0x42 }),
            "Only a complete release followed by a new press may activate the replacement profile.");
    }

    public static async Task RuntimeRevisionRejectsLateSuppressedInputAsync()
    {
        var targets = new ConcurrentQueue<int>();
        await using var coordinator = CreateCoordinator((plan, _) =>
        {
            targets.Enqueue(FirstTargetCode(plan));
            return Task.CompletedTask;
        });
        var source = Source("DEVICE-A", 0x1E);
        coordinator.TryApplyConfiguration(
            Configuration(Mapping(source, MappingTriggerKind.KeyDown, 0x41)),
            runtimeRevision: 10);
        coordinator.TryApplyConfiguration(
            Configuration(Mapping(source, MappingTriggerKind.KeyDown, 0x42)),
            cancelPendingActions: true,
            runtimeRevision: 11);

        var stale = coordinator.SubmitTracked(
            Event(source, InputEventPhase.Pressed),
            expectedRuntimeRevision: 10);
        Require(
            !(await stale.Committed.WaitAsync(TimeSpan.FromSeconds(2))).Success,
            "A late suppressed edge from the previous foreground context must be rejected.");
        Require(
            !(await stale.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Success,
            "A rejected stale edge must never be acknowledged as a successful action.");
        Require(targets.IsEmpty, "Stale input must not execute the replacement profile's action.");

        var current = coordinator.SubmitTracked(
            Event(source, InputEventPhase.Pressed),
            expectedRuntimeRevision: 11);
        Require(
            (await current.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Success,
            "Current-context suppressed input should remain executable.");
        Require(targets.SequenceEqual(new[] { 0x42 }),
            "Only the current profile action may execute after the revision fence.");
    }

    public static async Task ForegroundContextReplacementCancelsOldActionsAsync()
    {
        var firstEntered = NewSignal();
        var firstCancelled = NewSignal();
        var invokedTargets = new ConcurrentQueue<int>();
        var invocationCount = 0;
        await using var coordinator = CreateCoordinator(async (plan, cancellationToken) =>
        {
            invokedTargets.Enqueue(FirstTargetCode(plan));
            if (Interlocked.Increment(ref invocationCount) != 1)
            {
                return;
            }

            firstEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                firstCancelled.TrySetResult();
                throw;
            }
        });
        var firstSource = Source("DEVICE-A", 0x1E);
        var queuedSource = Source("DEVICE-A", 0x30);
        var configuration = Configuration(
            Mapping(firstSource, MappingTriggerKind.KeyDown, 0x41),
            Mapping(queuedSource, MappingTriggerKind.KeyDown, 0x42));
        coordinator.TryApplyConfiguration(configuration);

        var tracked = coordinator.SubmitTracked(Event(firstSource, InputEventPhase.Pressed));
        Require((await tracked.Committed).Success, "The running old-context action was not committed.");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.TrySubmit(Event(queuedSource, InputEventPhase.Pressed));
        coordinator.TryApplyConfiguration(configuration, cancelPendingActions: true);

        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancelledCompletion = await tracked.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!cancelledCompletion.Success,
            "A suppressed old-context input must not be acknowledged as successfully completed after cancellation.");

        coordinator.TrySubmit(Event(firstSource, InputEventPhase.Pressed));
        await WaitUntilAsync(() => invokedTargets.Count == 2,
            "A new-context action did not execute after old work was cancelled.");
        Require(
            invokedTargets.SequenceEqual(new[] { 0x41, 0x41 }),
            "The running action should cancel, queued old-context work should be discarded, and only new work may execute.");
    }

    public static async Task OutOfOrderCaptureTimestampsAreMonotonicizedAsync()
    {
        var targets = new ConcurrentQueue<int>();
        var recognitionFaults = 0;
        await using var coordinator = CreateCoordinator((plan, _) =>
        {
            targets.Enqueue(FirstTargetCode(plan));
            return Task.CompletedTask;
        });
        coordinator.Faulted += (_, args) =>
        {
            if (args.Stage == MappingExecutionStage.InputRecognition)
            {
                Interlocked.Increment(ref recognitionFaults);
            }
        };
        var first = Source("DEVICE-A", 0x1E);
        var second = Source("DEVICE-A", 0x30);
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(first, MappingTriggerKind.KeyDown, 0x41),
            Mapping(second, MappingTriggerKind.KeyDown, 0x42)));
        var later = DateTimeOffset.UtcNow.AddSeconds(1);

        coordinator.TrySubmit(Event(first, InputEventPhase.Pressed, timestampUtc: later));
        coordinator.TrySubmit(Event(second, InputEventPhase.Pressed, timestampUtc: later.AddSeconds(-1)));
        await WaitUntilAsync(() => targets.Count == 2, "Out-of-order input stopped recognition.");

        Require(recognitionFaults == 0, "Capture-thread reorder must not move state-machine time backwards.");
        Require(targets.SequenceEqual(new[] { 0x41, 0x42 }), "Monotonicization must retain arrival order.");
    }

    public static async Task ActionsAreFifoWithoutBlockingInputSubmissionAsync()
    {
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var startedTargets = new ConcurrentQueue<int>();
        await using var coordinator = CreateCoordinator(async (plan, cancellationToken) =>
        {
            var code = FirstTargetCode(plan);
            startedTargets.Enqueue(code);
            if (code == 0x41)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
        });
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(Source("DEVICE-A", 0x1E), MappingTriggerKind.KeyDown, 0x41),
            Mapping(Source("DEVICE-A", 0x30), MappingTriggerKind.KeyDown, 0x42)));

        Require(
            coordinator.TrySubmit(Event(Source("DEVICE-A", 0x1E), InputEventPhase.Pressed)),
            "First input should be queued.");
        Require(
            coordinator.TrySubmit(Event(Source("DEVICE-A", 0x30), InputEventPhase.Pressed)),
            "Second input submission must not wait for the first action.");
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Require(startedTargets.SequenceEqual(new[] { 0x41 }), "A later action must not overtake the first.");

        releaseFirst.TrySetResult();
        await WaitUntilAsync(() => startedTargets.Count == 2, "The queued second action did not execute.");
        Require(startedTargets.SequenceEqual(new[] { 0x41, 0x42 }), "Actions must execute in FIFO order.");
    }

    public static async Task SaturatedActionQueueDropsWithoutBlockingInputAsync()
    {
        var firstEntered = NewSignal();
        var saturation = new TaskCompletionSource<MappingExecutionFaultEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = CreateCoordinator(async (_, cancellationToken) =>
        {
            firstEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        coordinator.Faulted += (_, args) =>
        {
            if (args.Exception.Message.Contains("safety limit", StringComparison.Ordinal))
            {
                saturation.TrySetResult(args);
            }
        };
        var mappingCount = MappingExecutionCoordinator.ActionQueueCapacity + 3;
        var mappings = Enumerable.Range(1, mappingCount)
            .Select(index => Mapping(
                Source("DEVICE-A", index),
                MappingTriggerKind.KeyDown,
                0x41))
            .ToArray();
        coordinator.TryApplyConfiguration(Configuration(mappings));

        for (var index = 1; index <= mappingCount; index++)
        {
            Require(
                coordinator.TrySubmit(Event(
                    Source("DEVICE-A", index),
                    InputEventPhase.Pressed)),
                "Input edges must remain non-blocking while actions are saturated.");
        }

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reported = await saturation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(
            reported.Stage == MappingExecutionStage.ActionExecution,
            "Action queue saturation must be reported as an execution fault.");
        coordinator.Stop();
    }

    public static async Task ExecutionFailureIsReportedAndWorkerContinuesAsync()
    {
        var fault = new TaskCompletionSource<MappingExecutionFaultEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var successfulTargets = new ConcurrentQueue<int>();
        await using var coordinator = CreateCoordinator((plan, _) =>
        {
            var code = FirstTargetCode(plan);
            if (code == 0x41)
            {
                throw new SyntheticMappingExecutionException();
            }

            successfulTargets.Enqueue(code);
            return Task.CompletedTask;
        });
        coordinator.Faulted += (_, args) => fault.TrySetResult(args);
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(Source("DEVICE-A", 0x1E), MappingTriggerKind.KeyDown, 0x41),
            Mapping(Source("DEVICE-A", 0x30), MappingTriggerKind.KeyDown, 0x42)));

        coordinator.TrySubmit(Event(Source("DEVICE-A", 0x1E), InputEventPhase.Pressed));
        coordinator.TrySubmit(Event(Source("DEVICE-A", 0x30), InputEventPhase.Pressed));

        var reported = await fault.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => successfulTargets.Count == 1, "The worker stopped after one action failed.");
        Require(reported.Stage == MappingExecutionStage.ActionExecution, "The failure stage must be visible.");
        Require(reported.Exception is SyntheticMappingExecutionException, "The original failure must be retained.");
        Require(successfulTargets.Single() == 0x42, "A later valid action must still execute.");
    }

    public static async Task StopCancelsRunningActionAndRejectsNewInputAsync()
    {
        var entered = NewSignal();
        var cancelled = NewSignal();
        await using var coordinator = CreateCoordinator(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        });
        var source = Source("DEVICE-A", 0x1E);
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(source, MappingTriggerKind.KeyDown, 0x41)));
        coordinator.TrySubmit(Event(source, InputEventPhase.Pressed));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Stop();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(
            !coordinator.TrySubmit(Event(source, InputEventPhase.Released)),
            "Stopped coordinator must reject new input.");
    }

    public static async Task TrackedInputWaitsForSuccessfulActionAsync()
    {
        var entered = NewSignal();
        var release = NewSignal();
        await using var coordinator = CreateCoordinator(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        var source = Source("DEVICE-A", 0x1E);
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(source, MappingTriggerKind.KeyDown, 0x41)));

        var completion = coordinator.SubmitTrackedAsync(Event(
            source,
            InputEventPhase.Pressed,
            sequenceNumber: 10_001));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!completion.IsCompleted, "Tracked input must not complete when its action only entered the queue.");
        release.TrySetResult();
        var result = await completion.WaitAsync(TimeSpan.FromSeconds(2));
        Require(result.Success && result.ProgressCounter > 0, "Successful action completion must produce a success receipt.");
    }

    public static async Task TrackedLongPressIncludesDeadlineAndActionAsync()
    {
        var entered = NewSignal();
        var release = NewSignal();
        await using var coordinator = CreateCoordinator(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        var source = Source("DEVICE-A", 0x1E);
        var mapping = Mapping(source, MappingTriggerKind.LongPress, 0x41) with
        {
            Trigger = new MappingTrigger
            {
                Kind = MappingTriggerKind.LongPress,
                LongPressMilliseconds = 30
            }
        };
        coordinator.TryApplyConfiguration(Configuration(mapping));

        var completion = coordinator.SubmitTrackedAsync(Event(
            source,
            InputEventPhase.Pressed,
            sequenceNumber: 10_002));
        await Task.Delay(10);
        Require(!completion.IsCompleted, "Long-press Down must not complete before its deadline.");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!completion.IsCompleted, "Long-press input must still wait for its mapped action.");
        release.TrySetResult();
        Require(
            (await completion.WaitAsync(TimeSpan.FromSeconds(2))).Success,
            "Long-press completion must follow the successful action.");
    }

    public static async Task TrackedInputIgnoresAnotherKeysDeadlineAsync()
    {
        var longPressSource = Source("DEVICE-A", 0x30);
        var immediateSource = Source("DEVICE-A", 0x1E);
        var longPress = Mapping(longPressSource, MappingTriggerKind.LongPress, 0x42) with
        {
            Trigger = new MappingTrigger
            {
                Kind = MappingTriggerKind.LongPress,
                LongPressMilliseconds = 10_000
            }
        };
        await using var coordinator = CreateCoordinator((_, _) => Task.CompletedTask);
        coordinator.TryApplyConfiguration(Configuration(
            longPress,
            Mapping(immediateSource, MappingTriggerKind.KeyDown, 0x41)));

        var pendingLongPress = coordinator.SubmitTrackedAsync(Event(
            longPressSource,
            InputEventPhase.Pressed,
            sequenceNumber: 10_020));
        var immediate = coordinator.SubmitTrackedAsync(Event(
            immediateSource,
            InputEventPhase.Pressed,
            sequenceNumber: 10_021));

        var result = await immediate.WaitAsync(TimeSpan.FromSeconds(2));
        Require(result.Success, "An immediate mapping must complete independently of another key's deadline.");
        Require(!pendingLongPress.IsCompleted, "The long-press origin must continue waiting for its own deadline.");
    }

    public static async Task TrackedLongWaitProvidesVerifiableLivenessAsync()
    {
        var source = Source("DEVICE-A", 0x1E);
        var mapping = Mapping(source, MappingTriggerKind.LongPress, 0x41) with
        {
            Trigger = new MappingTrigger
            {
                Kind = MappingTriggerKind.LongPress,
                LongPressMilliseconds = 1_500
            }
        };
        await using var coordinator = CreateCoordinator((_, _) => Task.CompletedTask);
        coordinator.TryApplyConfiguration(Configuration(mapping));

        var tracking = coordinator.SubmitTracked(Event(
            source,
            InputEventPhase.Pressed,
            sequenceNumber: 10_022));
        var commit = await tracking.Committed.WaitAsync(TimeSpan.FromSeconds(2));
        Require(commit.Success && commit.VerifyLivenessAsync is not null,
            "A committed bounded wait must expose a runtime liveness proof.");
        var livenessProbe = commit.VerifyLivenessAsync ??
            throw new InvalidOperationException("The liveness probe was unexpectedly missing.");

        long prior = commit.ProgressCounter;
        for (var index = 0; index < 3; index++)
        {
            await Task.Delay(375);
            var current = await livenessProbe(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Require(current > prior, "Each health challenge must observe fresh state-machine progress.");
            prior = current;
        }

        Require(!tracking.Completion.IsCompleted, "The liveness proof must not advance the completion watermark.");
        Require((await tracking.Completion.WaitAsync(TimeSpan.FromSeconds(2))).Success,
            "The long-press action must still complete normally after its deadline.");
    }

    public static async Task TrackedNoActionCompletesWithoutWaitingForFutureReleaseAsync()
    {
        var executions = 0;
        await using var coordinator = CreateCoordinator((_, _) =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });
        var source = Source("DEVICE-A", 0x1E);
        coordinator.TryApplyConfiguration(Configuration(
            Mapping(source, MappingTriggerKind.SinglePress, 0x41)));

        var completion = await coordinator.SubmitTrackedAsync(Event(
                source,
                InputEventPhase.Pressed,
                sequenceNumber: 10_003))
            .WaitAsync(TimeSpan.FromSeconds(2));
        Require(completion.Success, "A Down with no activated action should complete safely.");
        Require(executions == 0, "SinglePress must still wait for the later release event.");
    }

    public static async Task TrackedPlanningAndExecutionFailuresAreNotSuccessfulAsync()
    {
        var source = Source("DEVICE-A", 0x1E);
        var invalidMapping = Mapping(source, MappingTriggerKind.KeyDown, 0x41) with
        {
            Action = new ShortcutAction()
        };
        await using (var planning = CreateCoordinator((_, _) =>
                     throw new InvalidOperationException("Execution must not start.")))
        {
            planning.TryApplyConfiguration(Configuration(invalidMapping));
            var result = await planning.SubmitTrackedAsync(Event(
                    source,
                    InputEventPhase.Pressed,
                    sequenceNumber: 10_004))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Require(!result.Success && result.Exception is ActionPlanningException,
                "Planning failure must produce a failed tracked receipt.");
        }

        await using (var execution = CreateCoordinator((_, _) =>
                     throw new SyntheticMappingExecutionException()))
        {
            execution.TryApplyConfiguration(Configuration(
                Mapping(source, MappingTriggerKind.KeyDown, 0x41)));
            var result = await execution.SubmitTrackedAsync(Event(
                    source,
                    InputEventPhase.Pressed,
                    sequenceNumber: 10_005))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Require(!result.Success && result.Exception is SyntheticMappingExecutionException,
                "Execution failure must produce a failed tracked receipt.");
        }
    }

    private static MappingExecutionCoordinator CreateCoordinator(
        Func<ActionPlan, CancellationToken, Task> executeAsync) =>
        new(new MappingActionPlanner(), executeAsync);

    private static KeyPilotConfiguration Configuration(params InputMapping[] mappings)
    {
        var profile = new MappingProfile { Name = "active", Mappings = mappings.ToList() };
        return new KeyPilotConfiguration
        {
            IsMappingEnabled = true,
            ActiveProfileId = profile.Id,
            Profiles = { profile }
        };
    }

    private static KeyPilotConfiguration ConfigurationWithInactiveProfile(
        InputMapping selected,
        InputMapping inactive)
    {
        var activeProfile = new MappingProfile { Name = "active", Mappings = { selected } };
        var inactiveProfile = new MappingProfile { Name = "inactive", Mappings = { inactive } };
        return new KeyPilotConfiguration
        {
            IsMappingEnabled = true,
            ActiveProfileId = activeProfile.Id,
            Profiles = { inactiveProfile, activeProfile }
        };
    }

    private static InputMapping Mapping(
        InputSource source,
        MappingTriggerKind triggerKind,
        int outputVirtualKey) =>
        new()
        {
            Name = $"map-{outputVirtualKey:X2}",
            Source = source,
            Trigger = new MappingTrigger { Kind = triggerKind },
            Action = new SendKeyAction
            {
                Target = new InputControlId
                {
                    Kind = InputControlKind.VirtualKey,
                    Code = outputVirtualKey
                }
            }
        };

    private static InputSource Source(string deviceId, int scanCode) =>
        new()
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Keyboard,
                MatchMode = DeviceMatchMode.ExactDevice,
                DeviceId = deviceId
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.KeyboardScanCode,
                Code = scanCode,
                RawQualifier = "RAWKEYBOARD-V1;PREFIX=0000"
            }
        };

    private static InputEvent Event(
        InputSource source,
        InputEventPhase phase,
        bool isInjected = false,
        DateTimeOffset? timestampUtc = null,
        long? sequenceNumber = null) =>
        new()
        {
            Source = source,
            Phase = phase,
            TimestampUtc = timestampUtc ?? DateTimeOffset.UtcNow,
            SequenceNumber = sequenceNumber ?? Interlocked.Increment(ref _sequence),
            IsInjected = isInjected
        };

    private static void SubmitPress(MappingExecutionCoordinator coordinator, InputSource source)
    {
        coordinator.TrySubmit(Event(source, InputEventPhase.Pressed));
        coordinator.TrySubmit(Event(source, InputEventPhase.Released));
    }

    private static int FirstTargetCode(ActionPlan plan) =>
        plan.Operations
            .OfType<InjectInputOperation>()
            .Select(operation => operation.Target)
            .OfType<ControlInjectionTarget>()
            .First().Control.Code;

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            try
            {
                await Task.Delay(5, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(failureMessage);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class SyntheticMappingExecutionException : Exception;
}
