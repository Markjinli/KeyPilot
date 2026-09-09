using System.ComponentModel;
using System.Runtime.InteropServices;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>
/// Emits keyboard input through Win32 SendInput. This backend intentionally supports only
/// keyboard scan-code and virtual-key targets; HID usages, gamepad buttons, and captured vendor
/// reports require a virtual-HID/driver backend and are rejected rather than approximated.
/// </summary>
public sealed class WindowsSendInputBackend : IInputInjectionBackend
{
    private readonly ISendInputNative _native;

    public WindowsSendInputBackend()
        : this(Win32SendInputNative.Instance)
    {
    }

    internal WindowsSendInputBackend(ISendInputNative native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>Checks whether the current SendInput backend can encode a control without emitting it.</summary>
    public static bool TryValidateControl(InputControlId? control, out string error)
    {
        if (control is null)
        {
            error = "The input control is missing.";
            return false;
        }

        try
        {
            KeyboardSendInputEncoder.ValidateTarget(new ControlInjectionTarget(control));
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            error = exception.Message;
            return false;
        }
    }

    public ValueTask InjectAsync(
        InputInjectionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.OriginMarker.Value == 0)
        {
            throw new ArgumentException(
                "A non-zero injection marker is required.",
                nameof(request));
        }

        var input = KeyboardSendInputEncoder.Encode(request);
        var result = _native.Send(input);
        if (result.InsertedCount != 1)
        {
            var error = result.ErrorCode == 0 ? 31 : result.ErrorCode; // ERROR_GEN_FAILURE
            throw new Win32Exception(error, "SendInput did not insert the keyboard event.");
        }

        return ValueTask.CompletedTask;
    }
}

internal static class KeyboardSendInputEncoder
{
    private const ushort VirtualKeyPause = 0x13;

    internal const uint InputKeyboard = 1;
    internal const uint KeyEventExtendedKey = 0x0001;
    internal const uint KeyEventKeyUp = 0x0002;
    internal const uint KeyEventScanCode = 0x0008;

    public static NativeInput Encode(InputInjectionRequest request)
    {
        var control = request.Target switch
        {
            ControlInjectionTarget { Control: not null } target => target.Control,
            CapturedInputInjectionTarget { Source.Control: not null } target =>
                target.Source.Control,
            _ => throw new NotSupportedException(
                "The input target has no keyboard control that SendInput can emit.")
        };

        var flags = request.Phase switch
        {
            InputInjectionPhase.Down => 0u,
            InputInjectionPhase.Up => KeyEventKeyUp,
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                "The injection phase is unsupported.")
        };

        ushort virtualKey;
        ushort scanCode;
        switch (control.Kind)
        {
            case InputControlKind.KeyboardScanCode when IsKnownE1Pause(control):
                // KEYEVENTF_EXTENDEDKEY means E0, not E1. SendInput has no generic E1 flag, so
                // translate the one E1 key represented by the current model to VK_PAUSE.
                virtualKey = VirtualKeyPause;
                scanCode = 0;
                break;

            case InputControlKind.KeyboardScanCode when IsE1Control(control):
                throw new NotSupportedException(
                    "SendInput cannot emit a general E1 scan code; use a supported virtual key.");

            case InputControlKind.KeyboardScanCode
                when control.Code == 0x45
                     && control.IsExtended
                     && !HasExplicitE0Prefix(control):
                throw new NotSupportedException(
                    "Extended scan code 0x45 is ambiguous without its E0/E1 prefix; use VK_PAUSE or preserve PREFIX=0004.");

            case InputControlKind.KeyboardScanCode when control.Code is >= 1 and <= byte.MaxValue:
                virtualKey = 0;
                scanCode = checked((ushort)control.Code);
                flags |= KeyEventScanCode;
                break;

            case InputControlKind.VirtualKey when control.Code is >= 1 and <= 0xFE:
                virtualKey = checked((ushort)control.Code);
                scanCode = 0;
                break;

            case InputControlKind.KeyboardScanCode:
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "Set-1 scan codes must be in the range 0x01 through 0xFF.");

            case InputControlKind.VirtualKey:
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "Virtual-key values must be in the range 0x01 through 0xFE.");

            default:
                throw new NotSupportedException(
                    $"SendInput cannot emit {control.Kind}; use a virtual-HID/driver backend.");
        }

        if (control.IsExtended && virtualKey != VirtualKeyPause)
        {
            // At this point the control is an E0 scan code or an explicitly extended virtual key.
            flags |= KeyEventExtendedKey;
        }

        return new NativeInput
        {
            Type = InputKeyboard,
            Union = new NativeInputUnion
            {
                Keyboard = new NativeKeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = scanCode,
                    Flags = flags,
                    Time = 0,
                    ExtraInformation = request.OriginMarker.Value
                }
            }
        };
    }

    public static void ValidateTarget(InputInjectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _ = Encode(new InputInjectionRequest(
            target,
            InputInjectionPhase.Down,
            new InputInjectionMarker(1)));
    }

    private static bool IsKnownE1Pause(InputControlId control) =>
        control.Code == 0x45 && HasRawKeyboardPrefix(control, "0004");

    private static bool IsE1Control(InputControlId control) =>
        HasRawKeyboardPrefix(control, "0004");

    private static bool HasExplicitE0Prefix(InputControlId control) =>
        HasRawKeyboardPrefix(control, "0002");

    private static bool HasRawKeyboardPrefix(InputControlId control, string prefix) =>
        control.RawQualifier?.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => token.Equals(
                $"PREFIX={prefix}",
                StringComparison.OrdinalIgnoreCase)) == true;
}

internal readonly record struct SendInputNativeResult(uint InsertedCount, int ErrorCode);

internal interface ISendInputNative
{
    SendInputNativeResult Send(NativeInput input);
}

internal sealed class Win32SendInputNative : ISendInputNative
{
    public static Win32SendInputNative Instance { get; } = new();

    private Win32SendInputNative()
    {
    }

    public SendInputNativeResult Send(NativeInput input)
    {
        var inputs = new[] { input };
        var inserted = SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<NativeInput>());
        return new SendInputNativeResult(inserted, Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] NativeInput[] inputs,
        int inputSize);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInput
{
    public uint Type;
    public NativeInputUnion Union;
}

[StructLayout(LayoutKind.Explicit)]
internal struct NativeInputUnion
{
    [FieldOffset(0)]
    public NativeMouseInput Mouse;

    [FieldOffset(0)]
    public NativeKeyboardInput Keyboard;

    [FieldOffset(0)]
    public NativeHardwareInput Hardware;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMouseInput
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public nuint ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeKeyboardInput
{
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeHardwareInput
{
    public uint Message;
    public ushort ParameterLow;
    public ushort ParameterHigh;
}
