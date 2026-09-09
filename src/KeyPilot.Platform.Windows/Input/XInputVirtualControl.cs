namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Stable button-like controls derived from XInput trigger and thumb-stick values.
/// These values are application identifiers, not bits from XINPUT_GAMEPAD.wButtons.
/// </summary>
public enum XInputVirtualControl
{
    LeftTrigger = 1,
    RightTrigger = 2,
    LeftStickLeft = 3,
    LeftStickRight = 4,
    LeftStickUp = 5,
    LeftStickDown = 6,
    RightStickLeft = 7,
    RightStickRight = 8,
    RightStickUp = 9,
    RightStickDown = 10
}

/// <summary>Fixed press and release thresholds used to turn analog values into stable edges.</summary>
public static class XInputVirtualControlThresholds
{
    public const byte TriggerPress = 30;
    public const byte TriggerRelease = 20;
    public const short LeftStickPress = 7_849;
    public const short LeftStickRelease = 6_000;
    public const short RightStickPress = 8_689;
    public const short RightStickRelease = 6_500;
}

/// <summary>One press or release edge from a threshold-derived XInput control.</summary>
public readonly record struct XInputVirtualControlTransition(
    XInputVirtualControl Control,
    bool IsPressed);

[Flags]
internal enum XInputVirtualControlState : ushort
{
    None = 0,
    LeftTrigger = 1 << 0,
    RightTrigger = 1 << 1,
    LeftStickLeft = 1 << 2,
    LeftStickRight = 1 << 3,
    LeftStickUp = 1 << 4,
    LeftStickDown = 1 << 5,
    RightStickLeft = 1 << 6,
    RightStickRight = 1 << 7,
    RightStickUp = 1 << 8,
    RightStickDown = 1 << 9
}

internal readonly record struct XInputVirtualControlUpdate(
    XInputVirtualControlState ActiveControls,
    IReadOnlyList<XInputVirtualControlTransition> Transitions);

/// <summary>
/// Converts analog XInput state into button-like edges. Release thresholds are lower than press
/// thresholds so normal trigger and stick jitter cannot repeatedly toggle a mapping.
/// </summary>
internal static class XInputVirtualControlMapper
{
    private static readonly (XInputVirtualControl Control, XInputVirtualControlState State)[] Controls =
    [
        (XInputVirtualControl.LeftTrigger, XInputVirtualControlState.LeftTrigger),
        (XInputVirtualControl.RightTrigger, XInputVirtualControlState.RightTrigger),
        (XInputVirtualControl.LeftStickLeft, XInputVirtualControlState.LeftStickLeft),
        (XInputVirtualControl.LeftStickRight, XInputVirtualControlState.LeftStickRight),
        (XInputVirtualControl.LeftStickUp, XInputVirtualControlState.LeftStickUp),
        (XInputVirtualControl.LeftStickDown, XInputVirtualControlState.LeftStickDown),
        (XInputVirtualControl.RightStickLeft, XInputVirtualControlState.RightStickLeft),
        (XInputVirtualControl.RightStickRight, XInputVirtualControlState.RightStickRight),
        (XInputVirtualControl.RightStickUp, XInputVirtualControlState.RightStickUp),
        (XInputVirtualControl.RightStickDown, XInputVirtualControlState.RightStickDown)
    ];

    public static XInputVirtualControlUpdate Update(
        XInputVirtualControlState previous,
        XInputSnapshot current)
    {
        var next = current.IsConnected ? GetActiveControls(previous, current) : XInputVirtualControlState.None;
        var changed = previous ^ next;
        if (changed == XInputVirtualControlState.None)
        {
            return new XInputVirtualControlUpdate(
                next,
                Array.Empty<XInputVirtualControlTransition>());
        }

        var transitions = new List<XInputVirtualControlTransition>(Controls.Length);

        // Publish every release before any press. A stick that crosses directly from one side to
        // the other therefore cannot be observed as having both opposing directions held.
        AddTransitions(transitions, changed, previous, isPressed: false);
        AddTransitions(transitions, changed, next, isPressed: true);
        return new XInputVirtualControlUpdate(next, transitions);
    }

