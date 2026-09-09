using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Triggers;

internal static class TriggerStateMachineTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
        new (string Name, Action Run)[]
        {
            ("触发器：单击在释放时仅触发一次", SinglePressCompletesOnRelease),
            ("触发器：按下与释放严格对应物理边沿", KeyEdgesFireOnce),
            ("触发器：长按按确定阈值触发一次", LongPressUsesDeterministicDeadline),
            ("触发器：提前释放取消长按", EarlyReleaseCancelsLongPress),
            ("触发器：双击接受窗口边界", DoublePressAcceptsWindowBoundary),
            ("触发器：过期双击重新计数", ExpiredDoublePressStartsAgain),
            ("触发器：相同控制在不同设备上隔离", PhysicalDevicesDoNotShareClickState),
            ("触发器：精确设备映射不匹配其他设备", ExactDeviceSelectorIsHonored),
            ("触发器：注入事件不触发映射", InjectedEventsAreIgnored),
            ("触发器：重置清除所有待定状态", ResetClearsPendingState)
        };

    private static void SinglePressCompletesOnRelease()
    {
        var mapping = CreateMapping(MappingTriggerKind.SinglePress);
        var machine = new MappingTriggerStateMachine(new[] { mapping });

        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 0)), "press");
        AssertEmpty(machine.Process(Event(InputEventPhase.Repeated, 15)), "repeat");
        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 20)), "duplicate press");

        var activations = machine.Process(Event(InputEventPhase.Released, 30));
        AssertEqual(1, activations.Count, "single activation count");
        AssertEqual(mapping.Id, activations[0].Mapping.Id, "mapping id");
        AssertEqual(InputEventPhase.Released, activations[0].OriginatingEvent.Phase, "origin phase");
        AssertEqual(Epoch.AddMilliseconds(30), activations[0].TriggeredAtUtc, "activation time");

        AssertEmpty(machine.Process(Event(InputEventPhase.Released, 40)), "stray release");
    }

    private static void KeyEdgesFireOnce()
    {
        var keyDown = new MappingTriggerStateMachine(
            new[] { CreateMapping(MappingTriggerKind.KeyDown) });
        AssertEqual(1, keyDown.Process(Event(InputEventPhase.Pressed, 0)).Count, "first key down");
        AssertEmpty(keyDown.Process(Event(InputEventPhase.Repeated, 10)), "key-down repeat");
        AssertEmpty(keyDown.Process(Event(InputEventPhase.Pressed, 20)), "duplicate key down");
        AssertEmpty(keyDown.Process(Event(InputEventPhase.Released, 30)), "key-down release");
        AssertEqual(1, keyDown.Process(Event(InputEventPhase.Pressed, 40)).Count, "rearmed key down");

        var keyUp = new MappingTriggerStateMachine(
            new[] { CreateMapping(MappingTriggerKind.KeyUp) });
        AssertEmpty(keyUp.Process(Event(InputEventPhase.Released, 0)), "stray key up");
        AssertEmpty(keyUp.Process(Event(InputEventPhase.Pressed, 10)), "key-up press");
        AssertEmpty(keyUp.Process(Event(InputEventPhase.Repeated, 20)), "key-up repeat");
        AssertEqual(1, keyUp.Process(Event(InputEventPhase.Released, 30)).Count, "real key up");
        AssertEmpty(keyUp.Process(Event(InputEventPhase.Released, 40)), "duplicate key up");
    }

    private static void LongPressUsesDeterministicDeadline()
    {
        var mapping = CreateMapping(MappingTriggerKind.LongPress, longPressMilliseconds: 600);
        var machine = new MappingTriggerStateMachine(new[] { mapping });

        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 0)), "long press start");
        AssertEqual(Epoch.AddMilliseconds(600), machine.NextDeadlineUtc, "next deadline");
        AssertEmpty(machine.Process(Event(InputEventPhase.Repeated, 100)), "early repeat");
        AssertEmpty(machine.AdvanceTo(Epoch.AddMilliseconds(599)), "before threshold");

        var activations = machine.AdvanceTo(Epoch.AddMilliseconds(600));
        AssertEqual(1, activations.Count, "long-press activation count");
        AssertEqual(Epoch.AddMilliseconds(600), activations[0].TriggeredAtUtc, "long-press time");
        AssertEqual(InputEventPhase.Pressed, activations[0].OriginatingEvent.Phase, "long-press origin");
        AssertEqual<DateTimeOffset?>(null, machine.NextDeadlineUtc, "deadline after activation");

        AssertEmpty(machine.Process(Event(InputEventPhase.Repeated, 700)), "repeat after activation");
        AssertEmpty(machine.Process(Event(InputEventPhase.Released, 800)), "release after activation");
    }

    private static void EarlyReleaseCancelsLongPress()
    {
        var machine = new MappingTriggerStateMachine(
            new[] { CreateMapping(MappingTriggerKind.LongPress, longPressMilliseconds: 600) });

        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 0)), "long press start");
        AssertEmpty(machine.Process(Event(InputEventPhase.Released, 599)), "early release");
        AssertEmpty(machine.AdvanceTo(Epoch.AddSeconds(2)), "cancelled deadline");
    }

    private static void DoublePressAcceptsWindowBoundary()
    {
        var mapping = CreateMapping(MappingTriggerKind.DoublePress, doublePressWindowMilliseconds: 350);
        var machine = new MappingTriggerStateMachine(new[] { mapping });

        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 0)), "first down");
        AssertEmpty(machine.Process(Event(InputEventPhase.Released, 20)), "first click");
        AssertEqual(Epoch.AddMilliseconds(370), machine.NextDeadlineUtc, "double deadline");
        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 370)), "second down at deadline");
        AssertEmpty(machine.Process(Event(InputEventPhase.Repeated, 380)), "second-click repeat");

        var activations = machine.Process(Event(InputEventPhase.Released, 390));
        AssertEqual(1, activations.Count, "double activation count");
        AssertEqual(InputEventPhase.Released, activations[0].OriginatingEvent.Phase, "double origin");
        AssertEqual(Epoch.AddMilliseconds(390), activations[0].TriggeredAtUtc, "double time");
    }

    private static void ExpiredDoublePressStartsAgain()
    {
        var machine = new MappingTriggerStateMachine(
            new[] { CreateMapping(MappingTriggerKind.DoublePress, doublePressWindowMilliseconds: 350) });

        CompleteClick(machine, "pad-a", 0, 20);
        AssertEmpty(machine.AdvanceTo(Epoch.AddMilliseconds(371)), "expire first click");
        CompleteClick(machine, "pad-a", 400, 420);

        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 600)), "new second down");
        AssertEqual(1, machine.Process(Event(InputEventPhase.Released, 620)).Count, "new double click");
    }

    private static void PhysicalDevicesDoNotShareClickState()
    {
        var mapping = CreateMapping(MappingTriggerKind.DoublePress, doublePressWindowMilliseconds: 350);
        var machine = new MappingTriggerStateMachine(new[] { mapping });

        CompleteClick(machine, "keyboard-a", 0, 20);
        CompleteClick(machine, "keyboard-b", 40, 60);

        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 100, "keyboard-a")), "device A second down");
        var deviceA = machine.Process(Event(InputEventPhase.Released, 120, "keyboard-a"));
        AssertEqual(1, deviceA.Count, "device A double");
        AssertEqual("keyboard-a", deviceA[0].OriginatingEvent.Source.Device.DeviceId, "device A identity");

        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 140, "keyboard-b")), "device B second down");
        var deviceB = machine.Process(Event(InputEventPhase.Released, 160, "keyboard-b"));
        AssertEqual(1, deviceB.Count, "device B double");
        AssertEqual("keyboard-b", deviceB[0].OriginatingEvent.Source.Device.DeviceId, "device B identity");
    }

    private static void ExactDeviceSelectorIsHonored()
    {
        var source = Source("keyboard-a", DeviceMatchMode.ExactDevice);
        var mapping = CreateMapping(MappingTriggerKind.KeyDown, source: source);
        var machine = new MappingTriggerStateMachine(new[] { mapping });

        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 0, "keyboard-b")), "other device");
        AssertEqual(1, machine.Process(Event(InputEventPhase.Pressed, 10, "keyboard-a")).Count, "exact device");
    }

    private static void InjectedEventsAreIgnored()
    {
        var machine = new MappingTriggerStateMachine(
            new[] { CreateMapping(MappingTriggerKind.KeyDown) });

        AssertEmpty(machine.Process(Event(InputEventPhase.Pressed, 0, injected: true)), "injected press");
        AssertEmpty(machine.Process(Event(InputEventPhase.Released, 10, injected: true)), "injected release");
        AssertEqual(1, machine.Process(Event(InputEventPhase.Pressed, 20)).Count, "physical press");
    }

    private static void ResetClearsPendingState()
    {
        var machine = new MappingTriggerStateMachine(
            new[] { CreateMapping(MappingTriggerKind.DoublePress) });

        CompleteClick(machine, "pad-a", 0, 20);
        machine.Reset();
        AssertEqual<DateTimeOffset?>(null, machine.NextDeadlineUtc, "deadline after reset");

        CompleteClick(machine, "pad-a", 0, 20);
        AssertEmpty(machine.AdvanceTo(Epoch.AddMilliseconds(21)), "fresh first click after reset");
    }

    private static void CompleteClick(
        MappingTriggerStateMachine machine,
        string deviceId,
        int pressedAtMilliseconds,
        int releasedAtMilliseconds)
    {
        AssertEmpty(
            machine.Process(Event(InputEventPhase.Pressed, pressedAtMilliseconds, deviceId)),
            $"{deviceId} press");
        AssertEmpty(
            machine.Process(Event(InputEventPhase.Released, releasedAtMilliseconds, deviceId)),
            $"{deviceId} release");
    }

    private static InputMapping CreateMapping(
        MappingTriggerKind kind,
        int longPressMilliseconds = 600,
        int doublePressWindowMilliseconds = 350,
        InputSource? source = null) =>
        new()
        {
            Name = kind.ToString(),
            Source = source ?? Source(null, DeviceMatchMode.AnyOfKind),
            Trigger = new MappingTrigger
            {
                Kind = kind,
                LongPressMilliseconds = longPressMilliseconds,
                DoublePressWindowMilliseconds = doublePressWindowMilliseconds
            },
            Action = new SendKeyAction
            {
                Target = new InputControlId
                {
                    Kind = InputControlKind.KeyboardScanCode,
                    Code = 0x20
                }
            }
        };

    private static InputEvent Event(
        InputEventPhase phase,
        int milliseconds,
        string deviceId = "pad-a",
        bool injected = false) =>
        new()
        {
            Source = Source(deviceId, DeviceMatchMode.ExactDevice),
            Phase = phase,
            TimestampUtc = Epoch.AddMilliseconds(milliseconds),
            SequenceNumber = milliseconds,
            IsInjected = injected
        };

    private static InputSource Source(string? deviceId, DeviceMatchMode matchMode) =>
        new()
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Keyboard,
                MatchMode = matchMode,
                DeviceId = deviceId,
                VendorId = 0x1234,
                ProductId = 0x5678
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.KeyboardScanCode,
                Code = 0x1E
            }
        };

    private static void AssertEmpty<T>(IReadOnlyCollection<T> items, string description) =>
        AssertEqual(0, items.Count, description);

    private static void AssertEqual<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}.");
        }
    }
}
