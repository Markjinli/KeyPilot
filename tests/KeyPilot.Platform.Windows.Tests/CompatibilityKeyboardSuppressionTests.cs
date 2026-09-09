using KeyPilot.Core.Actions;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class CompatibilityKeyboardSuppressionTests
{
    public static Task RulesStayKeyboardOnlyAndProtectEmergencyKeysAsync()
    {
        var rightCtrl = Source(0x1D, 0x0002);
        var rules = CompatibilityKeyboardRuleSet.Create(new[]
        {
            rightCtrl,
            Source(0x1D, 0x0000),
            Source(0x2A, 0x0000),
            Source(0x58, 0x0000),
            Source(0x38, 0x0000),
            Source(0x53, 0x0002),
            Source(0x38, 0x0002),
            Source(0x53, 0x0000),
            Source(0x1E, 0x0000, DeviceMatchMode.ExactDevice)
        });

        Require(rules.Count == 3, "Only right Ctrl, right Alt, and numpad Delete may remain.");
        Require(CompatibilityKeyboardKey.TryCreate(rightCtrl, out var rightCtrlKey) && rules.Contains(rightCtrlKey), "Right Ctrl must remain mappable.");
        Require(!rules.Contains(new CompatibilityKeyboardKey(0x1D, 0)), "Left Ctrl must be reserved.");
        Require(!rules.Contains(new CompatibilityKeyboardKey(0x2A, 0)), "Left Shift must be reserved.");
        Require(!rules.Contains(new CompatibilityKeyboardKey(0x58, 0)), "F12 must be reserved for the bypass chord.");
        Require(!rules.Contains(new CompatibilityKeyboardKey(0x38, 0)), "Left Alt must stay available for SAS.");
        Require(!rules.Contains(new CompatibilityKeyboardKey(0x53, 2)), "E0 Delete must stay available for SAS.");
        return Task.CompletedTask;
    }

    public static Task ToggleGenerationQuarantinesHeldEdgesAsync()
    {
        var key = new CompatibilityKeyboardKey(0x1E, 0);
        var tracker = new CompatibilityKeyboardEdgeTracker();
        tracker.AdvanceGeneration(1);
        Require(!tracker.TryObserve(key, isKeyDown: false, out _), "An unmatched release must pass through.");
        Require(tracker.TryObserve(key, isKeyDown: true, out var pressed) && pressed == InputEventPhase.Pressed, "First down must be Pressed.");
        Require(tracker.TryObserve(key, isKeyDown: true, out var repeated) && repeated == InputEventPhase.Repeated, "Held down must be Repeated.");

        tracker.ObservePassThrough(key, isKeyDown: true);
        tracker.AdvanceGeneration(2);
        Require(!tracker.TryObserve(key, isKeyDown: true, out _), "An autorepeat from a pass-through held key must stay quarantined.");
        Require(!tracker.TryObserve(key, isKeyDown: false, out _), "The release paired with a pass-through down must also pass through.");
        Require(tracker.TryObserve(key, isKeyDown: true, out pressed) && pressed == InputEventPhase.Pressed, "A fresh down after release must re-arm suppression.");
        return Task.CompletedTask;
    }

    public static Task RawInputTokensSurviveGenerationChangesButStayBoundedAsync()
    {
        var clock = 1_000L;
        var key = new PendingRawInputKey(new CompatibilityKeyboardKey(0x1E, 0), IsKeyDown: true);
        var otherKey = new PendingRawInputKey(new CompatibilityKeyboardKey(0x30, 0), IsKeyDown: true);
        var tracker = new PendingRawInputTracker(2, TimeSpan.FromSeconds(30), () => clock);

        Require(tracker.TryReserve(key, generation: 1, out var olderToken), "The first dedupe token must be reserved.");
        Require(tracker.TryReserve(key, generation: 2, out var newerToken), "A new-generation token must not replace the old one.");
        Require(tracker.Count == 2, "Both generations must remain pending until their Raw Input packets arrive.");
        Require(tracker.Cancel(key, newerToken), "A failed enqueue must cancel only its own reservation.");
        Require(tracker.TryConsume(key, out var generation) && generation == 1,
            "A rule switch must not erase the already-suppressed packet's dedupe token.");
        Require(!tracker.TryConsume(key), "A token must be consumed at most once.");

        Require(tracker.TryReserve(key, generation: 3, out _), "Capacity must become available after consumption.");
        Require(tracker.TryReserve(otherKey, generation: 3, out _), "The configured capacity must be usable.");
        Require(!tracker.TryReserve(otherKey, generation: 4, out _), "The tracker must fail closed to reservation at its hard capacity.");

        clock += 30_001;
        Require(tracker.Count == 0, "Unmatched dedupe tokens must expire.");
        Require(!tracker.TryConsume(key), "An expired token must never swallow a later pass-through packet.");
        Require(olderToken != newerToken, "Reservations require unique identities for exact cancellation.");
        return Task.CompletedTask;
    }

    public static Task RuntimeRevisionChangesEvenWhenRulesStayEqualAsync()
    {
        using var suppression = new LowLevelKeyboardSuppressionSource(
            new InputInjectionMarker(0x4B50544C));
        var source = Source(0x1E, 0x0000);

        suppression.ApplySources(new[] { source }, runtimeRevision: 10);
        Require(suppression.IsRuntimeRevisionCurrent(10),
            "The first compatibility policy must expose its runtime revision.");
        suppression.ApplySources(new[] { source }, runtimeRevision: 11);
        Require(!suppression.IsRuntimeRevisionCurrent(10),
            "Equal suppression rules from an old foreground context must still become stale.");
        Require(suppression.IsRuntimeRevisionCurrent(11),
            "The replacement foreground context must become current atomically.");
        return Task.CompletedTask;
    }

    public static async Task CommitReplayStaysFifoWhileCompletionIsDetachedAsync()
    {
        var firstCommit = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replayed = new List<string>();
        var committed = new List<string>();
        var dispatcher = new OrderedCommitDispatcher<string>(
            (item, _) => committed.Add(item),
            (item, _) =>
            {
                replayed.Add(item);
                return ValueTask.CompletedTask;
            });

        await dispatcher.EnqueueAsync("down", firstCommit.Task, blockedCompletion.Task);
        await dispatcher.EnqueueAsync("up", Task.FromResult(false), null);
        dispatcher.Complete();
        await Task.Delay(30);
        Require(replayed.Count == 0, "A later rejected release must not overtake the pending down decision.");

        firstCommit.TrySetResult(false);
        await dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Require(replayed.SequenceEqual(new[] { "down", "up" }),
            "Required replay edges must preserve physical FIFO order.");

        var detached = new OrderedCommitDispatcher<string>(
            (item, _) => committed.Add(item),
            (_, _) => ValueTask.CompletedTask);
        await detached.EnqueueAsync("macro-down", Task.FromResult(true), blockedCompletion.Task);
        await detached.EnqueueAsync("next-edge", Task.FromResult(true), Task.FromResult(true));
        detached.Complete();
        await detached.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Require(committed.TakeLast(2).SequenceEqual(new[] { "macro-down", "next-edge" }),
            "A long action completion must not block later commit decisions.");
        Require(!blockedCompletion.Task.IsCompleted,
            "The dispatcher test requires the first action completion to remain pending.");
    }

    private static InputSource Source(
        int makeCode,
        ushort prefix,
        DeviceMatchMode matchMode = DeviceMatchMode.AnyOfKind) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Keyboard,
            MatchMode = matchMode,
            DeviceId = matchMode == DeviceMatchMode.ExactDevice ? "DEVICE-A" : null
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.KeyboardScanCode,
            Code = makeCode,
            IsExtended = prefix != 0,
            RawQualifier = $"RAWKEYBOARD-V1;PREFIX={prefix:X4}"
        }
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
