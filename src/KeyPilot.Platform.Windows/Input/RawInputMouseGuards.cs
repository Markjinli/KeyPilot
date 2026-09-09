using System.Runtime.InteropServices;

namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Fail-closed RAWMOUSE button translation. Only middle / XBUTTON1 / XBUTTON2 edges are published;
/// left, right, wheel, hwheel, and move packets yield nothing.
/// </summary>
internal static class RawInputMouseGuards
{
    internal const ushort RiMouseMiddleDown = 0x0010;
    internal const ushort RiMouseMiddleUp = 0x0020;
    internal const ushort RiMouseButton4Down = 0x0040;
    internal const ushort RiMouseButton4Up = 0x0080;
    internal const ushort RiMouseButton5Down = 0x0100;
    internal const ushort RiMouseButton5Up = 0x0200;

    internal const ushort AllowedButtonMask =
        RiMouseMiddleDown | RiMouseMiddleUp |
        RiMouseButton4Down | RiMouseButton4Up |
        RiMouseButton5Down | RiMouseButton5Up;

    internal const ushort VkMButton = 0x04;
    internal const ushort VkXButton1 = 0x05;
    internal const ushort VkXButton2 = 0x06;

    internal const int RawMouseStructSize = 24;

    [StructLayout(LayoutKind.Explicit, Size = RawMouseStructSize)]
    internal struct RawMouseLayout
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    internal static IReadOnlyList<(ushort VirtualKey, bool IsPressed)> TranslateButtonFlags(ushort usButtonFlags)
    {
        var masked = (ushort)(usButtonFlags & AllowedButtonMask);
        if (masked == 0)
        {
            return [];
        }

        var results = new List<(ushort VirtualKey, bool IsPressed)>(3);
        Append(results, masked, RiMouseMiddleDown, RiMouseMiddleUp, VkMButton);
        Append(results, masked, RiMouseButton4Down, RiMouseButton4Up, VkXButton1);
        Append(results, masked, RiMouseButton5Down, RiMouseButton5Up, VkXButton2);
        return results;
    }

    private static void Append(
        List<(ushort VirtualKey, bool IsPressed)> results,
        ushort masked,
        ushort down,
        ushort up,
        ushort virtualKey)
    {
        if ((masked & down) != 0)
        {
            results.Add((virtualKey, true));
        }

        if ((masked & up) != 0)
        {
            results.Add((virtualKey, false));
        }
    }
}
