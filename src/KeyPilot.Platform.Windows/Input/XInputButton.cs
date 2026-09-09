namespace KeyPilot.Platform.Windows.Input;

/// <summary>The fourteen digital controls exposed by an XInput gamepad.</summary>
public enum XInputButton : ushort
{
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Menu = 0x0010,
    View = 0x0020,
    LStick = 0x0040,
    RStick = 0x0080,
    LB = 0x0100,
    RB = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000
}

/// <summary>One edge between two XInput button bit fields.</summary>
public readonly record struct XInputButtonTransition(XInputButton Button, bool IsPressed);

/// <summary>
/// Pure XInput bit-field conversion. Unknown/reserved bits are deliberately ignored.
/// </summary>
public static class XInputButtonMapper
{
    private static readonly XInputButton[] Buttons =
    [
        XInputButton.DPadUp,
        XInputButton.DPadDown,
        XInputButton.DPadLeft,
        XInputButton.DPadRight,
        XInputButton.Menu,
        XInputButton.View,
        XInputButton.LStick,
        XInputButton.RStick,
        XInputButton.LB,
        XInputButton.RB,
        XInputButton.A,
        XInputButton.B,
        XInputButton.X,
        XInputButton.Y
    ];

    /// <summary>Returns all known button edges in stable XInput bit order.</summary>
    public static IReadOnlyList<XInputButtonTransition> GetTransitions(
        ushort previousButtons,
        ushort currentButtons)
    {
        var changed = (ushort)(previousButtons ^ currentButtons);
        if (changed == 0)
        {
            return Array.Empty<XInputButtonTransition>();
        }

        var transitions = new List<XInputButtonTransition>(Buttons.Length);
        foreach (var button in Buttons)
        {
            var mask = (ushort)button;
            if ((changed & mask) != 0)
            {
                transitions.Add(
                    new XInputButtonTransition(
                        button,
                        IsPressed: (currentButtons & mask) != 0));
            }
        }

        return transitions;
    }
}
