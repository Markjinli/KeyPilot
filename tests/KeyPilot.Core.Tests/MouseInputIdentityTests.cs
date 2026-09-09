using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Serialization;
using KeyPilot.Core.Triggers;

internal static class MouseInputIdentityTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
        new (string Name, Action Run)[]
        {
            ("鼠标 AnyOfKind 匹配精确设备上的同一虚拟键", AnyOfKindMatchesExactDevice),
            ("键盘虚拟键 4 不匹配鼠标中键", KeyboardVirtualKeyDoesNotMatchMouse),
            ("鼠标中键 JSON 往返保留 mouse + virtualKey", JsonRoundTripPreservesMouseVirtualKey),
            ("JSON 拒绝不存在的 mouseButton 控制类型", JsonRejectsMouseButtonControlKind)
        };

    private static void AnyOfKindMatchesExactDevice()
    {
        var configured = MouseSource(DeviceMatchMode.AnyOfKind, deviceId: null);
        var actual = MouseSource(DeviceMatchMode.ExactDevice, "\\\\?\\HID#MOUSE");
        Assert(MappingTriggerStateMachine.MatchesSource(configured, actual),
            "AnyOfKind 鼠标中键必须匹配任意一只物理鼠标上的同一虚拟键。");
    }

    private static void KeyboardVirtualKeyDoesNotMatchMouse()
    {
        var keyboard = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Keyboard,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId { Kind = InputControlKind.VirtualKey, Code = 4 }
        };
        var mouse = MouseSource(DeviceMatchMode.AnyOfKind, null);
        Assert(!MappingTriggerStateMachine.MatchesSource(keyboard, mouse),
            "键盘虚拟键 4 不得匹配鼠标中键。");
        Assert(!MappingTriggerStateMachine.MatchesSource(mouse, keyboard),
            "鼠标中键不得匹配键盘虚拟键 4。");
    }

    private static void JsonRoundTripPreservesMouseVirtualKey()
    {
        var configuration = KeyPilotConfiguration.CreateDefault();
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "鼠标中键 → 网址",
            SuppressOriginal = false,
            Source = MouseSource(DeviceMatchMode.AnyOfKind, null),
            Action = new OpenUriAction { Uri = "https://example.com/" }
        });

        var json = KeyPilotJson.Serialize(configuration);
        Assert(json.Contains("\"mouse\"", StringComparison.Ordinal), "JSON 必须写出 mouse 设备类型。");
        Assert(!json.Contains("mouseButton", StringComparison.OrdinalIgnoreCase), "不得写出 mouseButton 控制类型。");
        var copy = KeyPilotJson.Deserialize(json);
        var source = copy.Profiles.Single().Mappings.Single().Source;
        Assert(source.Device.Kind == InputDeviceKind.Mouse, "设备类型没有保留为 Mouse。");
        Assert(source.Control.Kind == InputControlKind.VirtualKey, "控制类型没有保留为 VirtualKey。");
        Assert(source.Control.Code == 4, "中键虚拟键编码没有保留。");
        Assert(!copy.Profiles.Single().Mappings.Single().SuppressOriginal, "鼠标映射必须保持不屏蔽原始输入。");
    }

    private static void JsonRejectsMouseButtonControlKind()
    {
        const string invalid = """
            {
              "schemaVersion": 6,
              "isMappingEnabled": false,
              "profiles": [
                {
                  "name": "x",
                  "mappings": [
                    {
                      "name": "bad",
                      "source": {
                        "device": { "kind": "mouse", "matchMode": "anyOfKind" },
                        "control": { "kind": "mouseButton", "code": 4 }
                      },
                      "action": { "$action": "openUri", "uri": "https://example.com/" }
                    }
                  ]
                }
              ],
              "specialKeySlots": []
            }
            """;
        try
        {
            _ = KeyPilotJson.Deserialize(invalid);
            throw new InvalidOperationException("mouseButton 控制类型不应被反序列化。");
        }
        catch (System.Text.Json.JsonException)
        {
        }
    }

    private static InputSource MouseSource(DeviceMatchMode matchMode, string? deviceId) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Mouse,
            MatchMode = matchMode,
            DeviceId = deviceId
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.VirtualKey,
            Code = 4
        }
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
