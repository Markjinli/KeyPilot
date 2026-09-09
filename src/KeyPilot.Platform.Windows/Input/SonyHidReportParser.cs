namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Clean-room parser for public DualShock 4 / DualSense USB and Bluetooth input reports.
/// Digital buttons are mapped onto XInput bit values so existing AnyOfKind gamepad tiles still match.
/// </summary>
public readonly record struct SonyHidPadState(
    byte LeftStickX,
    byte LeftStickY,
    byte RightStickX,
    byte RightStickY,
    byte LeftTrigger,
    byte RightTrigger,
    ushort Buttons,
    byte? BatteryPercent = null,
    bool? Charging = null);

public static class SonyHidReportParser
{
    public static bool TryParse(uint productId, ReadOnlySpan<byte> report, out SonyHidPadState state)
    {
        state = default;
        if (report.IsEmpty)
        {
            return false;
        }

        return SonyHidIdentity.IsDualSense(productId)
            ? TryParseDualSense(report, out state)
            : TryParseDualShock4(report, out state);
    }

    public static IReadOnlyList<XInputButtonTransition> ButtonTransitions(
        ushort previousButtons,
        ushort currentButtons)
    {
        var changed = (ushort)(previousButtons ^ currentButtons);
        if (changed == 0)
        {
            return Array.Empty<XInputButtonTransition>();
        }

        ushort[] masks =
        [
            (ushort)XInputButton.DPadUp,
            (ushort)XInputButton.DPadDown,
            (ushort)XInputButton.DPadLeft,
            (ushort)XInputButton.DPadRight,
            (ushort)XInputButton.Menu,
            (ushort)XInputButton.View,
            (ushort)XInputButton.LStick,
            (ushort)XInputButton.RStick,
            (ushort)XInputButton.LB,
            (ushort)XInputButton.RB,
            (ushort)XInputButton.A,
            (ushort)XInputButton.B,
            (ushort)XInputButton.X,
            (ushort)XInputButton.Y,
            SonyHidIdentity.PsButton,
            SonyHidIdentity.TouchpadClick
        ];

        var transitions = new List<XInputButtonTransition>(masks.Length);
        foreach (var mask in masks)
        {
            if ((changed & mask) == 0)
            {
                continue;
            }

            transitions.Add(new XInputButtonTransition((XInputButton)mask, (currentButtons & mask) != 0));
        }

        return transitions;
    }

    private static bool TryParseDualShock4(ReadOnlySpan<byte> report, out SonyHidPadState state)
    {
        // USB report-id 0x01: sticks at 1-4, hat/face at 5, shoulders at 6, PS/touch at 7, triggers at 8-9.
        // BT report-id 0x11: same fields two bytes later after the extra HID header.
        var offset = report[0] switch
        {
            0x01 => 1,
            0x11 when report.Length >= 12 => 3,
            _ => -1
        };
        if (offset < 0 || report.Length < offset + 9)
        {
            state = default;
            return false;
        }

        state = new SonyHidPadState(
            report[offset],
            report[offset + 1],
            report[offset + 2],
            report[offset + 3],
            report[offset + 7],
            report[offset + 8],
            PackDualShockButtons(report[offset + 4], report[offset + 5], report[offset + 6]));
        return true;
    }

    private static bool TryParseDualSense(ReadOnlySpan<byte> report, out SonyHidPadState state)
    {
        // USB report-id 0x01: sticks 1-4, triggers 5-6, hat/face 8, shoulders 9, PS/touch 10.
        // BT report-id 0x31: one extra prefix byte before the USB payload.
        var offset = report[0] switch
        {
            0x01 => 1,
            0x31 when report.Length >= 12 => 2,
            _ => -1
        };
        if (offset < 0 || report.Length < offset + 10)
        {
            state = default;
            return false;
        }

        byte? batteryPercent = null;
        bool? charging = null;
        if (report.Length >= offset + 54)
        {
            var batteryByte = report[offset + 52];
            charging = (report[offset + 53] & 0x08) != 0;
            batteryPercent = (batteryByte & 0x20) != 0
                ? (byte)100
                : (byte)Math.Min((batteryByte & 0x0F) * 100 / 8, 100);
        }

        state = new SonyHidPadState(
            report[offset],
            report[offset + 1],
            report[offset + 2],
            report[offset + 3],
            report[offset + 4],
            report[offset + 5],
            PackDualSenseButtons(report[offset + 7], report[offset + 8], report[offset + 9]),
            batteryPercent,
            charging);
        return true;
    }

    private static ushort PackDualShockButtons(byte hatFace, byte shoulders, byte extras)
    {
        var buttons = HatToXInput((byte)(hatFace & 0x0F));
        if ((hatFace & 0x10) != 0)
        {
            buttons |= (ushort)XInputButton.X; // Square
        }

        if ((hatFace & 0x20) != 0)
        {
            buttons |= (ushort)XInputButton.A; // Cross
        }

        if ((hatFace & 0x40) != 0)
        {
            buttons |= (ushort)XInputButton.B; // Circle
        }

        if ((hatFace & 0x80) != 0)
        {
            buttons |= (ushort)XInputButton.Y; // Triangle
        }

        if ((shoulders & 0x01) != 0)
        {
            buttons |= (ushort)XInputButton.LB;
        }

        if ((shoulders & 0x02) != 0)
        {
            buttons |= (ushort)XInputButton.RB;
        }

        if ((shoulders & 0x10) != 0)
        {
            buttons |= (ushort)XInputButton.View; // Share
        }

        if ((shoulders & 0x20) != 0)
        {
            buttons |= (ushort)XInputButton.Menu; // Options
        }

        if ((shoulders & 0x40) != 0)
        {
            buttons |= (ushort)XInputButton.LStick;
        }

        if ((shoulders & 0x80) != 0)
        {
            buttons |= (ushort)XInputButton.RStick;
        }

        if ((extras & 0x01) != 0)
        {
            buttons |= SonyHidIdentity.PsButton;
        }

        if ((extras & 0x02) != 0)
        {
            buttons |= SonyHidIdentity.TouchpadClick;
        }

        return buttons;
    }

    private static ushort PackDualSenseButtons(byte hatFace, byte shoulders, byte extras) =>
        PackDualShockButtons(hatFace, shoulders, extras);

    private static ushort HatToXInput(byte hat) => hat switch
    {
        0 => (ushort)XInputButton.DPadUp,
        1 => (ushort)(XInputButton.DPadUp | XInputButton.DPadRight),
        2 => (ushort)XInputButton.DPadRight,
        3 => (ushort)(XInputButton.DPadDown | XInputButton.DPadRight),
        4 => (ushort)XInputButton.DPadDown,
        5 => (ushort)(XInputButton.DPadDown | XInputButton.DPadLeft),
        6 => (ushort)XInputButton.DPadLeft,
        7 => (ushort)(XInputButton.DPadUp | XInputButton.DPadLeft),
        _ => 0
    };
}
