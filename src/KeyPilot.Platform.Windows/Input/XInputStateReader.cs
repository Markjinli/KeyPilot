using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KeyPilot.Platform.Windows.Input;

internal readonly record struct XInputSnapshot(
    bool IsConnected,
    uint PacketNumber,
    ushort Buttons,
    byte LeftTrigger = 0,
    byte RightTrigger = 0,
    short ThumbLX = 0,
    short ThumbLY = 0,
    short ThumbRX = 0,
    short ThumbRY = 0)
{
    internal static XInputSnapshot Disconnected => new(false, 0, 0);
}

internal interface IXInputStateReader
{
    XInputSnapshot Read(int userIndex);
}

/// <summary>Read-only XInput 1.4 interop. This type intentionally imports no output API.</summary>
internal sealed class XInput14StateReader : IXInputStateReader
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorDeviceNotConnected = 1167;

    public XInputSnapshot Read(int userIndex)
    {
        if ((uint)userIndex >= XInputGamepadSource.UserSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(userIndex));
        }

        var result = XInputGetState((uint)userIndex, out var state);
        return result switch
        {
            ErrorSuccess => new XInputSnapshot(
                IsConnected: true,
                state.PacketNumber,
                state.Gamepad.Buttons,
                state.Gamepad.LeftTrigger,
                state.Gamepad.RightTrigger,
                state.Gamepad.ThumbLX,
                state.Gamepad.ThumbLY,
                state.Gamepad.ThumbRX,
                state.Gamepad.ThumbRY),
            ErrorDeviceNotConnected => XInputSnapshot.Disconnected,
            _ => throw new Win32Exception(
                unchecked((int)result),
                $"XInputGetState failed for user slot {userIndex}.")
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeXInputState
    {
        public uint PacketNumber;
        public NativeXInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeXInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputGetState(uint userIndex, out NativeXInputState state);
}
