using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Actions;
using KeyPilot.Platform.Windows.Input;
using System.Text.RegularExpressions;

namespace KeyPilot.App.Presentation;

/// <summary>
/// Projects the durable Core configuration onto the prototype workspace. The UI remains a view:
/// Core objects that the action drawer cannot represent are carried through byte-for-byte by
/// the JSON serializer instead of being silently simplified.
/// </summary>
internal static class WorkspaceConfigurationAdapter
{
    public const string ShortcutActionType = "快捷键";
    public const string SpecialKeyActionType = "按键 / 组合键";
    public const string LegacySpecialKeyActionType = "特殊按键";
    public const string MediaActionType = "媒体控制";
    public const string VolumeActionType = "音量控制";
    public const string LaunchActionType = "应用 / 文件";
    public const string UriActionType = "网址";
    public const string ScriptActionType = "脚本 / 命令";
    public const string GamepadActionType = "手柄按键";
    public const string PassthroughActionType = "高级动作（原样保留）";

    private const int DefaultHoldMilliseconds = 30;

    private static readonly IReadOnlyDictionary<string, string> ShortcutAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl"] = "ControlLeft",
            ["Control"] = "ControlLeft",
            ["CtrlLeft"] = "ControlLeft",
            ["LeftCtrl"] = "ControlLeft",
            ["CtrlRight"] = "ControlRight",
            ["RightCtrl"] = "ControlRight",
            ["Alt"] = "AltLeft",
            ["AltLeft"] = "AltLeft",
            ["LeftAlt"] = "AltLeft",
            ["AltRight"] = "AltRight",
            ["RightAlt"] = "AltRight",
            ["Shift"] = "ShiftLeft",
            ["ShiftLeft"] = "ShiftLeft",
            ["LeftShift"] = "ShiftLeft",
            ["ShiftRight"] = "ShiftRight",
            ["RightShift"] = "ShiftRight",
            ["Win"] = "MetaLeft",
            ["Windows"] = "MetaLeft",
            ["Meta"] = "MetaLeft",
            ["WinLeft"] = "MetaLeft",
            ["LeftWin"] = "MetaLeft",
            ["WinRight"] = "MetaRight",
            ["RightWin"] = "MetaRight",
            ["Esc"] = "Escape",
            ["Return"] = "Enter",
            ["Up"] = "ArrowUp",
            ["Down"] = "ArrowDown",
            ["Left"] = "ArrowLeft",
            ["Right"] = "ArrowRight",
            ["Plus"] = "Equal",
            ["Equals"] = "Equal"
        };

    internal sealed record RestoreResult(
        Guid ActiveProfileId,
        string ActiveProfileName,
        IReadOnlyList<InputMapping> DetachedMappings);

    public static RestoreResult Restore(
        KeyPilotConfiguration configuration,
        IReadOnlyCollection<InputNodeState> nodes)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(nodes);

        foreach (var node in nodes)
        {
            node.Mapping = null;
        }

        RestoreSpecialSlots(configuration.SpecialKeySlots, nodes);

        var activeProfile = configuration.ActiveProfileId is Guid activeId
            ? configuration.Profiles.FirstOrDefault(profile => profile.Id == activeId)
            : null;
        activeProfile ??= configuration.Profiles.FirstOrDefault(profile => profile.IsEnabled)
            ?? configuration.Profiles.First();

        var detached = new List<InputMapping>();
        foreach (var mapping in activeProfile.Mappings)
        {
            var sourceNode = FindSourceNode(mapping.Source, nodes);
            if (sourceNode is null || sourceNode.Mapping is not null)
            {
                detached.Add(mapping);
                continue;
            }

            sourceNode.Mapping = CreateDraft(mapping, sourceNode, nodes);
        }

        return new RestoreResult(activeProfile.Id, activeProfile.Name, detached);
    }

    public static KeyPilotConfiguration Capture(
        KeyPilotConfiguration current,
        RestoreResult restore,
        IReadOnlyCollection<InputNodeState> nodes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(nodes);

        var activeProfile = current.Profiles.FirstOrDefault(profile => profile.Id == restore.ActiveProfileId)
            ?? throw new InvalidOperationException("The active profile no longer exists in the Core configuration.");
        var mappings = new List<InputMapping>();
        var emittedIds = new HashSet<Guid>();

        foreach (var node in nodes)
        {
            if (node.Mapping is null)
            {
                continue;
            }

            var mapping = CreateMapping(node, node.Mapping, nodes);
            if (!emittedIds.Add(mapping.Id))
            {
                throw new InvalidOperationException($"Mapping ID {mapping.Id} appears more than once in the workspace.");
            }

            mappings.Add(mapping);
        }

        foreach (var mapping in restore.DetachedMappings)
        {
            if (emittedIds.Add(mapping.Id))
            {
                mappings.Add(mapping);
            }
        }

        var updatedProfile = activeProfile with { Mappings = mappings };
        var profiles = current.Profiles
            .Select(profile => profile.Id == updatedProfile.Id ? updatedProfile : profile)
            .ToList();

        return current with
        {
            ActiveProfileId = updatedProfile.Id,
            Profiles = profiles,
            SpecialKeySlots = CaptureSpecialSlots(nodes)
        };
    }

    public static bool TryCreateAction(
        string actionType,
        string value,
        IReadOnlyCollection<InputNodeState> nodes,
        out MappingAction? action,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(actionType);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(nodes);

        action = null;
        error = string.Empty;
        switch (actionType)
        {
            case ShortcutActionType:
                var controls = new List<InputControlId>();
                foreach (var token in ParseShortcutTokens(value))
                {
                    if (!TryParseShortcutToken(token, out var control))
                    {
                        error = $"无法识别快捷键“{token}”。";
                        return false;
                    }

                    if (controls.Any(existing => existing.CanonicalKey == control.CanonicalKey))
                    {
                        error = "快捷键不能包含重复按键。";
                        return false;
                    }

                    controls.Add(control);
                }

                if (controls.Count is < 1 or > 8)
                {
                    error = "快捷键必须包含 1 至 8 个按键。";
                    return false;
                }

                action = controls.Count == 1
                    ? new SendKeyAction { Target = controls[0], HoldMilliseconds = DefaultHoldMilliseconds }
                    : new ShortcutAction { Keys = controls, HoldMilliseconds = DefaultHoldMilliseconds };
                return true;

            case SpecialKeyActionType:
            case LegacySpecialKeyActionType:
                if (LogicalOutputTargetCodec.TryDecode(value.Trim(), out var logicalTarget, out _) &&
                    logicalTarget is not null)
                {
                    if (logicalTarget.Family == LogicalOutputFamily.Hid)
                    {
                        error = "已复制的是 HID 输出目标；当前版本缺少虚拟 HID 输出后端。";
                        return false;
                    }

                    if (logicalTarget.Family == LogicalOutputFamily.Gamepad &&
                        !WindowsViGEmXbox360Backend.ProbeBus())
                    {
                        error = "已复制的是手柄输出目标；当前没有 ViGEmBus 虚拟手柄后端。";
                        return false;
                    }

                    if (logicalTarget.Family == LogicalOutputFamily.Keyboard)
                    {
                        foreach (var control in logicalTarget.Controls)
                        {
                            if (!WindowsSendInputBackend.TryValidateControl(control, out var backendError))
                            {
                                error = $"该按键不能由当前键盘输出后端模拟：{backendError}";
                                return false;
                            }
                        }
                    }

                    action = new CopyInputToOutputAction
                    {
                        EncodedTarget = value.Trim(),
                        HoldMilliseconds = DefaultHoldMilliseconds
                    };
                    return true;
                }

                var special = FindSpecialTarget(value, nodes);
                if (special is null || !int.TryParse(special.Id, out var slotNumber))
                {
                    error = "请粘贴主页复制的 KeyPilot 按键信息，或输入一个旧版特殊键槽位名称。";
                    return false;
                }

                if (!WindowsSendInputBackend.TryValidateControl(
                        special.CapturedSource?.Control,
                        out _))
                {
                    error = "当前 SendInput 后端只能输出采集为键盘扫描码的特殊键；原始 HID 特殊键需要后续虚拟 HID 后端。";
                    return false;
                }

                action = new EmitSpecialKeyAction { SlotNumber = slotNumber };
                return true;

            case MediaActionType:
                if (!TryParseMediaCommand(value, out var mediaCommand))
                {
                    error = "请选择播放 / 暂停、停止、上一首或下一首。";
                    return false;
                }

                action = new MediaControlAction { Command = mediaCommand };
                return true;

            case VolumeActionType:
                if (!TryParseVolumeAction(value, out var volumeAction))
                {
                    error = "请选择音量增加、音量降低、静音切换，或设置 0–100% 的音量。";
                    return false;
                }

                action = volumeAction;
                return true;

            case LaunchActionType:
                action = new LaunchProgramAction { FilePath = value.Trim().Trim('"') };
                return true;

            case UriActionType:
                if (!ExternalUriNormalizer.TryNormalize(value, out var normalizedUri, out var uriError))
                {
                    error = $"网址或协议地址无效：{uriError}";
                    return false;
                }

                action = new OpenUriAction { Uri = normalizedUri };
                return true;

            case ScriptActionType:
                action = new RunScriptAction { ScriptPath = value.Trim().Trim('"') };
                return true;

            case GamepadActionType:
                var gamepad = FindGamepadTarget(value, nodes);
                if (gamepad?.CapturedSource is null)
                {
                    error = "请选择一个有效的手柄按键。";
                    return false;
                }

                if (!LogicalOutputTargetCodec.TryFromInputSource(gamepad.CapturedSource, out var gamepadTarget, out var gamepadCodecError) ||
                    gamepadTarget is null)
                {
                    error = gamepadCodecError;
                    return false;
                }

                if (gamepadTarget.Family != LogicalOutputFamily.Gamepad)
                {
                    error = "该目标不是手柄输出。";
                    return false;
                }

                if (!WindowsViGEmXbox360Backend.ProbeBus())
                {
                    error = "当前没有虚拟手柄后端（需要 ViGEmBus）。采集和键盘映射仍可用。";
                    return false;
                }

                action = new CopyInputToOutputAction
                {
                    EncodedTarget = LogicalOutputTargetCodec.Encode(gamepadTarget),
                    HoldMilliseconds = DefaultHoldMilliseconds
                };
                return true;

            default:
                error = $"不支持动作类型“{actionType}”。";
                return false;
        }
    }

    public static MappingTrigger CreateTrigger(string displayValue, MappingTrigger? original = null)
    {
        var kind = displayValue switch
        {
            "双击" => MappingTriggerKind.DoublePress,
            "长按 600ms" => MappingTriggerKind.LongPress,
            "按下时" => MappingTriggerKind.KeyDown,
            "释放时" => MappingTriggerKind.KeyUp,
            _ => MappingTriggerKind.SinglePress
        };

        return new MappingTrigger
        {
            Kind = kind,
            LongPressMilliseconds = original?.LongPressMilliseconds ?? 600,
            DoublePressWindowMilliseconds = original?.DoublePressWindowMilliseconds ?? 350
        };
    }

    public static string TriggerDisplayName(MappingTrigger trigger) => trigger.Kind switch
    {
        MappingTriggerKind.DoublePress => "双击",
        MappingTriggerKind.LongPress => "长按 600ms",
        MappingTriggerKind.KeyDown => "按下时",
        MappingTriggerKind.KeyUp => "释放时",
        _ => "单击"
    };

    private static void RestoreSpecialSlots(
        IReadOnlyList<SpecialKeySlot> slots,
        IReadOnlyCollection<InputNodeState> nodes)
    {
        foreach (var node in nodes.Where(node => node.Kind == InputNodeKind.Special))
        {
            var slot = int.TryParse(node.Id, out var number)
                ? slots.FirstOrDefault(candidate => candidate.Number == number)
                : null;
            if (slot is null)
            {
                continue;
            }

            node.FriendlyName = slot.DisplayName;
            node.CapturedSource = slot.Source;
            node.CaptureIdentity = slot.Source?.CanonicalKey;
            node.DevicePath = slot.Source?.Device.DeviceId;
            node.RawCode = slot.Source is null ? string.Empty : FormatStoredSource(slot.Source);
            node.Location = slot.Source is null ? "HID" : StoredLocation(slot.Source);
        }
    }

    private static List<SpecialKeySlot> CaptureSpecialSlots(IReadOnlyCollection<InputNodeState> nodes)
    {
        var specialNodes = nodes
            .Where(node => node.Kind == InputNodeKind.Special)
            .ToDictionary(node => int.Parse(node.Id));
        return Enumerable.Range(SpecialKeySlots.Minimum, SpecialKeySlots.Count)
            .Select(number =>
            {
                if (!specialNodes.TryGetValue(number, out var node))
                {
                    throw new InvalidOperationException($"Special-key slot {number} is missing from the workspace.");
                }

                return new SpecialKeySlot
                {
                    Number = number,
                    DisplayName = string.IsNullOrWhiteSpace(node.FriendlyName)
                        ? $"特殊键 {number}"
                        : node.FriendlyName.Trim(),
                    Source = node.CapturedSource
                };
            })
            .ToList();
    }

    private static MappingDraft CreateDraft(
        InputMapping mapping,
        InputNodeState sourceNode,
        IReadOnlyCollection<InputNodeState> nodes)
    {
        var draft = new MappingDraft
        {
            PersistedId = mapping.Id,
            OriginalMapping = mapping,
            SourceStableId = sourceNode.StableId,
            SourceCaptureIdentity = mapping.Source.CanonicalKey,
            SourceDevicePath = mapping.Source.Device.DeviceId,
            Source = mapping.Source,
            Action = mapping.Action,
            Trigger = TriggerDisplayName(mapping.Trigger),
            Condition = mapping.Condition ?? new(),
            BlockOriginal = mapping.SuppressOriginal
        };

        if (!TryProjectAction(mapping.Action, nodes, draft) || !CanProjectTrigger(mapping.Trigger))
        {
            draft.IsReadOnlyPassthrough = true;
            draft.ActionType = PassthroughActionType;
            draft.ActionValue = DescribePassthrough(mapping);
        }

        return draft;
    }

    private static InputMapping CreateMapping(
        InputNodeState node,
        MappingDraft draft,
        IReadOnlyCollection<InputNodeState> nodes)
    {
        if (draft.IsReadOnlyPassthrough)
        {
            return draft.OriginalMapping
                ?? throw new InvalidOperationException("A passthrough mapping has no original Core mapping.");
        }

        var source = draft.Source ?? node.CapturedSource
            ?? throw new InvalidOperationException($"{node.StableId} has no captured input source.");
        var action = draft.Action;
        if (action is null &&
            !TryCreateAction(draft.ActionType, draft.ActionValue, nodes, out action, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var original = draft.OriginalMapping;
        return new InputMapping
        {
            Id = draft.PersistedId ?? original?.Id ?? Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(original?.Name)
                ? $"{node.Label} → {MappingNameTarget(draft)}"
                : original.Name,
            IsEnabled = original?.IsEnabled ?? true,
            SuppressOriginal = draft.BlockOriginal,
            Source = source,
            Trigger = CreateTrigger(draft.Trigger, original?.Trigger),
            Condition = draft.Condition ?? original?.Condition ?? new(),
            Action = action ?? throw new InvalidOperationException("The mapping action was not created.")
        };
    }

    private static string MappingNameTarget(MappingDraft draft) =>
        draft.ActionValue.StartsWith(LogicalOutputTargetCodec.Prefix, StringComparison.Ordinal)
            ? SpecialKeyActionType
            : draft.ActionValue.Length <= 80
                ? draft.ActionValue
                : draft.ActionType;

    private static bool TryProjectAction(
        MappingAction action,
        IReadOnlyCollection<InputNodeState> nodes,
        MappingDraft draft)
    {
        switch (action)
        {
            case SendKeyAction sendKey
                when sendKey.Transition == KeyTransition.Press &&
                     sendKey.HoldMilliseconds == DefaultHoldMilliseconds:
                var gamepad = FindNodeByControl(sendKey.Target, nodes, InputNodeKind.Gamepad);
                if (gamepad is not null)
                {
                    draft.ActionType = GamepadActionType;
                    draft.ActionValue = gamepad.Id;
                    SetTarget(draft, gamepad);
                    return true;
                }

                if (TryFormatShortcutControl(sendKey.Target, out var key))
                {
                    draft.ActionType = ShortcutActionType;
                    draft.ActionValue = key;
                    return true;
                }

                return false;

            case ShortcutAction shortcut when shortcut.HoldMilliseconds == DefaultHoldMilliseconds:
                var tokens = new List<string>();
                foreach (var control in shortcut.Keys)
                {
                    if (!TryFormatShortcutControl(control, out var token))
                    {
                        return false;
                    }

                    tokens.Add(token);
                }

                draft.ActionType = ShortcutActionType;
                draft.ActionValue = string.Join(" + ", tokens);
                return tokens.Count > 0;

            case LaunchProgramAction launch
                when string.IsNullOrWhiteSpace(launch.Arguments) &&
                     string.IsNullOrWhiteSpace(launch.WorkingDirectory):
                draft.ActionType = LaunchActionType;
                draft.ActionValue = launch.FilePath;
                return true;

            case OpenUriAction uri when ExternalUriNormalizer.TryNormalize(uri.Uri, out var normalizedUri, out _):
                draft.ActionType = UriActionType;
                draft.ActionValue = normalizedUri;
                return true;

            case RunScriptAction script when string.IsNullOrWhiteSpace(script.Arguments):
                draft.ActionType = ScriptActionType;
                draft.ActionValue = script.ScriptPath;
                return true;

            case EmitSpecialKeyAction special:
                var slot = nodes.FirstOrDefault(node =>
                    node.Kind == InputNodeKind.Special && node.Id == special.SlotNumber.ToString());
                if (slot is null)
                {
                    return false;
                }

                draft.ActionType = SpecialKeyActionType;
                draft.ActionValue = slot.Label;
                SetTarget(draft, slot);
                return true;

            case CopyInputToOutputAction copied
                when copied.HoldMilliseconds == DefaultHoldMilliseconds &&
                     LogicalOutputTargetCodec.TryDecode(copied.EncodedTarget, out var target, out _) &&
                     target is not null:
                draft.ActionType = SpecialKeyActionType;
                draft.ActionValue = copied.EncodedTarget;
                return true;

            case MediaControlAction media when Enum.IsDefined(media.Command):
                draft.ActionType = MediaActionType;
                draft.ActionValue = FormatMediaCommand(media.Command);
                return true;

            case VolumeControlAction volume when TryFormatVolumeAction(volume, out var volumeValue):
                draft.ActionType = VolumeActionType;
                draft.ActionValue = volumeValue;
                return true;

            default:
                return false;
        }
    }

    private static void SetTarget(MappingDraft draft, InputNodeState target)
    {
        draft.TargetStableId = target.StableId;
        draft.TargetCaptureIdentity = target.CaptureIdentity;
        draft.TargetSource = target.CapturedSource;
    }

    private static InputNodeState? FindSourceNode(
        InputSource source,
        IReadOnlyCollection<InputNodeState> nodes)
    {
        var exact = nodes.FirstOrDefault(node =>
            node.CapturedSource is not null &&
            string.Equals(node.CapturedSource.CanonicalKey, source.CanonicalKey, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        return source.Device.Kind switch
        {
            InputDeviceKind.Keyboard => FindNodeByControl(source.Control, nodes, InputNodeKind.Keyboard),
            InputDeviceKind.Gamepad => FindNodeByControl(source.Control, nodes, InputNodeKind.Gamepad),
            InputDeviceKind.Mouse => FindNodeByControl(source.Control, nodes, InputNodeKind.Mouse),
            InputDeviceKind.Hid => FindNodeByControl(source.Control, nodes, InputNodeKind.Remote)
                ?? FindNodeByControl(source.Control, nodes, InputNodeKind.Special),
            _ => null
        };
    }

    private static InputNodeState? FindNodeByControl(
        InputControlId control,
        IReadOnlyCollection<InputNodeState> nodes,
        InputNodeKind kind) => nodes.FirstOrDefault(node =>
            node.Kind == kind &&
            node.CapturedSource is not null &&
            ControlsIdentifySameKnownButton(node.CapturedSource.Control, control));

    private static bool ControlsIdentifySameKnownButton(InputControlId left, InputControlId right)
    {
        if (left.Kind != right.Kind || left.Code != right.Code)
        {
            return false;
        }

        return left.Kind switch
        {
            InputControlKind.KeyboardScanCode => left.IsExtended == right.IsExtended,
            InputControlKind.VirtualKey => true,
            InputControlKind.GamepadButton => true,
            InputControlKind.GamepadAxisDirection => true,
            InputControlKind.GamepadRotation => true,
            _ => left.CanonicalKey == right.CanonicalKey
        };
    }

    private static InputNodeState? FindSpecialTarget(
        string value,
        IReadOnlyCollection<InputNodeState> nodes)
    {
        var matches = nodes.Where(node =>
            node.Kind == InputNodeKind.Special &&
            node.CapturedSource is not null &&
            (string.Equals(node.Label, value, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Id, value, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.FriendlyName, value, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static InputNodeState? FindGamepadTarget(
        string value,
        IReadOnlyCollection<InputNodeState> nodes)
    {
        var requested = value.StartsWith("Gamepad ", StringComparison.OrdinalIgnoreCase)
            ? value[8..].Trim()
            : value.Trim();
        var matches = nodes.Where(node =>
            node.Kind == InputNodeKind.Gamepad &&
            (string.Equals(node.Id, requested, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(node.Label, requested, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string[] ParseShortcutTokens(string value)
    {
        var normalized = value.Trim();
        var plusIsFinalKey = normalized.EndsWith("+ +", StringComparison.Ordinal) ||
            normalized == "+";
        if (plusIsFinalKey && normalized.Length > 1)
        {
            normalized = normalized[..^2].TrimEnd();
        }

        var tokens = normalized.Length == 0 || normalized == "+"
            ? new List<string>()
            : normalized.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        if (plusIsFinalKey)
        {
            tokens.Add("+");
        }

        return tokens.ToArray();
    }

    private static bool TryParseShortcutToken(string token, out InputControlId control)
    {
        var stableId = ShortcutAliases.GetValueOrDefault(token, token);
        if (token.Length == 1)
        {
            stableId = token[0] switch
            {
                >= 'a' and <= 'z' => $"Key{char.ToUpperInvariant(token[0])}",
                >= 'A' and <= 'Z' => $"Key{token[0]}",
                >= '0' and <= '9' => $"Digit{token[0]}",
                '`' => "Backquote",
                '-' => "Minus",
                '=' or '+' => "Equal",
                '[' => "BracketLeft",
                ']' => "BracketRight",
                '\\' => "Backslash",
                ';' => "Semicolon",
                '\'' => "Quote",
                ',' => "Comma",
                '.' => "Period",
                '/' => "Slash",
                _ => stableId
            };
        }

        if (!KeyboardKeyResolver.TryGetSet1Code(stableId, out var code))
        {
            control = new InputControlId();
            return false;
        }

        control = new InputControlId
        {
            Kind = InputControlKind.KeyboardScanCode,
            Code = code.MakeCode,
            IsExtended = code.Prefix != 0,
            RawQualifier = $"RAWKEYBOARD-V1;PREFIX={code.Prefix:X4}"
        };
        return true;
    }

    private static bool TryFormatShortcutControl(InputControlId control, out string token)
    {
        token = string.Empty;
        if (control.Kind != InputControlKind.KeyboardScanCode || control.Code is <= 0 or > ushort.MaxValue)
        {
            return false;
        }

        var prefix = control.RawQualifier?.Contains("PREFIX=0004", StringComparison.OrdinalIgnoreCase) == true
            ? (ushort)0x0004
            : control.IsExtended
                ? (ushort)0x0002
                : (ushort)0;
        if (!KeyboardKeyResolver.TryResolveSet1Code((ushort)control.Code, prefix, out var stableId))
        {
            return false;
        }

        token = stableId switch
        {
            "ControlLeft" => "CtrlLeft",
            "ControlRight" => "CtrlRight",
            "MetaLeft" => "WinLeft",
            "MetaRight" => "WinRight",
            "Escape" => "Esc",
            "ArrowUp" => "Up",
            "ArrowDown" => "Down",
            "ArrowLeft" => "Left",
            "ArrowRight" => "Right",
            "Equal" => "=",
            "Backquote" => "`",
            "BracketLeft" => "[",
            "BracketRight" => "]",
            "Backslash" => "\\",
            "Semicolon" => ";",
            "Quote" => "'",
            "Comma" => ",",
            "Period" => ".",
            "Slash" => "/",
            "Minus" => "-",
            _ when stableId.StartsWith("Key", StringComparison.Ordinal) => stableId[3..],
            _ when stableId.StartsWith("Digit", StringComparison.Ordinal) => stableId[5..],
            _ => stableId
        };
        return true;
    }

    private static string FormatStoredSource(InputSource source) => source.Control.Kind switch
    {
        InputControlKind.InputChord => $"组合键 · {source.ChordMembers?.Count ?? 0} 个成员 · 2 秒录制",
        InputControlKind.InputSequence => $"按键序列 · {source.PatternSteps?.Count ?? 0} 个边沿 · 2 秒录制",
        _ => $"{source.Device.Kind} · {source.Control.Kind} · 0x{source.Control.Code:X}"
    };

    private static string StoredLocation(InputSource source) => source.Control.Kind switch
    {
        InputControlKind.InputChord => "Input Chord",
        InputControlKind.InputSequence => "Input Sequence",
        _ => source.Device.Kind switch
        {
            InputDeviceKind.Keyboard => "Keyboard / OEM",
            InputDeviceKind.Gamepad => "Gamepad",
            InputDeviceKind.ConsumerControl => "HID / Consumer",
            _ => "HID / Vendor"
        }
    };

    private static bool CanProjectTrigger(MappingTrigger trigger) => trigger.Kind switch
    {
        MappingTriggerKind.LongPress => trigger.LongPressMilliseconds == 600,
        MappingTriggerKind.DoublePress => trigger.DoublePressWindowMilliseconds == 350,
        _ => true
    };

    private static bool TryParseMediaCommand(string value, out MediaControlCommand command)
    {
        command = value.Trim() switch
        {
            "播放 / 暂停" => MediaControlCommand.PlayPause,
            "停止" => MediaControlCommand.Stop,
            "上一首" => MediaControlCommand.PreviousTrack,
            "下一首" => MediaControlCommand.NextTrack,
            _ => (MediaControlCommand)(-1)
        };
        return Enum.IsDefined(command);
    }

    private static string FormatMediaCommand(MediaControlCommand command) => command switch
    {
        MediaControlCommand.PlayPause => "播放 / 暂停",
        MediaControlCommand.Stop => "停止",
        MediaControlCommand.PreviousTrack => "上一首",
        MediaControlCommand.NextTrack => "下一首",
        _ => command.ToString()
    };

    private static bool TryParseVolumeAction(string value, out VolumeControlAction? action)
    {
        action = value.Trim() switch
        {
            "音量增加" => new VolumeControlAction { Command = VolumeControlCommand.Increase },
            "音量降低" => new VolumeControlAction { Command = VolumeControlCommand.Decrease },
            "静音切换" => new VolumeControlAction { Command = VolumeControlCommand.ToggleMute },
            _ => null
        };
        if (action is not null)
        {
            return true;
        }

        var match = Regex.Match(value.Trim(), @"^设置音量\s+(\d{1,3})%?$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var level) || level is < 0 or > 100)
        {
            return false;
        }

        action = new VolumeControlAction
        {
            Command = VolumeControlCommand.SetLevelPercent,
            LevelPercent = level
        };
        return true;
    }

    private static bool TryFormatVolumeAction(VolumeControlAction action, out string value)
    {
        value = action.Command switch
        {
            VolumeControlCommand.Increase when action.LevelPercent is null => "音量增加",
            VolumeControlCommand.Decrease when action.LevelPercent is null => "音量降低",
            VolumeControlCommand.ToggleMute when action.LevelPercent is null => "静音切换",
            VolumeControlCommand.SetLevelPercent when action.LevelPercent is >= 0 and <= 100 =>
                $"设置音量 {action.LevelPercent}%",
            _ => string.Empty
        };
        return value.Length > 0;
    }

    private static string DescribePassthrough(InputMapping mapping)
    {
        var action = mapping.Action;
        var actionDescription = action switch
        {
            MacroAction macro => $"宏 · {macro.Steps.Count} 步",
            DelayAction delay => $"延迟 · {delay.Milliseconds}ms",
            LaunchProgramAction => "应用启动（含高级参数）",
            RunScriptAction => "脚本（含高级参数）",
            SendKeyAction => "按键动作（含高级选项）",
            ShortcutAction => "快捷键（含高级选项）",
            CopyInputToOutputAction => "复制的按键输出（含高级选项）",
            MediaControlAction => "媒体控制（未知命令）",
            VolumeControlAction => "音量控制（未知参数）",
            OpenUriAction => "非 HTTP(S) 协议",
            _ => action.GetType().Name
        };
        var triggerDescription = mapping.Trigger.Kind switch
        {
            MappingTriggerKind.LongPress when mapping.Trigger.LongPressMilliseconds != 600 =>
                $" · 长按 {mapping.Trigger.LongPressMilliseconds}ms",
            MappingTriggerKind.DoublePress when mapping.Trigger.DoublePressWindowMilliseconds != 350 =>
                $" · 双击窗口 {mapping.Trigger.DoublePressWindowMilliseconds}ms",
            _ => string.Empty
        };
        return actionDescription + triggerDescription;
    }
}
