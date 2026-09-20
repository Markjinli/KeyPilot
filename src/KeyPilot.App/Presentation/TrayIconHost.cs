using System.Runtime.InteropServices;

namespace KeyPilot.App.Presentation;

/// <summary>
/// Unpackaged WinUI has no tray control. This hosts a Win32 notify icon on a message-only window
/// so minimizing or closing the workbench can keep mapping hooks alive.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private const uint WmAppTray = 0x8000 + 0x51;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmCommand = 0x0111;
    private const uint WmDestroy = 0x0002;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;
    private const int IconId = 1;
    private const int CommandOpen = 1;
    private const int CommandExit = 2;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNonotify = 0x0080;
    private static readonly nint HwndMessage = -3;

    private readonly WndProc _wndProc;
    private nint _classAtom;
    private nint _hwnd;
    private nint _icon;
    private bool _added;
    private bool _disposed;
    private string _className = string.Empty;

    public event Action? OpenRequested;

    public event Action? ExitRequested;

    public TrayIconHost()
    {
        _wndProc = WindowProcedure;
    }

    public void Show(string iconPath, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureWindow();
        if (_icon == 0 && File.Exists(iconPath))
        {
            _icon = NativeMethods.LoadImage(
                0,
                iconPath,
                ImageIcon,
                0,
                0,
                LrLoadFromFile | LrDefaultSize);
        }

        var data = CreateData(tooltip);
        if (_added)
        {
            return;
        }

        if (!NativeMethods.Shell_NotifyIcon(NimAdd, ref data))
        {
            throw new InvalidOperationException("无法创建通知区域图标。");
        }

        _added = true;
    }

    public void Hide()
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(string.Empty);
        _ = NativeMethods.Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        if (_icon != 0)
        {
            _ = NativeMethods.DestroyIcon(_icon);
            _icon = 0;
        }

        if (_hwnd != 0)
        {
            _ = NativeMethods.DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (_classAtom != 0)
        {
            _ = NativeMethods.UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
            _classAtom = 0;
        }
    }

    private void EnsureWindow()
    {
        if (_hwnd != 0)
        {
            return;
        }

        _className = "KeyPilotTray." + Guid.NewGuid().ToString("N");
        var windowClass = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = _className
        };
        _classAtom = NativeMethods.RegisterClass(ref windowClass);
        if (_classAtom == 0)
        {
            throw new InvalidOperationException("无法注册托盘消息窗口。");
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            _className,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            0,
            windowClass.hInstance,
            0);
        if (_hwnd == 0)
        {
            throw new InvalidOperationException("无法创建托盘消息窗口。");
        }
    }

    private NotifyIconData CreateData(string tooltip)
    {
        return new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmAppTray,
            hIcon = _icon,
            szTip = string.IsNullOrWhiteSpace(tooltip) ? "KeyPilot" : tooltip
        };
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmAppTray)
        {
            var mouse = (uint)lParam & 0xFFFF;
            if (mouse is WmLButtonUp or WmLButtonDblClk)
            {
                OpenRequested?.Invoke();
                return 0;
            }

            if (mouse == WmRButtonUp)
            {
                ShowMenu();
                return 0;
            }
        }

        if (message == WmDestroy)
        {
            return 0;
        }

        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            _ = NativeMethods.AppendMenu(menu, 0, CommandOpen, "打开工作台");
            _ = NativeMethods.AppendMenu(menu, 0, CommandExit, "退出 KeyPilot");
            _ = NativeMethods.GetCursorPos(out var point);
            _ = NativeMethods.SetForegroundWindow(_hwnd);
            var command = NativeMethods.TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCmd | TpmNonotify,
                point.X,
                point.Y,
                0,
                _hwnd,
                0);
            if (command == CommandOpen)
            {
                OpenRequested?.Invoke();
            }
            else if (command == CommandExit)
            {
                ExitRequested?.Invoke();
            }
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassW")]
        public static extern nint RegisterClass(ref WndClass lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")]
        public static extern bool UnregisterClass(string lpClassName, nint hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
        public static extern nint CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            nint hWndParent,
            nint hMenu,
            nint hInstance,
            nint lpParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
        public static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
        public static extern nint LoadImage(
            nint hInst,
            string name,
            uint type,
            int cx,
            int cy,
            uint fuLoad);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(nint hIcon);

        [DllImport("user32.dll")]
        public static extern nint CreatePopupMenu();

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(nint hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
        public static extern bool AppendMenu(nint hMenu, uint uFlags, nint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        public static extern uint TrackPopupMenu(
            nint hMenu,
            uint uFlags,
            int x,
            int y,
            int nReserved,
            nint hWnd,
            nint prcRect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out Point lpPoint);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        public static extern nint GetModuleHandle(string? lpModuleName);
    }
}
