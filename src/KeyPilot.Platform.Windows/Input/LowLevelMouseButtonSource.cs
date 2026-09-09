using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Global, listen-only mouse hook for middle / back / forward. It never swallows the original click.
/// Raw Input mouse + RIDEV_INPUTSINK is unreliable on WinUI HWNDs; this is the mapping source.
/// </summary>
public sealed class LowLevelMouseButtonSource : IDisposable
{
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;
    private const uint LlmhfInjected = 0x00000001;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;
    private const uint PeekMessageNoRemove = 0;
    private const uint WmQuit = 0x0012;

    private readonly LowLevelMouseProc _hookCallback;
    private readonly ManualResetEventSlim _messageQueueReady = new(false);
    private readonly object _lifecycleGate = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private nint _hookHandle;
    private int _stopRequested;
    private int _disposed;

    public LowLevelMouseButtonSource()
    {
        _hookCallback = HookCallback;
    }

    public event EventHandler<RawMouseEvent>? ButtonReceived;

    public event EventHandler<Exception>? CaptureFaulted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_lifecycleGate)
        {
            if (_hookThread is { IsAlive: true })
            {
                return;
            }

            Volatile.Write(ref _stopRequested, 0);
            _messageQueueReady.Reset();
            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "KeyPilot mouse buttons"
            };
            _hookThread.Start();
        }
    }

    public void Stop()
    {
        Volatile.Write(ref _stopRequested, 1);
        Thread? thread;
        uint threadId;
        lock (_lifecycleGate)
        {
            thread = _hookThread;
            threadId = _hookThreadId;
        }

        if (threadId != 0)
        {
            _ = NativeMethods.PostThreadMessage(threadId, WmQuit, 0, 0);
        }

        if (thread is not null && thread != Thread.CurrentThread)
        {
            _ = thread.Join(TimeSpan.FromSeconds(2));
        }

        lock (_lifecycleGate)
        {
            if (_hookHandle != 0)
            {
                _ = NativeMethods.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = 0;
            }

            _hookThread = null;
            _hookThreadId = 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        _messageQueueReady.Dispose();
    }

    internal static bool TryTranslate(
        uint message,
        uint mouseData,
        uint flags,
        out ushort virtualKey,
        out bool isPressed)
    {
        virtualKey = 0;
        isPressed = false;
        if ((flags & LlmhfInjected) != 0)
        {
            return false;
        }

        switch (message)
        {
            case WmMButtonDown:
                virtualKey = RawInputMouseGuards.VkMButton;
                isPressed = true;
                return true;
            case WmMButtonUp:
                virtualKey = RawInputMouseGuards.VkMButton;
                isPressed = false;
                return true;
            case WmXButtonDown:
            case WmXButtonUp:
                var which = mouseData >> 16;
                virtualKey = which == XButton1
                    ? RawInputMouseGuards.VkXButton1
                    : which == XButton2
                        ? RawInputMouseGuards.VkXButton2
                        : (ushort)0;
                if (virtualKey == 0)
                {
                    return false;
                }

                isPressed = message == WmXButtonDown;
                return true;
            default:
                return false;
        }
    }

    private void HookThreadMain()
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(out _, 0, 0, 0, PeekMessageNoRemove);
            _messageQueueReady.Set();
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                return;
            }

            var module = NativeMethods.GetModuleHandle(null);
            var hook = NativeMethods.SetWindowsHookEx(WhMouseLl, _hookCallback, module, 0);
            if (hook == 0)
            {
                var error = Marshal.GetLastWin32Error();
                CaptureFaulted?.Invoke(this, new Win32Exception(error, "无法安装鼠标中键/前进/后退钩子。"));
                return;
            }

            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    NativeMethods.UnhookWindowsHookEx(hook);
                    return;
                }

                _hookHandle = hook;
            }

            while (Volatile.Read(ref _stopRequested) == 0)
            {
                var result = NativeMethods.GetMessage(out var message, 0, 0, 0);
                if (result <= 0)
                {
                    break;
                }

                NativeMethods.TranslateMessage(in message);
                NativeMethods.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            CaptureFaulted?.Invoke(this, exception);
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (_hookHandle != 0)
                {
                    NativeMethods.UnhookWindowsHookEx(_hookHandle);
                    _hookHandle = 0;
                }
            }
        }
    }

    private nint HookCallback(int code, nuint wParam, nint lParam)
    {
        if (code == HcAction && lParam != 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                if (TryTranslate((uint)wParam, data.MouseData, data.Flags, out var virtualKey, out var isPressed))
                {
                    ButtonReceived?.Invoke(
                        this,
                        new RawMouseEvent(
                            0,
                            string.Empty,
                            0,
                            virtualKey,
                            isPressed,
                            unchecked((uint)data.ExtraInfo),
                            DateTimeOffset.UtcNow));
                }
            }
            catch (Exception exception)
            {
                CaptureFaulted?.Invoke(this, exception);
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private delegate nint LowLevelMouseProc(int code, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public int PointX;
        public int PointY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWindowsHookEx(
            int hookId,
            LowLevelMouseProc callback,
            nint module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(nint hook);

        [DllImport("user32.dll")]
        internal static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(in Message message);

        [DllImport("user32.dll")]
        internal static extern nint DispatchMessage(in Message message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(
            out Message message,
            nint window,
            uint minimum,
            uint maximum,
            uint remove);

        [DllImport("kernel32.dll")]
        internal static extern nint GetModuleHandle(string? module);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}
