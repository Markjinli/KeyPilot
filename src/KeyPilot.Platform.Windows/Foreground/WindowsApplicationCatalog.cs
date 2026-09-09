using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using KeyPilot.Core.Configuration;

namespace KeyPilot.Platform.Windows.Foreground;

/// <summary>
/// Builds picker-friendly application identities from visible top-level windows. Foreground and
/// process lookups are inherently racy, so a disappearing window is an empty result rather than
/// an exception.
/// </summary>
public sealed class WindowsApplicationCatalog
{
    private const string ApplicationFrameHostName = ApplicationIdentity.ApplicationFrameHostProcessName;

    private readonly IForegroundWindowNative _foregroundNative;
    private readonly IWindowEnumerationNative _windows;
    private readonly IProcessIdentityNative _processes;

    public WindowsApplicationCatalog()
        : this(
            User32ForegroundWindowNative.Instance,
            User32WindowEnumerationNative.Instance,
            Win32ProcessIdentityNative.Instance)
    {
    }

    internal WindowsApplicationCatalog(
        IForegroundWindowNative foregroundNative,
        IWindowEnumerationNative windows,
        IProcessIdentityNative processes)
    {
        ArgumentNullException.ThrowIfNull(foregroundNative);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(processes);
        _foregroundNative = foregroundNative;
        _windows = windows;
        _processes = processes;
    }

    public bool TryGetForegroundIdentity(out ApplicationIdentity identity)
    {
        identity = new ApplicationIdentity();
        try
        {
            var window = _foregroundNative.GetForegroundWindow();
            if (window == 0)
            {
                return false;
            }

            var threadId = _foregroundNative.GetWindowThreadProcessId(window, out var processId);
            if (threadId == 0 || processId == 0 || processId > int.MaxValue)
            {
                return false;
            }

            return TryCreateIdentity((int)processId, window, out identity);
        }
        catch
        {
            identity = new ApplicationIdentity();
            return false;
        }
    }

    public IReadOnlyList<ApplicationIdentity> ListRunningWindowedApplications()
    {
        var identities = new List<ApplicationIdentity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            _windows.EnumerateTopLevelWindows(window =>
            {
                try
                {
                    if (!_windows.IsWindowVisible(window) ||
                        _windows.GetAncestorRoot(window) != window ||
                        _windows.IsToolWindow(window))
                    {
                        return true;
                    }

                    var threadId = _windows.GetWindowThreadProcessId(window, out var processId);
                    if (threadId == 0 || processId == 0 || processId > int.MaxValue)
                    {
                        return true;
                    }

                    if (!TryCreateIdentity((int)processId, window, out var identity) ||
                        !identity.HasMatchKey)
                    {
                        return true;
                    }

                    var key = identity.NormalizedPackageFamilyName.Length > 0
                        ? $"pfn:{identity.NormalizedPackageFamilyName}"
                        : $"exe:{identity.NormalizedProcessName}";
                    if (seen.Add(key))
                    {
                        identities.Add(identity);
                    }
                }
                catch
                {
                    // A window can disappear between EnumWindows and the identity lookup.
                }

                return true;
            });
        }
        catch
        {
            return identities;
        }

        identities.Sort((left, right) =>
            string.Compare(
                left.EffectiveDisplayName,
                right.EffectiveDisplayName,
                StringComparison.CurrentCultureIgnoreCase));
        return identities;
    }

    private bool TryCreateIdentity(int processId, nint window, out ApplicationIdentity identity)
    {
        identity = new ApplicationIdentity();
        var processName = _processes.GetProcessName(processId)?.Trim();
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalized = ApplicationProfileResolver.NormalizeProcessName(processName);
        var packageFamily = _processes.GetPackageFamilyName(processId)?.Trim() ?? string.Empty;
        if (string.Equals(normalized, ApplicationFrameHostName, StringComparison.OrdinalIgnoreCase) &&
            packageFamily.Length == 0)
        {
            return false;
        }

        var executablePath = _processes.GetExecutablePath(processId);
        var description = string.IsNullOrWhiteSpace(executablePath)
            ? null
            : _processes.GetFileDescription(executablePath);
        var title = _windows.GetWindowTitle(window);
        var displayName = FirstNonEmpty(description, title, normalized);

        identity = new ApplicationIdentity
        {
            ProcessName = normalized,
            DisplayName = displayName,
            PackageFamilyName = packageFamily
        };
        return identity.HasMatchKey;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}

