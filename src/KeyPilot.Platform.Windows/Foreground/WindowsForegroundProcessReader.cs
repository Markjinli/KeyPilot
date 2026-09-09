using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeyPilot.Platform.Windows.Foreground;

/// <summary>
/// Reads the executable name that owns the current Windows foreground window. Foreground-window
/// ownership is inherently racy, so a window or process disappearing is reported as an ordinary
/// unsuccessful read instead of escaping into the caller.
/// </summary>
public sealed class WindowsForegroundProcessReader
{
    private readonly IForegroundWindowNative _native;
    private readonly Func<int, string?> _processNameResolver;

    public WindowsForegroundProcessReader()
        : this(User32ForegroundWindowNative.Instance, ResolveProcessName)
    {
    }

    internal WindowsForegroundProcessReader(
        IForegroundWindowNative native,
        Func<int, string?> processNameResolver)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(processNameResolver);
        _native = native;
        _processNameResolver = processNameResolver;
    }

    /// <summary>
    /// Attempts to return <see cref="Process.ProcessName"/> for the current foreground window.
    /// The returned value is trimmed and normally does not contain the <c>.exe</c> suffix.
    /// </summary>
    public bool TryGetForegroundProcessName(out string processName)
    {
        processName = string.Empty;

        try
        {
            var window = _native.GetForegroundWindow();
            if (window == 0)
            {
                return false;
            }

            var threadId = _native.GetWindowThreadProcessId(window, out var processId);
            if (threadId == 0 || processId == 0 || processId > int.MaxValue)
            {
                return false;
            }

            var resolvedName = _processNameResolver((int)processId)?.Trim();
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                return false;
            }

            processName = resolvedName;
            return true;
        }
        catch
        {
            // The foreground HWND and its owner can disappear between every step. Process access
            // can also be denied. This observer must never make those expected races fatal.
            processName = string.Empty;
            return false;
        }
    }

    private static string ResolveProcessName(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.ProcessName;
    }
}

internal interface IForegroundWindowNative
{
    nint GetForegroundWindow();

    uint GetWindowThreadProcessId(nint window, out uint processId);
}

internal sealed class User32ForegroundWindowNative : IForegroundWindowNative
{
    internal static User32ForegroundWindowNative Instance { get; } = new();

    private User32ForegroundWindowNative()
    {
    }

    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public uint GetWindowThreadProcessId(nint window, out uint processId) =>
        NativeMethods.GetWindowThreadProcessId(window, out processId);

    private static class NativeMethods
    {
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}
