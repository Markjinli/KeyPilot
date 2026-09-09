using System.Globalization;
using System.Text.RegularExpressions;

namespace KeyPilot.Platform.Windows.Audio;

public enum WindowsCaptureKind
{
    Other,
    DualSense,
    DualShockHeadset,
    CableOutput,
    XiaomiRemote
}

public sealed record WindowsCaptureEndpoint(
    string Id,
    string Name,
    string? InstanceId,
    WindowsCaptureKind Kind,
    bool IsDefaultMultimedia,
    bool IsDefaultCommunications);

/// <summary>
/// Identifies DualSense / DualShock USB headset capture endpoints, Xiaomi RC003 capture endpoints
/// when Windows exposes them, and VB-CABLE Output without opening those devices. DualSense USB
/// audio is a standard capture endpoint. RC003 ATVV PCM only becomes a Windows microphone after
/// it is played into CABLE Input; CABLE Output is the endpoint, not the device identity.
/// </summary>
public static class WindowsCaptureEndpointClassifier
{
    private static readonly Regex SonyVidPid = new(
        @"VID[_&]?0*054C.{0,16}PID[_&]?0*([0-9A-Fa-f]{4})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex XiaomiVidPid = new(
        @"VID[_&]?0*2717.{0,16}PID[_&]?0*32B8",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static WindowsCaptureKind Classify(string? friendlyName, string? instanceId)
    {
        var name = friendlyName ?? string.Empty;
        if (IsCableOutput(name))
        {
            return WindowsCaptureKind.CableOutput;
        }

        if (IsXiaomiRemote(name, instanceId))
        {
            return WindowsCaptureKind.XiaomiRemote;
        }

        if (TrySonyProduct(instanceId, out var product) || TrySonyProduct(name, out product))
        {
            return product is SonyHidIdentityCodes.DualSense or SonyHidIdentityCodes.DualSenseEdge
                ? WindowsCaptureKind.DualSense
                : WindowsCaptureKind.DualShockHeadset;
        }

        if (name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("无线控制器", StringComparison.Ordinal) &&
            name.Contains("麦克风", StringComparison.Ordinal))
        {
            return WindowsCaptureKind.DualSense;
        }

        return WindowsCaptureKind.Other;
    }

    public static bool IsCableOutput(string name) =>
        name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("CABLE  Output", StringComparison.OrdinalIgnoreCase);

    public static bool IsCableInput(string name) =>
        name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase);

    public static string DisplayKind(WindowsCaptureKind kind) => kind switch
    {
        WindowsCaptureKind.DualSense => "PS5 手柄麦克风",
        WindowsCaptureKind.DualShockHeadset => "PS4 手柄耳机麦",
        WindowsCaptureKind.XiaomiRemote => "小米遥控器 2 Pro",
        WindowsCaptureKind.CableOutput => "语音桥接端点",
        _ => "其它麦克风"
    };

    public static bool IsXiaomiRemote(string? friendlyName, string? instanceId)
    {
        if (XiaomiVidPid.IsMatch(instanceId ?? string.Empty))
        {
            return true;
        }

        var name = (friendlyName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return false;
        }

        return name.Contains("小米蓝牙语音遥控器", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("小米遥控器 2", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("小米遥控器2", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Xiaomi Bluetooth Remote", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("mi rc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySonyProduct(string? text, out ushort productId)
    {
        productId = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = SonyVidPid.Match(text);
        if (!match.Success)
        {
            return false;
        }

        return ushort.TryParse(
                   match.Groups[1].Value,
                   NumberStyles.HexNumber,
                   CultureInfo.InvariantCulture,
                   out productId)
               && productId is SonyHidIdentityCodes.DualShock4Fat
                   or SonyHidIdentityCodes.DualShock4Slim
                   or SonyHidIdentityCodes.DualSense
                   or SonyHidIdentityCodes.DualSenseEdge;
    }
}

internal static class SonyHidIdentityCodes
{
    public const ushort DualShock4Fat = 0x05C4;
    public const ushort DualShock4Slim = 0x09CC;
    public const ushort DualSense = 0x0CE6;
    public const ushort DualSenseEdge = 0x0DF2;
}
