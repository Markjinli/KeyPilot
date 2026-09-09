using System.Runtime.InteropServices;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class RawInputMouseGuardsTests
{
    internal static Task TranslateButtonFlagsIsFailClosedAsync()
    {
        AssertPair(0x0010, RawInputMouseGuards.VkMButton, true, "middle down");
        AssertPair(0x0020, RawInputMouseGuards.VkMButton, false, "middle up");
        AssertPair(0x0040, RawInputMouseGuards.VkXButton1, true, "back down");
        AssertPair(0x0080, RawInputMouseGuards.VkXButton1, false, "back up");
        AssertPair(0x0100, RawInputMouseGuards.VkXButton2, true, "forward down");
        AssertPair(0x0200, RawInputMouseGuards.VkXButton2, false, "forward up");

        AssertEmpty(0x0001, "left down");
        AssertEmpty(0x0002, "left up");
        AssertEmpty(0x0004, "right down");
        AssertEmpty(0x0008, "right up");
        AssertEmpty(0x0400, "wheel");
        AssertEmpty(0x0800, "hwheel");
        AssertEmpty(0, "move");

        var leftAndMiddle = RawInputMouseGuards.TranslateButtonFlags(0x0001 | 0x0010);
        Assert(leftAndMiddle.Count == 1 && leftAndMiddle[0] == (RawInputMouseGuards.VkMButton, true),
            "Left+middle must publish only middle.");

        var middleAndBack = RawInputMouseGuards.TranslateButtonFlags(0x0010 | 0x0040);
        Assert(middleAndBack.Count == 2, "Middle+back must publish both edges.");
        Assert(middleAndBack[0] == (RawInputMouseGuards.VkMButton, true), "Middle must come first.");
        Assert(middleAndBack[1] == (RawInputMouseGuards.VkXButton1, true), "Back must come second.");
        return Task.CompletedTask;
    }

    internal static Task LowLevelMouseHookTranslatesMiddleAndXButtonsAsync()
    {
        Assert(LowLevelMouseButtonSource.TryTranslate(0x0207, 0, 0, out var middleDown, out var pressed) &&
            middleDown == RawInputMouseGuards.VkMButton && pressed,
            "WM_MBUTTONDOWN must be the middle button.");
        Assert(LowLevelMouseButtonSource.TryTranslate(0x0208, 0, 0, out var middleUp, out var released) &&
            middleUp == RawInputMouseGuards.VkMButton && !released,
            "WM_MBUTTONUP must release the middle button.");
        Assert(LowLevelMouseButtonSource.TryTranslate(0x020B, 0x00010000, 0, out var back, out var backDown) &&
            back == RawInputMouseGuards.VkXButton1 && backDown,
            "WM_XBUTTONDOWN + XBUTTON1 must be back.");
        Assert(LowLevelMouseButtonSource.TryTranslate(0x020C, 0x00020000, 0, out var forward, out var forwardUp) &&
            forward == RawInputMouseGuards.VkXButton2 && !forwardUp,
            "WM_XBUTTONUP + XBUTTON2 must be forward.");
        Assert(!LowLevelMouseButtonSource.TryTranslate(0x0201, 0, 0, out _, out _),
            "Left click must be ignored.");
        Assert(!LowLevelMouseButtonSource.TryTranslate(0x0207, 0, 0x00000001, out _, out _),
            "Injected middle clicks must be ignored.");
        return Task.CompletedTask;
    }

    internal static Task RawMouseLayoutIsTwentyFourBytesAsync()
    {
        Assert(
            Marshal.SizeOf<RawInputMouseGuards.RawMouseLayout>() == RawInputMouseGuards.RawMouseStructSize,
            "RAWMOUSE must be the explicit 24-byte Win32 layout.");
        Assert(RawInputMouseGuards.RawMouseStructSize == 24, "The documented RAWMOUSE size is 24 bytes.");
        return Task.CompletedTask;
    }

    private static void AssertPair(ushort flags, ushort virtualKey, bool isPressed, string name)
    {
        var translated = RawInputMouseGuards.TranslateButtonFlags(flags);
        Assert(translated.Count == 1, $"{name} must publish one edge.");
        Assert(translated[0] == (virtualKey, isPressed), $"{name} must map to VK 0x{virtualKey:X2}.");
    }

    private static void AssertEmpty(ushort flags, string name)
    {
        Assert(RawInputMouseGuards.TranslateButtonFlags(flags).Count == 0, $"{name} must be ignored.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
