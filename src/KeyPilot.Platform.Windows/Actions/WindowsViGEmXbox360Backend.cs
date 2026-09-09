using System.Runtime.InteropServices;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>
/// Virtual Xbox 360 output through ViGEmBus. The bus is probed without loading GPL code; the MIT
/// Nefarius client is optional and loaded by name so a missing package still compiles.
/// </summary>
public sealed class WindowsViGEmXbox360Backend : IGamepadInjectionBackend, IDisposable
{
    private const string MissingBus =
        "未安装 ViGEmBus。安装后再使用「手柄按键」输出；采集与键盘映射不受影响。";
    private const string MissingClient =
        "已检测到 ViGEmBus，但当前构建未链接 ViGEm 客户端。";

    private readonly object _gate = new();
    private IXbox360Session? _session;
    private bool _disposed;

    public bool IsAvailable => ProbeBus();

    public string UnavailableReason => ProbeBus() ? string.Empty : MissingBus;

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _session is { IsConnected: true };
            }
        }
    }

    public static bool ProbeBus()
    {
        var handle = CreateFile(
            @"\\.\ViGEmBus",
            0,
            FileShare.ReadWrite,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return error is 5 or 32; // present but access/sharing denied still means the bus exists
        }

        CloseHandle(handle);
        return true;
    }

    public void Validate(InputControlId control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!ProbeBus())
        {
            throw new InvalidOperationException(MissingBus);
        }

        if (control.Kind is not InputControlKind.GamepadButton
            and not InputControlKind.GamepadAxisDirection)
        {
            throw new NotSupportedException($"Gamepad output cannot emit {control.Kind}.");
        }

        if (control.Kind == InputControlKind.GamepadButton &&
            control.Code is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(control), "Gamepad button code is out of range.");
        }

        if (control.Kind == InputControlKind.GamepadAxisDirection &&
            control.Code is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(control), "Gamepad axis code is out of range.");
        }
    }

    public ValueTask InjectAsync(InputInjectionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Target is not ControlInjectionTarget { Control: { } control })
        {
            throw new NotSupportedException("Gamepad output only accepts logical control targets.");
        }

        Validate(control);
        var session = EnsureSession();
        var pressed = request.Phase == InputInjectionPhase.Down;
        if (control.Kind == InputControlKind.GamepadButton)
        {
            session.SetButton((ushort)control.Code, pressed);
        }
        else
        {
            session.SetAxis((XInputVirtualControl)control.Code, pressed);
        }

        session.Submit();
        return ValueTask.CompletedTask;
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disconnect();
    }

    private IXbox360Session EnsureSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { IsConnected: true })
            {
                return _session;
            }

            _session?.Dispose();
            _session = NefariusXbox360Session.TryCreate(out var error);
            if (_session is null)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? MissingClient : error);
            }

            return _session;
        }
    }

    private interface IXbox360Session : IDisposable
    {
        bool IsConnected { get; }

        void SetButton(ushort code, bool pressed);

        void SetAxis(XInputVirtualControl control, bool pressed);

        void Submit();
    }

    private sealed class NefariusXbox360Session : IXbox360Session
    {
        private readonly IDisposable _client;
        private readonly object _controller;
        private readonly Action<ushort, bool> _setButton;
        private readonly Action<XInputVirtualControl, bool> _setAxis;
        private readonly Action _submit;
        private readonly Action _disconnect;

        private NefariusXbox360Session(
            IDisposable client,
            object controller,
            Action<ushort, bool> setButton,
            Action<XInputVirtualControl, bool> setAxis,
            Action submit,
            Action disconnect)
        {
            _client = client;
            _controller = controller;
            _setButton = setButton;
            _setAxis = setAxis;
            _submit = submit;
            _disconnect = disconnect;
            IsConnected = true;
        }

        public bool IsConnected { get; private set; }

        public static IXbox360Session? TryCreate(out string error)
        {
            error = MissingClient;
            var clientType = Type.GetType(
                "Nefarius.ViGEm.Client.ViGEmClient, Nefarius.ViGEm.Client",
                throwOnError: false);
            if (clientType is null)
            {
                return null;
            }

            try
            {
                var client = (IDisposable)Activator.CreateInstance(clientType)!;
                var create = clientType.GetMethod("CreateXbox360Controller", Type.EmptyTypes);
                if (create?.Invoke(client, null) is not { } controller)
                {
                    client.Dispose();
                    return null;
                }

                var connect = controller.GetType().GetMethod("Connect", Type.EmptyTypes);
                connect?.Invoke(controller, null);
                var submit = controller.GetType().GetMethod("SubmitReport", Type.EmptyTypes);
                var disconnect = controller.GetType().GetMethod("Disconnect", Type.EmptyTypes);
                var setButtonState = controller.GetType().GetMethods()
                    .FirstOrDefault(method => method.Name == "SetButtonState" && method.GetParameters().Length == 2);
                var setAxisValue = controller.GetType().GetMethods()
                    .FirstOrDefault(method => method.Name == "SetAxisValue" && method.GetParameters().Length == 2);
                var setSlider = controller.GetType().GetMethods()
                    .FirstOrDefault(method => method.Name == "SetSliderValue" && method.GetParameters().Length == 2);

                if (setButtonState is null || submit is null)
                {
                    (controller as IDisposable)?.Dispose();
                    client.Dispose();
                    return null;
                }

                var buttonType = setButtonState.GetParameters()[0].ParameterType;
                return new NefariusXbox360Session(
                    client,
                    controller,
                    (code, pressed) =>
                    {
                        var button = MapButton(buttonType, code);
                        setButtonState.Invoke(controller, [button, pressed]);
                    },
                    (axis, pressed) => ApplyAxis(controller, setAxisValue, setSlider, axis, pressed),
                    () => submit.Invoke(controller, null),
                    () => disconnect?.Invoke(controller, null));
            }
            catch (Exception exception)
            {
                error = exception.GetBaseException().Message;
                return null;
            }
        }

        public void SetButton(ushort code, bool pressed) => _setButton(code, pressed);

        public void SetAxis(XInputVirtualControl control, bool pressed) => _setAxis(control, pressed);

        public void Submit() => _submit();

        public void Dispose()
        {
            if (!IsConnected)
            {
                return;
            }

            IsConnected = false;
            try
            {
                _disconnect();
            }
            catch (Exception)
            {
            }

            (_controller as IDisposable)?.Dispose();
            _client.Dispose();
        }

        private static object MapButton(Type buttonType, ushort code)
        {
            var name = code switch
            {
                0x0001 => "Up",
                0x0002 => "Down",
                0x0004 => "Left",
                0x0008 => "Right",
                0x0010 => "Start",
                0x0020 => "Back",
                0x0040 => "LeftThumb",
                0x0080 => "RightThumb",
                0x0100 => "LeftShoulder",
                0x0200 => "RightShoulder",
                0x0400 => "Guide",
                0x1000 => "A",
                0x2000 => "B",
                0x4000 => "X",
                0x8000 => "Y",
                _ => throw new NotSupportedException($"No Xbox 360 button for code 0x{code:X4}.")
            };

            return Enum.Parse(buttonType, name, ignoreCase: false);
        }

        private static void ApplyAxis(
            object controller,
            System.Reflection.MethodInfo? setAxis,
            System.Reflection.MethodInfo? setSlider,
            XInputVirtualControl control,
            bool pressed)
        {
            short stick = pressed ? short.MaxValue : (short)0;
            short negative = pressed ? short.MinValue : (short)0;
            byte trigger = pressed ? byte.MaxValue : (byte)0;
            switch (control)
            {
                case XInputVirtualControl.LeftTrigger:
                    InvokeNamed(setSlider, controller, "LeftTrigger", trigger);
                    break;
                case XInputVirtualControl.RightTrigger:
                    InvokeNamed(setSlider, controller, "RightTrigger", trigger);
                    break;
                case XInputVirtualControl.LeftStickLeft:
                    InvokeNamed(setAxis, controller, "LeftThumbX", negative);
                    break;
                case XInputVirtualControl.LeftStickRight:
                    InvokeNamed(setAxis, controller, "LeftThumbX", stick);
                    break;
                case XInputVirtualControl.LeftStickUp:
                    InvokeNamed(setAxis, controller, "LeftThumbY", stick);
                    break;
                case XInputVirtualControl.LeftStickDown:
                    InvokeNamed(setAxis, controller, "LeftThumbY", negative);
                    break;
                case XInputVirtualControl.RightStickLeft:
                    InvokeNamed(setAxis, controller, "RightThumbX", negative);
                    break;
                case XInputVirtualControl.RightStickRight:
                    InvokeNamed(setAxis, controller, "RightThumbX", stick);
                    break;
                case XInputVirtualControl.RightStickUp:
                    InvokeNamed(setAxis, controller, "RightThumbY", stick);
                    break;
                case XInputVirtualControl.RightStickDown:
                    InvokeNamed(setAxis, controller, "RightThumbY", negative);
                    break;
                default:
                    throw new NotSupportedException($"No Xbox 360 axis for {control}.");
            }
        }

        private static void InvokeNamed(
            System.Reflection.MethodInfo? method,
            object controller,
            string enumName,
            object value)
        {
            if (method is null)
            {
                throw new InvalidOperationException(MissingClient);
            }

            var enumType = method.GetParameters()[0].ParameterType;
            method.Invoke(controller, [Enum.Parse(enumType, enumName, ignoreCase: false), value]);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
