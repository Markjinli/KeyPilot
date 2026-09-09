using System.Runtime.InteropServices;

namespace KeyPilot.Platform.Windows.Foreground;

/// <summary>
/// Signals foreground-window changes through the Win32 accessibility event stream. A caller can
/// keep a slower polling reader as a fallback when the hook is unavailable.
/// </summary>
public sealed class WindowsForegroundWindowWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly IForegroundWindowEventNative _native;
    private ForegroundWindowEventCallback? _callback;
    private nint _hook;
    private bool _disposed;

    public WindowsForegroundWindowWatcher()
        : this(User32ForegroundWindowEventNative.Instance)
    {
    }

    internal WindowsForegroundWindowWatcher(IForegroundWindowEventNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public event EventHandler? ForegroundChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _hook != 0;
            }
        }
    }

    public bool Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hook != 0)
            {
                return true;
            }

            _callback = OnForegroundChanged;
            _hook = _native.InstallForegroundHook(_callback);
            if (_hook == 0)
            {
                _callback = null;
                return false;
            }

            return true;
        }
    }

    public void Dispose()
    {
        nint hook;
        ForegroundWindowEventCallback? callback;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hook = _hook;
            callback = _callback;
            _hook = 0;
        }

        try
        {
            if (hook != 0)
            {
                _native.UninstallForegroundHook(hook);
            }
        }
        finally
        {
            // SetWinEventHook retains only the unmanaged function pointer. Keep the managed
            // delegate rooted until UnhookWinEvent has returned and any in-flight callback is
            // past the native boundary.
            GC.KeepAlive(callback);
            lock (_gate)
            {
                _callback = null;
            }
        }
    }

    private void OnForegroundChanged(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (eventType != User32ForegroundWindowEventNative.EventSystemForeground || window == 0)
        {
            return;
        }

        EventHandler? handlers;
        lock (_gate)
        {
            if (_disposed || _hook == 0)
            {
                return;
            }

            handlers = ForegroundChanged;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // A UI subscriber must not unwind through the native accessibility callback.
            }
        }
    }
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate void ForegroundWindowEventCallback(
    nint hook,
    uint eventType,
    nint window,
    int objectId,
    int childId,
    uint eventThread,
    uint eventTime);

internal interface IForegroundWindowEventNative
{
    nint InstallForegroundHook(ForegroundWindowEventCallback callback);

    bool UninstallForegroundHook(nint hook);
}

internal sealed class User32ForegroundWindowEventNative : IForegroundWindowEventNative
{
    internal const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;

    internal static User32ForegroundWindowEventNative Instance { get; } = new();

    private User32ForegroundWindowEventNative()
    {
    }

    public nint InstallForegroundHook(ForegroundWindowEventCallback callback) =>
        NativeMethods.SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            0,
            callback,
            0,
            0,
            WinEventOutOfContext);

    public bool UninstallForegroundHook(nint hook) => NativeMethods.UnhookWinEvent(hook);

    private static class NativeMethods
    {
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint SetWinEventHook(
            uint eventMin,
            uint eventMax,
            nint eventHookModule,
            ForegroundWindowEventCallback callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(nint hook);
    }
}
