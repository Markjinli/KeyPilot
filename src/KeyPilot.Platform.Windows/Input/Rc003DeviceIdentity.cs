using System.Globalization;
using System.Text.RegularExpressions;
using KeyPilot.Core.Input;

namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Fail-closed identity for Xiaomi Bluetooth Remote 2 Pro (RC003). Matching is VID/PID only;
/// Bluetooth names and addresses are never persisted.
/// </summary>
public static class Rc003DeviceIdentity
{
    public const ushort VendorId = 0x2717;
    public const ushort ProductId = 0x32B8;
    public const ushort KeyboardUsagePage = 0x07;

    public static bool IsAdvertisedName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Trim().ToLowerInvariant();
        return normalized is "mi rc"
            or "xiaomi bluetooth remote 2 pro"
            or "小米蓝牙语音遥控器"
            or "小米遥控器 2 pro"
            or "小米遥控器2 pro"
            or "小米遥控器2pro";
    }

    public const string Power = "power";
    public const string Mic = "mic";
    public const string Up = "up";
    public const string Left = "left";
    public const string Ok = "ok";
    public const string Right = "right";
    public const string Down = "down";
    public const string Back = "back";
    public const string VolumeUp = "volume_up";
    public const string Home = "home";
    public const string VolumeDown = "volume_down";
    public const string Menu = "menu";
    public const string Tv = "tv";

    public static readonly string[] ButtonOrder =
    [
        Power, Mic, Up, Left, Ok, Right, Down, Back, VolumeUp, Home, VolumeDown, Menu, Tv
    ];

    /// <summary>Physical button positions as fractions of the RC003 body (same geometry as the product).</summary>
    public static readonly Rc003Hotspot[] Hotspots =
    [
        new(Power, 0.386, 0.099, 0.15, 0.072),
        new(Mic, 0.630, 0.099, 0.15, 0.072),
        new(Up, 0.502, 0.179, 0.18, 0.065),
        new(Left, 0.362, 0.246, 0.15, 0.080),
        new(Ok, 0.502, 0.246, 0.19, 0.095),
        new(Right, 0.638, 0.246, 0.15, 0.080),
        new(Down, 0.502, 0.317, 0.18, 0.065),
        new(Back, 0.406, 0.389, 0.17, 0.080),
        new(VolumeUp, 0.604, 0.390, 0.16, 0.080),
        new(Home, 0.406, 0.479, 0.17, 0.080),
        new(VolumeDown, 0.604, 0.480, 0.16, 0.080),
        new(Menu, 0.406, 0.569, 0.17, 0.080),
        new(Tv, 0.604, 0.569, 0.16, 0.080)
    ];

    public readonly record struct Rc003Hotspot(string ButtonId, double X, double Y, double Width, double Height);

    private static readonly Dictionary<string, (string Label, ushort Usage, bool Degraded)> Buttons =
        new(StringComparer.Ordinal)
        {
            [Power] = ("电源", 0x0066, false),
            [Mic] = ("麦克风", 0x003E, false),
            [Up] = ("上", 0x0052, false),
            [Left] = ("左", 0x0050, false),
            [Ok] = ("确定", 0x0028, false),
            [Right] = ("右", 0x004F, false),
            [Down] = ("下", 0x0051, false),
            [Back] = ("返回", 0x00F1, true),
            [VolumeUp] = ("音量 +", 0x0080, true),
            [Home] = ("主页", 0x004A, false),
            [VolumeDown] = ("音量 −", 0x0081, true),
            [Menu] = ("菜单", 0x0065, false),
            [Tv] = ("TV", 0x0035, false)
        };

    private static readonly Dictionary<ushort, string> UsageToButton = Buttons
        .ToDictionary(pair => pair.Value.Usage, pair => pair.Key);

    private static readonly Dictionary<ushort, string> VirtualKeyToButton = new()
    {
        [0x1B] = Power,      // VK_ESCAPE — power often arrives as Esc on Windows
        [0x74] = Mic,        // VK_F5
        [0x26] = Up,         // VK_UP
        [0x25] = Left,       // VK_LEFT
        [0x0D] = Ok,         // VK_RETURN
        [0x27] = Right,      // VK_RIGHT
        [0x28] = Down,       // VK_DOWN
        [0x08] = Back,       // VK_BACK — only if the host translates 0xF1
        [0xAF] = VolumeUp,   // VK_VOLUME_UP
        [0x24] = Home,       // VK_HOME
        [0xAE] = VolumeDown, // VK_VOLUME_DOWN
        [0x5D] = Menu,       // VK_APPS
        [0xC0] = Tv          // VK_OEM_3 (`~) — HID usage 0x35
    };

    private static readonly Regex VidPidPattern = new(
        @"VID[_&]?([0-9A-Fa-f]{1,8}).{0,12}PID[_&]?([0-9A-Fa-f]{1,8})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryParseHex(string value, out ushort parsed)
    {
        if (value.Length > 4)
        {
            value = value[^4..];
        }

        return ushort.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
    }

    public static bool TryGetButton(string buttonId, out string label, out ushort usage, out bool degraded)
    {
        if (Buttons.TryGetValue(buttonId, out var button))
        {
            label = button.Label;
            usage = button.Usage;
            degraded = button.Degraded;
            return true;
        }

        label = string.Empty;
        usage = 0;
        degraded = false;
        return false;
    }

    public static bool IsRc003(uint vendorId, uint productId) =>
        vendorId == VendorId && productId == ProductId;

    public static bool IsRc003(string? devicePath) =>
        TryParseVidPid(devicePath, out var vendorId, out var productId) &&
        IsRc003(vendorId, productId);

    public static bool TryParseVidPid(string? devicePath, out ushort vendorId, out ushort productId)
    {
        vendorId = 0;
        productId = 0;
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        var match = VidPidPattern.Match(devicePath);
        return match.Success &&
               TryParseHex(match.Groups[1].Value, out vendorId) &&
               TryParseHex(match.Groups[2].Value, out productId);
    }

    public static bool TryMapKeyboardEvent(RawKeyboardEvent input, out string buttonId)
    {
        buttonId = string.Empty;
        if (!IsRc003(input.DevicePath))
        {
            return false;
        }

        return VirtualKeyToButton.TryGetValue(input.VirtualKey, out buttonId!) &&
               !string.IsNullOrEmpty(buttonId);
    }

    public static InputSource CreateSource(string buttonId)
    {
        if (!TryGetButton(buttonId, out _, out var usage, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(buttonId), buttonId, "Unknown RC003 button.");
        }

        return new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Hid,
                MatchMode = DeviceMatchMode.ExactDevice,
                VendorId = VendorId,
                ProductId = ProductId
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.HidUsage,
                Code = usage,
                UsagePage = KeyboardUsagePage,
                Usage = usage
            }
        };
    }

    public static InputSource CreateLiveSource(string buttonId, string devicePath)
    {
        var source = CreateSource(buttonId);
        return source with
        {
            Device = source.Device with { DeviceId = devicePath }
        };
    }
}
