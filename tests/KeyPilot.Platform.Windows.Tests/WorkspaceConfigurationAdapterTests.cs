using KeyPilot.App.Presentation;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Serialization;
using KeyPilot.Platform.Windows.Actions;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class WorkspaceConfigurationAdapterTests
{
    public static Task RestoresSpecialSlotsAndKnownInputsOfflineAsync()
    {
        var nodes = CreateWorkspaceNodes("Space", "KeyA");
        var configuration = KeyPilotConfiguration.CreateDefault("掌机方案");
        var hidSource = HidSource();
        configuration.SpecialKeySlots[0] = configuration.SpecialKeySlots[0] with
        {
            DisplayName = "控制中心",
            Source = hidSource
        };
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "空格映射",
            Source = KeyboardSource("Space", exactDevice: true),
            Action = new OpenUriAction { Uri = "https://example.com/" }
        });
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "手柄映射",
            Source = GamepadSource(XInputButton.Y),
            Action = new EmitSpecialKeyAction { SlotNumber = 1 }
        });

        var restored = WorkspaceConfigurationAdapter.Restore(configuration, nodes);

        var special = nodes.Single(node => node.StableId == "special:1");
        Assert(special.FriendlyName == "控制中心", "特殊键名称没有恢复。");
        Assert(special.CapturedSource?.CanonicalKey == hidSource.CanonicalKey, "特殊键来源没有恢复。");
        Assert(nodes.Single(node => node.StableId == "keyboard:Space").Mapping is not null,
            "精确设备键盘映射应能离线投影到已知键位。");
        Assert(nodes.Single(node => node.StableId == "gamepad:Y").Mapping is not null,
            "手柄映射应能离线投影到已知按钮。");
        Assert(restored.DetachedMappings.Count == 0, "已知输入不应落入分离映射。");
        return Task.CompletedTask;
    }

    public static Task SixDrawerActionsRoundTripToCoreAsync()
    {
        var ids = new[] { "KeyA", "KeyB", "KeyC", "KeyD", "KeyE", "KeyF" };
        var nodes = CreateWorkspaceNodes(ids);
        var configuration = KeyPilotConfiguration.CreateDefault("六类动作");
        configuration.SpecialKeySlots[0] = configuration.SpecialKeySlots[0] with
        {
            DisplayName = "厂商键",
            Source = HidSource()
        };
        var actions = new MappingAction[]
        {
            new ShortcutAction
            {
                Keys =
                {
                    KeyboardControl("ControlRight"),
                    KeyboardControl("AltRight"),
                    KeyboardControl("Equal")
                }
            },
            new EmitSpecialKeyAction { SlotNumber = 1 },
            new LaunchProgramAction { FilePath = @"C:\Tools\Panel.exe" },
            new OpenUriAction { Uri = "https://example.com/" },
            new RunScriptAction { ScriptPath = @"C:\Scripts\mode.bat" },
            new SendKeyAction { Target = GamepadSource(XInputButton.Y).Control }
        };
        for (var index = 0; index < ids.Length; index++)
        {
            configuration.Profiles.Single().Mappings.Add(new InputMapping
            {
                Name = $"动作 {index + 1}",
                Source = KeyboardSource(ids[index]),
                Action = actions[index]
            });
        }

        var restored = WorkspaceConfigurationAdapter.Restore(configuration, nodes);
        var drafts = ids.Select(id => nodes.Single(node => node.Id == id).Mapping!).ToArray();
        var expectedTypes = new[]
        {
            WorkspaceConfigurationAdapter.ShortcutActionType,
            WorkspaceConfigurationAdapter.SpecialKeyActionType,
            WorkspaceConfigurationAdapter.LaunchActionType,
            WorkspaceConfigurationAdapter.UriActionType,
            WorkspaceConfigurationAdapter.ScriptActionType,
            WorkspaceConfigurationAdapter.GamepadActionType
        };
        Assert(drafts.Select(draft => draft.ActionType).SequenceEqual(expectedTypes),
            "六类动作没有按抽屉类型恢复。");
        Assert(drafts.All(draft => !draft.IsReadOnlyPassthrough), "标准动作不应成为只读透传。");

        var captured = WorkspaceConfigurationAdapter.Capture(configuration, restored, nodes);
        var capturedActions = captured.Profiles.Single().Mappings.Select(mapping => mapping.Action).ToArray();
        Assert(capturedActions[0] is ShortcutAction, "快捷键往返类型错误。");
        Assert(capturedActions[1] is EmitSpecialKeyAction, "特殊键往返类型错误。");
        Assert(capturedActions[2] is LaunchProgramAction, "应用动作往返类型错误。");
        Assert(capturedActions[3] is OpenUriAction, "网址动作往返类型错误。");
        Assert(capturedActions[4] is RunScriptAction, "脚本动作往返类型错误。");
        Assert(capturedActions[5] is SendKeyAction, "手柄动作往返类型错误。");
        _ = KeyPilotJson.Serialize(captured);
        return Task.CompletedTask;
    }

    public static Task MappingConditionRoundTripsThroughWorkspaceAsync()
    {
        var nodes = CreateWorkspaceNodes("Space");
        var configuration = KeyPilotConfiguration.CreateDefault("条件映射");
        var condition = new MappingCondition
        {
            Kind = MappingConditionKind.ForegroundIs,
            Applications =
            {
                new ApplicationIdentity
                {
                    ProcessName = "photoshop",
                    DisplayName = "Photoshop"
                }
            }
        };
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "中键撤销",
            Source = KeyboardSource("Space"),
            Action = new OpenUriAction { Uri = "https://example.com/" },
            Condition = condition
        });

        var restored = WorkspaceConfigurationAdapter.Restore(configuration, nodes);
        var draft = nodes.Single(node => node.Id == "Space").Mapping!;
        Assert(draft.Condition.Kind == MappingConditionKind.ForegroundIs, "草稿未恢复生效范围。");
        Assert(draft.Condition.Applications.Single().NormalizedProcessName == "photoshop", "草稿未恢复软件。");

        var captured = WorkspaceConfigurationAdapter.Capture(configuration, restored, nodes);
        var capturedCondition = captured.Profiles.Single().Mappings.Single().Condition;
        Assert(capturedCondition.Kind == MappingConditionKind.ForegroundIs, "保存后丢失生效范围。");
        Assert(capturedCondition.Applications.Single().DisplayName == "Photoshop", "保存后丢失显示名。");
        _ = KeyPilotJson.Serialize(captured);
        return Task.CompletedTask;
    }

    public static Task MediaVolumeCopiedOutputAndUriNormalizeAsync()
    {
        var nodes = CreateWorkspaceNodes("KeyA", "KeyB", "KeyC");
        var configuration = KeyPilotConfiguration.CreateDefault("扩展动作");
        var encodedA = LogicalOutputTargetCodec.Encode(
            LogicalOutputTargetCodec.FromInputSource(KeyboardSource("KeyA", exactDevice: true)));
        var actions = new MappingAction[]
        {
            new MediaControlAction { Command = MediaControlCommand.NextTrack },
            new VolumeControlAction
            {
                Command = VolumeControlCommand.SetLevelPercent,
                LevelPercent = 37
            },
            new CopyInputToOutputAction { EncodedTarget = encodedA }
        };
        var ids = new[] { "KeyA", "KeyB", "KeyC" };
        for (var index = 0; index < ids.Length; index++)
        {
            configuration.Profiles.Single().Mappings.Add(new InputMapping
            {
                Name = $"扩展动作 {index}",
                Source = KeyboardSource(ids[index]),
                Action = actions[index]
            });
        }

        var restored = WorkspaceConfigurationAdapter.Restore(configuration, nodes);
        Assert(nodes.Single(node => node.Id == "KeyA").Mapping?.ActionType ==
            WorkspaceConfigurationAdapter.MediaActionType, "媒体动作未投影到抽屉。");
        Assert(nodes.Single(node => node.Id == "KeyB").Mapping?.ActionValue == "设置音量 37%",
            "精确音量未投影为百分比。");
        Assert(nodes.Single(node => node.Id == "KeyC").Mapping?.ActionType ==
            WorkspaceConfigurationAdapter.SpecialKeyActionType, "复制按键输出未投影到按键动作。");

        var captured = WorkspaceConfigurationAdapter.Capture(configuration, restored, nodes);
        var capturedActions = captured.Profiles.Single().Mappings.Select(mapping => mapping.Action).ToArray();
        Assert(capturedActions[0] is MediaControlAction, "媒体动作往返类型错误。");
        Assert(capturedActions[1] is VolumeControlAction { LevelPercent: 37 }, "音量动作往返类型错误。");
        Assert(capturedActions[2] is CopyInputToOutputAction copied && copied.EncodedTarget == encodedA,
            "复制按键输出 token 没有完整往返。");
        _ = KeyPilotJson.Serialize(captured);

        Assert(WorkspaceConfigurationAdapter.TryCreateAction(
                WorkspaceConfigurationAdapter.UriActionType,
                "example.com/path",
                nodes,
                out var uriAction,
                out _),
            "裸域名应能创建网址动作。");
        Assert(uriAction is OpenUriAction { Uri: "https://example.com/path" },
            "裸域名没有自动补成 HTTPS。");

        var encodedGamepad = LogicalOutputTargetCodec.Encode(
            LogicalOutputTargetCodec.FromInputSource(GamepadSource(XInputButton.A)));
        var createdGamepadOutput = WorkspaceConfigurationAdapter.TryCreateAction(
            WorkspaceConfigurationAdapter.SpecialKeyActionType,
            encodedGamepad,
            nodes,
            out var gamepadOutputAction,
            out var gamepadError);
        if (WindowsViGEmXbox360Backend.ProbeBus())
        {
            Assert(createdGamepadOutput && gamepadOutputAction is CopyInputToOutputAction,
                "ViGEmBus 可用时应能保存手柄输出目标。");
        }
        else
        {
            Assert(!createdGamepadOutput && gamepadError.Contains("虚拟手柄", StringComparison.Ordinal),
                "缺少虚拟手柄后端时不应保存手柄输出目标。");
        }
        return Task.CompletedTask;
    }

    public static Task GamepadUriMappingSerializesAsync()
    {
        var nodes = CreateWorkspaceNodes("KeyA").ToList();
        var gamepadA = CreateGamepadNode(XInputButton.A);
        nodes.Add(gamepadA);
        var configuration = KeyPilotConfiguration.CreateDefault("手柄网址");
        var restored = WorkspaceConfigurationAdapter.Restore(configuration, nodes);

        Assert(!MappingDispatchPolicy.CanRequestOriginalSuppression(gamepadA.CapturedSource),
            "XInput 映射不应默认请求屏蔽原始输入。");
        gamepadA.Mapping = new MappingDraft
        {
            SourceStableId = gamepadA.StableId,
            SourceCaptureIdentity = gamepadA.CaptureIdentity,
            Source = gamepadA.CapturedSource,
            ActionType = WorkspaceConfigurationAdapter.UriActionType,
            ActionValue = "https://example.com/",
            Action = new OpenUriAction { Uri = "https://example.com/" },
            BlockOriginal = MappingDispatchPolicy.CanRequestOriginalSuppression(gamepadA.CapturedSource)
        };

        var captured = WorkspaceConfigurationAdapter.Capture(configuration, restored, nodes);
        var mapping = captured.Profiles.Single().Mappings.Single();
        Assert(mapping.Source.Control.Code == (ushort)XInputButton.A, "手柄 A 编码没有保留。");
        Assert(mapping.Action is OpenUriAction, "手柄 A 的网址动作类型错误。");
        Assert(!mapping.SuppressOriginal, "手柄 A 映射应保留原始输入。");
        _ = KeyPilotJson.Serialize(captured);
        return Task.CompletedTask;
    }

    public static Task MouseUriMappingSerializesAsync()
    {
        var nodes = CreateWorkspaceNodes("KeyA").ToList();
        var middle = CreateMouseNode("Middle", 0x04);
        nodes.Add(middle);
        var configuration = KeyPilotConfiguration.CreateDefault("鼠标网址");
        var restored = WorkspaceConfigurationAdapter.Restore(configuration, nodes);

        Assert(!MappingDispatchPolicy.CanRequestOriginalSuppression(middle.CapturedSource),
            "鼠标映射不应请求屏蔽原始输入。");
        middle.Mapping = new MappingDraft
        {
            SourceStableId = middle.StableId,
            SourceCaptureIdentity = middle.CaptureIdentity,
            Source = middle.CapturedSource,
            ActionType = WorkspaceConfigurationAdapter.UriActionType,
            ActionValue = "https://example.com/",
            Action = new OpenUriAction { Uri = "https://example.com/" },
            BlockOriginal = MappingDispatchPolicy.CanRequestOriginalSuppression(middle.CapturedSource)
        };

        var captured = WorkspaceConfigurationAdapter.Capture(configuration, restored, nodes);
        var mapping = captured.Profiles.Single().Mappings.Single();
        Assert(mapping.Source.Device.Kind == InputDeviceKind.Mouse, "鼠标设备类型没有保留。");
        Assert(mapping.Source.Control.Kind == InputControlKind.VirtualKey, "鼠标控制类型没有保留。");
        Assert(mapping.Source.Control.Code == 4, "中键编码没有保留。");
        Assert(mapping.Action is OpenUriAction, "鼠标中键的网址动作类型错误。");
        Assert(!mapping.SuppressOriginal, "鼠标映射应保留原始输入。");

        var serialized = KeyPilotJson.Serialize(captured);
        var reloaded = KeyPilotJson.Deserialize(serialized);
        var reloadedNodes = CreateWorkspaceNodes("KeyA").ToList();
        reloadedNodes.Add(CreateMouseNode("Middle", 0x04));
        var reloadedRestore = WorkspaceConfigurationAdapter.Restore(reloaded, reloadedNodes);
        Assert(reloadedRestore.DetachedMappings.Count == 0, "鼠标映射重新载入后不应成为分离映射。");
        Assert(reloadedNodes.Single(node => node.Id == "Middle").Mapping is not null,
            "鼠标中键映射重新载入后没有恢复到工作台节点。");
        return Task.CompletedTask;
    }

    public static Task GamepadAnalogMappingSerializesAsync()
    {
        var nodes = CreateWorkspaceNodes("KeyA").ToList();
        var leftTrigger = CreateGamepadVirtualNode(XInputVirtualControl.LeftTrigger);
        nodes.Add(leftTrigger);
        var configuration = KeyPilotConfiguration.CreateDefault("模拟手柄网址");
        var restored = WorkspaceConfigurationAdapter.Restore(configuration, nodes);

        Assert(!MappingDispatchPolicy.CanRequestOriginalSuppression(leftTrigger.CapturedSource),
            "XInput 模拟输入不应请求屏蔽原始输入。");
        leftTrigger.Mapping = new MappingDraft
        {
            SourceStableId = leftTrigger.StableId,
            SourceCaptureIdentity = leftTrigger.CaptureIdentity,
            Source = leftTrigger.CapturedSource,
            ActionType = WorkspaceConfigurationAdapter.UriActionType,
            ActionValue = "https://example.com/",
            Action = new OpenUriAction { Uri = "https://example.com/" },
            BlockOriginal = false
        };

        var captured = WorkspaceConfigurationAdapter.Capture(configuration, restored, nodes);
        var mapping = captured.Profiles.Single().Mappings.Single();
        Assert(mapping.Source.Control.Kind == InputControlKind.GamepadAxisDirection,
            "LT 的控制类型没有保留。");
        Assert(mapping.Source.Control.Code == (int)XInputVirtualControl.LeftTrigger,
            "LT 的稳定编码没有保留。");
        Assert(!mapping.SuppressOriginal, "模拟输入映射必须保持只读放行。");

        var serialized = KeyPilotJson.Serialize(captured);
        var reloaded = KeyPilotJson.Deserialize(serialized);
        var reloadedNodes = CreateWorkspaceNodes("KeyA").ToList();
        reloadedNodes.Add(CreateGamepadVirtualNode(XInputVirtualControl.LeftTrigger));
        var reloadedRestore = WorkspaceConfigurationAdapter.Restore(reloaded, reloadedNodes);
        Assert(reloadedRestore.DetachedMappings.Count == 0, "LT 映射重新载入后不应成为分离映射。");
        Assert(reloadedNodes.Single(node => node.Id == nameof(XInputVirtualControl.LeftTrigger)).Mapping is not null,
            "LT 映射重新载入后没有恢复到工作台节点。");
        return Task.CompletedTask;
    }

    public static Task AdvancedAndDetachedMappingsPassThroughUnchangedAsync()
    {
        var nodes = CreateWorkspaceNodes("Space");
        var configuration = KeyPilotConfiguration.CreateDefault("活动方案");
        var advanced = new InputMapping
        {
            Name = "高级宏",
            Source = KeyboardSource("Space"),
            Action = new MacroAction
            {
                Steps =
                {
                    new SendKeyAction { Target = KeyboardControl("KeyA") },
                    new DelayAction { Milliseconds = 73 },
                    new LaunchProgramAction
                    {
                        FilePath = @"C:\Tools\Panel.exe",
                        Arguments = "--silent",
                        WorkingDirectory = @"C:\Tools"
                    }
                }
            }
        };
        var detached = new InputMapping
        {
            Name = "未知来源",
            Source = HidSource() with
            {
                Control = HidSource().Control with { Code = 9 }
            },
            Action = new OpenUriAction { Uri = "https://detached.example/" }
        };
        configuration.Profiles.Single().Mappings.Add(advanced);
        configuration.Profiles.Single().Mappings.Add(detached);
        var inactive = new MappingProfile
        {
            Name = "未激活方案",
            Mappings =
            {
                new InputMapping
                {
                    Name = "保留",
                    Source = KeyboardSource("KeyA"),
                    Action = new OpenUriAction { Uri = "https://inactive.example/" }
                }
            }
        };
        configuration.Profiles.Add(inactive);
        var originalAdvancedJson = KeyPilotJson.Serialize(configuration);

        var restored = WorkspaceConfigurationAdapter.Restore(configuration, nodes);
        var draft = nodes.Single(node => node.Id == "Space").Mapping!;
        Assert(draft.IsReadOnlyPassthrough, "宏应作为只读高级动作显示。");
        Assert(restored.DetachedMappings.Single().Id == detached.Id, "未知来源映射没有进入透传集合。");

        var captured = WorkspaceConfigurationAdapter.Capture(configuration, restored, nodes);
        var capturedActive = captured.Profiles.Single(profile => profile.Id == configuration.ActiveProfileId);
        Assert(ReferenceEquals(capturedActive.Mappings.Single(mapping => mapping.Id == advanced.Id), advanced),
            "高级映射必须原对象透传，不能重建并丢字段。");
        Assert(ReferenceEquals(capturedActive.Mappings.Single(mapping => mapping.Id == detached.Id), detached),
            "分离映射必须原对象透传。");
        Assert(ReferenceEquals(captured.Profiles.Single(profile => profile.Id == inactive.Id), inactive),
            "非活动方案必须保持不变。");
        Assert(KeyPilotJson.Serialize(captured) == originalAdvancedJson,
            "未编辑工作区的完整 Core JSON 应稳定往返。");
        return Task.CompletedTask;
    }

    public static Task ChordAndRotationSourcesRoundTripAsync()
    {
        var nodes = CreateWorkspaceNodes("ControlLeft", "Digit1").ToList();
        var chord = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Composite,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.InputChord,
                Code = 1
            },
            ChordMembers =
            [
                KeyboardSource("ControlLeft", exactDevice: true),
                KeyboardSource("Digit1", exactDevice: true)
            ]
        };
        var rotation = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Gamepad,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.GamepadRotation,
                Code = (int)GamepadRotationControl.LeftClockwise
            }
        };
        nodes.Add(new InputNodeState
        {
            Id = GamepadRotationControl.LeftClockwise.ToString(),
            Label = "L↻",
            FriendlyName = "手柄 L↻ 一圈",
            Kind = InputNodeKind.Gamepad,
            RawCode = string.Empty,
            CapturedSource = rotation,
            CaptureIdentity = rotation.CanonicalKey,
            Location = "Gamepad Rotation"
        });

        var configuration = KeyPilotConfiguration.CreateDefault("组合方案");
        configuration.SpecialKeySlots[0] = configuration.SpecialKeySlots[0] with
        {
            DisplayName = "组合：Ctrl + 1",
            Source = chord
        };
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "组合网址",
            SuppressOriginal = false,
            Source = chord,
            Action = new OpenUriAction { Uri = "https://example.com/chord" }
        });
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "左杆顺时针",
            SuppressOriginal = false,
            Source = rotation,
            Action = new OpenUriAction { Uri = "https://example.com/rotation" }
        });

        var restored = WorkspaceConfigurationAdapter.Restore(configuration, nodes);
        var special = nodes.Single(node => node.StableId == "special:1");
        var rotationNode = nodes.Single(node =>
            node.StableId == $"gamepad:{GamepadRotationControl.LeftClockwise}");
        Assert(special.CapturedSource?.CanonicalKey == chord.CanonicalKey,
            "组合槽位来源没有恢复。");
        Assert(special.Mapping?.BlockOriginal == false, "组合映射不应恢复为原键抑制。");
        Assert(rotationNode.Mapping is not null, "整圈手势映射没有投影到节点。");

        var captured = WorkspaceConfigurationAdapter.Capture(configuration, restored, nodes);
        var capturedProfile = captured.Profiles.Single(profile => profile.Id == restored.ActiveProfileId);
        Assert(captured.SpecialKeySlots[0].Source?.CanonicalKey == chord.CanonicalKey,
            "组合槽位没有完整往返。");
        Assert(capturedProfile.Mappings.Any(mapping => mapping.Source.CanonicalKey == chord.CanonicalKey),
            "组合映射没有完整往返。");
        Assert(capturedProfile.Mappings.Any(mapping => mapping.Source.CanonicalKey == rotation.CanonicalKey),
            "整圈手势映射没有完整往返。");
        _ = KeyPilotJson.Serialize(captured);
        return Task.CompletedTask;
    }

    private static InputNodeState[] CreateWorkspaceNodes(params string[] keyboardIds)
    {
        var nodes = keyboardIds.Select(id => new InputNodeState
        {
            Id = id,
            Label = id,
            FriendlyName = id,
            Kind = InputNodeKind.Keyboard,
            RawCode = id,
            CapturedSource = KeyboardSource(id),
            CaptureIdentity = KeyboardSource(id).CanonicalKey,
            Location = "Keyboard"
        }).ToList();
        nodes.Add(CreateGamepadNode(XInputButton.Y));
        for (var number = 1; number <= 10; number++)
        {
            nodes.Add(new InputNodeState
            {
                Id = number.ToString(),
                Label = $"SPECIAL {number:00}",
                FriendlyName = "等待采集",
                Kind = InputNodeKind.Special,
                RawCode = string.Empty,
                Location = "HID"
            });
        }

        return nodes.ToArray();
    }

    private static InputNodeState CreateGamepadNode(XInputButton button)
    {
        var source = GamepadSource(button);
        return new InputNodeState
        {
            Id = button.ToString(),
            Label = button.ToString(),
            FriendlyName = $"手柄 {button}",
            Kind = InputNodeKind.Gamepad,
            RawCode = string.Empty,
            CapturedSource = source,
            CaptureIdentity = source.CanonicalKey,
            Location = "Gamepad"
        };
    }

    private static InputNodeState CreateGamepadVirtualNode(XInputVirtualControl control)
    {
        var source = GamepadVirtualSource(control);
        return new InputNodeState
        {
            Id = control.ToString(),
            Label = control.ToString(),
            FriendlyName = $"手柄 {control}",
            Kind = InputNodeKind.Gamepad,
            RawCode = string.Empty,
            CapturedSource = source,
            CaptureIdentity = source.CanonicalKey,
            Location = "Gamepad Analog"
        };
    }

    private static InputNodeState CreateMouseNode(string id, ushort virtualKey)
    {
        var source = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Mouse,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.VirtualKey,
                Code = virtualKey
            }
        };
        return new InputNodeState
        {
            Id = id,
            Label = id,
            FriendlyName = $"鼠标 {id}",
            Kind = InputNodeKind.Mouse,
            RawCode = $"VK 0x{virtualKey:X2}",
            CapturedSource = source,
            CaptureIdentity = source.CanonicalKey,
            Location = "Mouse"
        };
    }

    private static InputSource KeyboardSource(string stableId, bool exactDevice = false) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Keyboard,
            MatchMode = exactDevice ? DeviceMatchMode.ExactDevice : DeviceMatchMode.AnyOfKind,
            DeviceId = exactDevice ? @"\\?\HID#KEYBOARD#TEST" : null
        },
        Control = KeyboardControl(stableId)
    };

    private static InputControlId KeyboardControl(string stableId)
    {
        if (!KeyboardKeyResolver.TryGetSet1Code(stableId, out var code))
        {
            throw new InvalidOperationException($"Unknown test keyboard ID {stableId}.");
        }

        return new InputControlId
        {
            Kind = InputControlKind.KeyboardScanCode,
            Code = code.MakeCode,
            IsExtended = code.Prefix != 0,
            RawQualifier = $"RAWKEYBOARD-V1;PREFIX={code.Prefix:X4}"
        };
    }

    private static InputSource GamepadSource(XInputButton button) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Gamepad,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.GamepadButton,
            Code = (int)button
        }
    };

    private static InputSource GamepadVirtualSource(XInputVirtualControl control) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Gamepad,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.GamepadAxisDirection,
            Code = (int)control
        }
    };

    private static InputSource HidSource() => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Hid,
            MatchMode = DeviceMatchMode.ExactDevice,
            DeviceId = @"\\?\HID#VID_1234&PID_5678#TEST",
            VendorId = 0x1234,
            ProductId = 0x5678
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.RawCode,
            Code = 7,
            UsagePage = 0xFF00,
            Usage = 1,
            RawQualifier = "OFFSET=1;MASK=01;BASE=00;ACTIVE=01"
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
