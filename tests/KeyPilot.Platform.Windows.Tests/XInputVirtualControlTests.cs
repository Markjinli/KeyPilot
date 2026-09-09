using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class XInputVirtualControlTests
{
    public static Task TriggerThresholdsAreHystereticAsync()
    {
        var state = XInputVirtualControlState.None;
        var belowPress = Update(state, leftTrigger: 29);
        Assert(belowPress.Transitions.Count == 0, "LT must stay released below its press threshold.");

        var pressed = Update(belowPress.ActiveControls, leftTrigger: 30);
        AssertSequence(
            [new XInputVirtualControlTransition(XInputVirtualControl.LeftTrigger, true)],
            pressed.Transitions);

        var hysteresis = Update(pressed.ActiveControls, leftTrigger: 25);
        Assert(hysteresis.Transitions.Count == 0, "LT must remain pressed inside the hysteresis band.");

        var released = Update(hysteresis.ActiveControls, leftTrigger: 20);
        AssertSequence(
            [new XInputVirtualControlTransition(XInputVirtualControl.LeftTrigger, false)],
            released.Transitions);
        return Task.CompletedTask;
    }

    public static Task StickDirectionsHandleDiagonalsAndReversalAsync()
    {
        var diagonal = Update(
            XInputVirtualControlState.None,
            thumbLX: short.MinValue,
            thumbLY: short.MaxValue,
            thumbRX: short.MaxValue,
            thumbRY: short.MinValue);
        Assert(
            diagonal.Transitions.Select(item => item.Control).SequenceEqual(
            [
                XInputVirtualControl.LeftStickLeft,
                XInputVirtualControl.LeftStickUp,
                XInputVirtualControl.RightStickRight,
                XInputVirtualControl.RightStickDown
            ]),
            "Both axes of each diagonal must be reported and short.MinValue must stay safe.");

        var reversed = Update(diagonal.ActiveControls, thumbLX: short.MaxValue);
        AssertSequence(
            [
                new XInputVirtualControlTransition(XInputVirtualControl.LeftStickLeft, false),
                new XInputVirtualControlTransition(XInputVirtualControl.LeftStickUp, false),
                new XInputVirtualControlTransition(XInputVirtualControl.RightStickRight, false),
                new XInputVirtualControlTransition(XInputVirtualControl.RightStickDown, false),
                new XInputVirtualControlTransition(XInputVirtualControl.LeftStickRight, true)
            ],
            reversed.Transitions);

        var disconnected = XInputVirtualControlMapper.Update(
            reversed.ActiveControls,
            XInputSnapshot.Disconnected);
        AssertSequence(
            [new XInputVirtualControlTransition(XInputVirtualControl.LeftStickRight, false)],
            disconnected.Transitions);
        return Task.CompletedTask;
    }

    public static Task PollingPublishesVirtualAndRawStateAsync()
    {
        var reader = new AnalogScriptedXInputReader();
        using var source = new XInputGamepadSource(reader, TimeSpan.FromMilliseconds(1));
        using var disconnected = new ManualResetEventSlim();
        var virtualEvents = new List<string>();
        var rawStates = new List<XInputRawStateChangedEventArgs>();

        source.VirtualControlChanged += (_, input) =>
            virtualEvents.Add($"{input.Control}:{input.IsPressed}");
        source.RawStateChanged += (_, input) => rawStates.Add(input);
        source.ConnectionChanged += (_, input) =>
        {
            if (input.UserIndex == 0 && !input.IsConnected)
            {
                disconnected.Set();
            }
        };

        source.Start();
        Assert(disconnected.Wait(TimeSpan.FromSeconds(2)), "The analog script did not disconnect.");
        source.Stop();

        AssertSequence(
            [
                "LeftTrigger:True",
                "LeftStickRight:True",
                "LeftTrigger:False",
                "LeftStickRight:False"
            ],
            virtualEvents);
        Assert(
            rawStates.Any(state => state.IsConnected && state.Buttons == 0x0400 &&
                state.LeftTrigger == 30 && state.ThumbLX == XInputVirtualControlThresholds.LeftStickPress),
            "Raw diagnostics must retain reserved button bits and analog values.");
        Assert(rawStates.Any(state => !state.IsConnected), "Raw diagnostics must publish disconnect state.");
        return Task.CompletedTask;
    }

    private static XInputVirtualControlUpdate Update(
        XInputVirtualControlState previous,
        byte leftTrigger = 0,
        byte rightTrigger = 0,
        short thumbLX = 0,
        short thumbLY = 0,
        short thumbRX = 0,
        short thumbRY = 0) =>
        XInputVirtualControlMapper.Update(
            previous,
            new XInputSnapshot(
                true,
                1,
                0,
                leftTrigger,
                rightTrigger,
                thumbLX,
                thumbLY,
                thumbRX,
                thumbRY));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertSequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"Sequence mismatch. Expected [{string.Join(", ", expected)}], " +
                $"actual [{string.Join(", ", actual)}].");
        }
    }

    private sealed class AnalogScriptedXInputReader : IXInputStateReader
    {
        private int _slotZeroReads;

        public XInputSnapshot Read(int userIndex)
        {
            if (userIndex != 0)
            {
                return XInputSnapshot.Disconnected;
            }

            return Interlocked.Increment(ref _slotZeroReads) switch
            {
                1 => XInputSnapshot.Disconnected,
                2 => new XInputSnapshot(
                    true,
                    1,
                    0x0400,
                    LeftTrigger: 30,
                    ThumbLX: XInputVirtualControlThresholds.LeftStickPress),
                _ => XInputSnapshot.Disconnected
            };
        }
    }
}
