using System.ComponentModel;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>Receives relative cursor deltas produced by the XInput motion controller.</summary>
public interface IRelativeMouseBackend
{
    void MoveRelative(int deltaX, int deltaY);
}

/// <summary>Emits marked, relative mouse movement through Win32 SendInput.</summary>
public sealed class WindowsRelativeMouseBackend : IRelativeMouseBackend
{
    internal const uint InputMouse = 0;
    internal const uint MouseEventMove = 0x0001;

    private readonly ISendInputNative _native;
    private readonly InputInjectionMarker _originMarker;

    public WindowsRelativeMouseBackend(InputInjectionMarker originMarker)
        : this(Win32SendInputNative.Instance, originMarker)
    {
    }

    internal WindowsRelativeMouseBackend(
        ISendInputNative native,
        InputInjectionMarker originMarker)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        if (originMarker.Value == 0)
        {
            throw new ArgumentException(
                "A non-zero injection marker is required.",
                nameof(originMarker));
        }

        _originMarker = originMarker;
    }

    public void MoveRelative(int deltaX, int deltaY)
    {
        if (deltaX == 0 && deltaY == 0)
        {
            return;
        }

        var input = new NativeInput
        {
            Type = InputMouse,
            Union = new NativeInputUnion
            {
                Mouse = new NativeMouseInput
                {
                    X = deltaX,
                    Y = deltaY,
                    Flags = MouseEventMove,
                    ExtraInformation = _originMarker.Value
                }
            }
        };
        var result = _native.Send(input);
        if (result.InsertedCount != 1)
        {
            var error = result.ErrorCode == 0 ? 31 : result.ErrorCode;
            throw new Win32Exception(error, "SendInput did not insert the relative mouse event.");
        }
    }
}
