namespace KeyPilot.Platform.Windows.Audio;

public enum ConnectedMicrophoneKind
{
    DualSense,
    DualShockHeadset,
    Rc003
}

/// <summary>
/// A microphone that belongs to an already-connected KeyPilot device. Laptop arrays, webcams and
/// CABLE Output itself are not listed — CABLE is only the Windows endpoint behind the Xiaomi remote.
/// </summary>
public sealed record ConnectedMicrophone(
    string Id,
    ConnectedMicrophoneKind Kind,
    string DeviceName,
    string DisplayName,
    string? CaptureEndpointId,
    bool IsWindowsDefault,
    bool RequiresVoiceBridge,
    string Status);

public static class ConnectedMicrophoneCatalog
{
    public const string Rc003Id = "rc003";
    public const string DualSenseId = "dualsense";
    public const string DualShockId = "dualshock-headset";

    public static IReadOnlyList<ConnectedMicrophone> List(
        IReadOnlyList<WindowsCaptureEndpoint> endpoints,
        bool rc003Connected,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        aliases ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ConnectedMicrophone>();

        AddRc003(result, endpoints, rc003Connected, aliases);
        AddSonyPads(result, endpoints, aliases);
        return result;
    }

    private static void AddRc003(
        List<ConnectedMicrophone> result,
        IReadOnlyList<WindowsCaptureEndpoint> endpoints,
        bool rc003Connected,
        IReadOnlyDictionary<string, string> aliases)
    {
        var native = endpoints.FirstOrDefault(endpoint => endpoint.Kind == WindowsCaptureKind.XiaomiRemote);
        if (native is null && !rc003Connected)
        {
            return;
        }

        var cable = WindowsAudioCaptureCatalog.FindCableOutput(endpoints);
        var endpoint = native ?? cable;
        var requiresBridge = native is null;
        var status = native is not null
            ? "已连接 · 真实录音设备"
            : endpoint is not null
                ? "已连接 · 真实麦克风"
                : "已连接 · 真实麦克风，尚未出现在 Windows 录音设备中";
        result.Add(Create(
            Rc003Id,
            ConnectedMicrophoneKind.Rc003,
            "小米遥控器 2 Pro",
            endpoint,
            requiresBridge,
            status,
            aliases));
    }

    private static void AddSonyPads(
        List<ConnectedMicrophone> result,
        IReadOnlyList<WindowsCaptureEndpoint> endpoints,
        IReadOnlyDictionary<string, string> aliases)
    {
        AddSonyGroup(
            result,
            endpoints.Where(endpoint => endpoint.Kind == WindowsCaptureKind.DualSense).ToArray(),
            DualSenseId,
            ConnectedMicrophoneKind.DualSense,
            "DualSense 手柄麦克风",
            aliases);
        AddSonyGroup(
            result,
            endpoints.Where(endpoint => endpoint.Kind == WindowsCaptureKind.DualShockHeadset).ToArray(),
            DualShockId,
            ConnectedMicrophoneKind.DualShockHeadset,
            "DualShock 手柄耳机麦",
            aliases);
    }

    private static void AddSonyGroup(
        List<ConnectedMicrophone> result,
        IReadOnlyList<WindowsCaptureEndpoint> group,
        string idPrefix,
        ConnectedMicrophoneKind kind,
        string deviceName,
        IReadOnlyDictionary<string, string> aliases)
    {
        for (var index = 0; index < group.Count; index++)
        {
            var id = group.Count == 1 ? idPrefix : $"{idPrefix}:{index}";
            result.Add(Create(
                id,
                kind,
                deviceName,
                group[index],
                requiresVoiceBridge: false,
                "已连接 · 真实录音设备",
                aliases));
        }
    }

    private static ConnectedMicrophone Create(
        string id,
        ConnectedMicrophoneKind kind,
        string deviceName,
        WindowsCaptureEndpoint? endpoint,
        bool requiresVoiceBridge,
        string status,
        IReadOnlyDictionary<string, string> aliases) =>
        new(
            id,
            kind,
            deviceName,
            ApplyAlias(id, deviceName, aliases),
            endpoint?.Id,
            endpoint is { IsDefaultMultimedia: true } or { IsDefaultCommunications: true },
            requiresVoiceBridge,
            status);

    private static string ApplyAlias(
        string id,
        string deviceName,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (aliases.TryGetValue(id, out var alias) && !string.IsNullOrWhiteSpace(alias))
        {
            return alias.Trim();
        }

        return deviceName;
    }
}