    private static XInputVirtualControlState GetActiveControls(
        XInputVirtualControlState previous,
        XInputSnapshot current)
    {
        var next = XInputVirtualControlState.None;
        next = Set(
            next,
            XInputVirtualControlState.LeftTrigger,
            TriggerIsActive(
                current.LeftTrigger,
                previous.HasFlag(XInputVirtualControlState.LeftTrigger)));
        next = Set(
            next,
            XInputVirtualControlState.RightTrigger,
            TriggerIsActive(
                current.RightTrigger,
                previous.HasFlag(XInputVirtualControlState.RightTrigger)));

        next = SetAxis(
            next,
            previous,
            current.ThumbLX,
            XInputVirtualControlState.LeftStickLeft,
            XInputVirtualControlState.LeftStickRight,
            XInputVirtualControlThresholds.LeftStickPress,
            XInputVirtualControlThresholds.LeftStickRelease);
        next = SetAxis(
            next,
            previous,
            current.ThumbLY,
            XInputVirtualControlState.LeftStickDown,
            XInputVirtualControlState.LeftStickUp,
            XInputVirtualControlThresholds.LeftStickPress,
            XInputVirtualControlThresholds.LeftStickRelease);
        next = SetAxis(
            next,
            previous,
            current.ThumbRX,
            XInputVirtualControlState.RightStickLeft,
            XInputVirtualControlState.RightStickRight,
            XInputVirtualControlThresholds.RightStickPress,
            XInputVirtualControlThresholds.RightStickRelease);
        next = SetAxis(
            next,
            previous,
            current.ThumbRY,
            XInputVirtualControlState.RightStickDown,
            XInputVirtualControlState.RightStickUp,
            XInputVirtualControlThresholds.RightStickPress,
            XInputVirtualControlThresholds.RightStickRelease);
        return next;
    }

    private static XInputVirtualControlState SetAxis(
        XInputVirtualControlState next,
        XInputVirtualControlState previous,
        short value,
        XInputVirtualControlState negative,
        XInputVirtualControlState positive,
        short pressThreshold,
        short releaseThreshold)
    {
        next = Set(
            next,
            negative,
            NegativeAxisIsActive(value, previous.HasFlag(negative), pressThreshold, releaseThreshold));
        return Set(
            next,
            positive,
            PositiveAxisIsActive(value, previous.HasFlag(positive), pressThreshold, releaseThreshold));
    }

    private static bool TriggerIsActive(byte value, bool wasActive) => wasActive
        ? value > XInputVirtualControlThresholds.TriggerRelease
        : value >= XInputVirtualControlThresholds.TriggerPress;

    private static bool NegativeAxisIsActive(
        short value,
        bool wasActive,
        short pressThreshold,
        short releaseThreshold) => wasActive
            ? value < -releaseThreshold
            : value <= -pressThreshold;

    private static bool PositiveAxisIsActive(
        short value,
        bool wasActive,
        short pressThreshold,
        short releaseThreshold) => wasActive
            ? value > releaseThreshold
            : value >= pressThreshold;

    private static XInputVirtualControlState Set(
        XInputVirtualControlState state,
        XInputVirtualControlState flag,
        bool active) => active ? state | flag : state;

    private static void AddTransitions(
        ICollection<XInputVirtualControlTransition> transitions,
        XInputVirtualControlState changed,
        XInputVirtualControlState active,
        bool isPressed)
    {
        foreach (var definition in Controls)
        {
            if ((changed & definition.State) != 0 &&
                (active & definition.State) != 0)
            {
                transitions.Add(new XInputVirtualControlTransition(definition.Control, isPressed));
            }
        }
    }
}