internal interface IWindowEnumerationNative
{
    void EnumerateTopLevelWindows(Func<nint, bool> visitor);

    bool IsWindowVisible(nint window);

    nint GetAncestorRoot(nint window);

    bool IsToolWindow(nint window);

    uint GetWindowThreadProcessId(nint window, out uint processId);

    string GetWindowTitle(nint window);
}

internal interface IProcessIdentityNative
{
    string? GetProcessName(int processId);

    string? GetExecutablePath(int processId);

    string? GetPackageFamilyName(int processId);

    string? GetFileDescription(string executablePath);
}

internal sealed class User32WindowEnumerationNative : IWindowEnumerationNative
{
    internal const uint GaRoot = 2;
    internal const int GwlExStyle = -20;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WindowTextLimit = 512;

    internal static User32WindowEnumerationNative Instance { get; } = new();

    private User32WindowEnumerationNative()
    {
    }

    public void EnumerateTopLevelWindows(Func<nint, bool> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        _ = NativeMethods.EnumWindows(
            (window, _) => visitor(window),
            0);
    }

    public bool IsWindowVisible(nint window) => NativeMethods.IsWindowVisible(window);

    public nint GetAncestorRoot(nint window) => NativeMethods.GetAncestor(window, GaRoot);

    public bool IsToolWindow(nint window)
    {
        var style = NativeMethods.GetWindowLongPtr(window, GwlExStyle);
        return (style.ToInt64() & WsExToolWindow) != 0;
    }

    public uint GetWindowThreadProcessId(nint window, out uint processId) =>
        NativeMethods.GetWindowThreadProcessId(window, out processId);

    public string GetWindowTitle(nint window)
    {
        var buffer = new StringBuilder(WindowTextLimit);
        var length = NativeMethods.GetWindowText(window, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString();
    }

    private static class NativeMethods
    {
        public delegate bool EnumWindowsProc(nint window, nint lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(nint window);

        [DllImport("user32.dll")]
        public static extern nint GetAncestor(nint window, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern nint GetWindowLongPtr(nint window, int index);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        public static extern int GetWindowText(nint window, StringBuilder text, int maxCount);
    }
}

internal sealed class Win32ProcessIdentityNative : IProcessIdentityNative
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    internal static Win32ProcessIdentityNative Instance { get; } = new();

    private Win32ProcessIdentityNative()
    {
    }

    public string? GetProcessName(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.ProcessName;
    }

    public string? GetExecutablePath(int processId)
    {
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var capacity = 260;
            var buffer = new StringBuilder(capacity);
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref capacity))
            {
                return null;
            }

            return buffer.ToString();
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public string? GetPackageFamilyName(int processId)
    {
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            uint length = 0;
            var first = NativeMethods.GetApplicationUserModelId(handle, ref length, null);
            if (first is not ErrorInsufficientBuffer and not ErrorSuccess || length == 0)
            {
                return null;
            }

            var buffer = new StringBuilder((int)length);
            if (NativeMethods.GetApplicationUserModelId(handle, ref length, buffer) != ErrorSuccess)
            {
                return null;
            }

            var aumid = buffer.ToString();
            var separator = aumid.IndexOf('!');
            return separator > 0 ? aumid[..separator] : aumid;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public string? GetFileDescription(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var description = FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(nint handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(
            nint process,
            int flags,
            StringBuilder name,
            ref int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetApplicationUserModelId(
            nint process,
            ref uint length,
            StringBuilder? applicationUserModelId);
    }
}
