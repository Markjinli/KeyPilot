using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Triggers;

namespace KeyPilot.Core.Diagnostics;

/// <summary>How KeyPilot treated the original physical input.</summary>
public enum InputProcessDisposition
{
    /// <summary>Mapping master is off; the event was only observed.</summary>
    CaptureOnly,

    /// <summary>Mapping is on, but this input has no enabled mapping.</summary>
    PassThrough,

    /// <summary>Original input was swallowed and replaced.</summary>
    InterceptAndRewrite,

    /// <summary>Original cannot be swallowed; a mapped action was still injected.</summary>
    CannotInterceptOverlay,

    /// <summary>Original could have been swallowed, but the mapping kept it and also injected.</summary>
    KeepOriginalOverlay,

    /// <summary>Mapping asked to swallow the original, but this device cannot; the mapping was not run.</summary>
    CannotInterceptRefused
}

public enum InputProcessResultKind
{
    None,
    Success,
    Overlay,
    Refused,
    Failed,
    Pending,
    Unmapped
}

public enum InputProcessLogFilter
{
    All,
    Rewritten,
    Uninterceptable
}

/// <summary>One observed physical edge and the mapping decision taken for it.</summary>
public sealed class InputProcessLogEntry
{
    public Guid Id { get; init; }

    public DateTimeOffset TimestampLocal { get; init; }

    public InputEventPhase Phase { get; init; }

    public InputDeviceKind DeviceKind { get; init; }

    public string DeviceLabel { get; init; } = string.Empty;

    public string KeyLabel { get; init; } = string.Empty;

    public string SourceKey { get; init; } = string.Empty;

    /// <summary>Device family plus control identity; ignores exact vs any-device match mode.</summary>
    public string CorrelationKey { get; init; } = string.Empty;

    public InputProcessDisposition Disposition { get; init; }

    public string DispositionLabel { get; init; } = string.Empty;

    public string ActionSummary { get; init; } = string.Empty;

    public InputProcessResultKind ResultKind { get; init; }

    public string ResultLabel { get; init; } = string.Empty;

    /// <summary>1 for the original edge; extra typematic repeats increment this when collapsed.</summary>
    public int RepeatCount { get; set; } = 1;
}

/// <summary>Pure classification of a physical input against the active mapping configuration.</summary>
public static class InputProcessLogClassifier
{
    public static bool CanSuppressOriginal(InputSource? source) =>
        source is
        {
            Device.Kind: InputDeviceKind.Keyboard,
            Control.Kind: InputControlKind.KeyboardScanCode
        };

    public static InputMapping? FindEnabledMapping(
        KeyPilotConfiguration configuration,
        InputSource source)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(source);
        if (!configuration.IsMappingEnabled)
        {
            return null;
        }

        var profile = configuration.ActiveProfileId is Guid activeId
            ? configuration.Profiles.FirstOrDefault(candidate => candidate.Id == activeId)
            : null;
        profile ??= configuration.Profiles.FirstOrDefault(candidate => candidate.IsEnabled);
        if (profile?.IsEnabled != true)
        {
            return null;
        }

        return profile.Mappings.FirstOrDefault(mapping =>
            mapping is { IsEnabled: true } &&
            MappingTriggerStateMachine.MatchesSource(mapping.Source, source));
    }

    public static InputProcessDisposition Classify(
        KeyPilotConfiguration configuration,
        InputSource source,
        bool originalWasSuppressed)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(source);
        if (!configuration.IsMappingEnabled)
        {
            return InputProcessDisposition.CaptureOnly;
        }

        var mapping = FindEnabledMapping(configuration, source);
        if (mapping is null)
        {
            return InputProcessDisposition.PassThrough;
        }

        var canSuppress = CanSuppressOriginal(source);
        if (mapping.SuppressOriginal)
        {
            return originalWasSuppressed && canSuppress
                ? InputProcessDisposition.InterceptAndRewrite
                : InputProcessDisposition.CannotInterceptRefused;
        }

        return canSuppress
            ? InputProcessDisposition.KeepOriginalOverlay
            : InputProcessDisposition.CannotInterceptOverlay;
    }

    public static InputProcessLogEntry CreateEntry(
        InputEvent inputEvent,
        string keyLabel,
        KeyPilotConfiguration configuration,
        bool originalWasSuppressed,
        DateTimeOffset localTimestamp,
        Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        ArgumentNullException.ThrowIfNull(inputEvent.Source);
        ArgumentNullException.ThrowIfNull(configuration);

        var mapping = FindEnabledMapping(configuration, inputEvent.Source);
        var disposition = Classify(configuration, inputEvent.Source, originalWasSuppressed);
        var resultKind = ResultKind(disposition, mapping, inputEvent.Phase);
        return new InputProcessLogEntry
        {
            Id = id ?? Guid.NewGuid(),
            TimestampLocal = localTimestamp,
            Phase = inputEvent.Phase,
            DeviceKind = inputEvent.Source.Device.Kind,
            DeviceLabel = DeviceLabel(inputEvent.Source.Device.Kind),
            KeyLabel = string.IsNullOrWhiteSpace(keyLabel) ? "未知" : keyLabel.Trim(),
            SourceKey = inputEvent.Source.CanonicalKey,
            CorrelationKey = CorrelationKey(inputEvent.Source),
            Disposition = disposition,
            DispositionLabel = DispositionLabel(disposition),
            ActionSummary = ActionSummary(mapping, disposition),
            ResultKind = resultKind,
            ResultLabel = ResultLabel(resultKind)
        };
    }

    public static string CorrelationKey(InputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return $"{source.Device.Kind}/{source.Control.CanonicalKey}";
    }

    public static string DeviceLabel(InputDeviceKind kind) => kind switch
    {
        InputDeviceKind.Keyboard => "键盘",
        InputDeviceKind.Mouse => "鼠标",
        InputDeviceKind.Gamepad => "手柄",
        InputDeviceKind.Hid => "HID",
        InputDeviceKind.ConsumerControl => "媒体",
        InputDeviceKind.Composite => "组合",
        _ => "输入"
    };

    public static string PhaseGlyph(InputEventPhase phase) => phase switch
    {
        InputEventPhase.Pressed => "↓",
        InputEventPhase.Released => "↑",
        _ => "⇉"
    };

    public static string DispositionLabel(InputProcessDisposition disposition) => disposition switch
    {
        InputProcessDisposition.InterceptAndRewrite => "拦截并改写",
        InputProcessDisposition.CannotInterceptOverlay => "放行并叠加",
        InputProcessDisposition.KeepOriginalOverlay => "放行并叠加",
        InputProcessDisposition.CannotInterceptRefused => "无法拦截",
        InputProcessDisposition.CaptureOnly => "仅采集",
        _ => "放行"
    };

    public static bool MatchesFilter(InputProcessLogEntry entry, InputProcessLogFilter filter) =>
        filter switch
        {
            InputProcessLogFilter.Rewritten =>
                entry.Disposition == InputProcessDisposition.InterceptAndRewrite,
            InputProcessLogFilter.Uninterceptable =>
                entry.Disposition is InputProcessDisposition.CannotInterceptOverlay
                    or InputProcessDisposition.CannotInterceptRefused,
            _ => true
        };

    public static string FormatRepeatSuffix(int repeatCount) =>
        repeatCount > 1 ? $" ×{repeatCount}" : string.Empty;

    internal static string ActionSummary(InputMapping? mapping, InputProcessDisposition disposition)
    {
        if (mapping is null ||
            disposition is InputProcessDisposition.PassThrough or InputProcessDisposition.CaptureOnly)
        {
            return "—";
        }

        var named = DescribeMappingName(mapping.Name);
        if (!string.IsNullOrWhiteSpace(named))
        {
            return named;
        }

        return DescribeAction(mapping.Action);
    }

    internal static string DescribeAction(MappingAction? action) => action switch
    {
        ShortcutAction shortcut when shortcut.Keys.Count > 0 =>
            $"模拟 {shortcut.Keys.Count} 键快捷键",
        SendKeyAction => "模拟按键",
        OpenUriAction uri when !string.IsNullOrWhiteSpace(uri.Uri) => $"打开 {uri.Uri}",
        LaunchProgramAction launch when !string.IsNullOrWhiteSpace(launch.FilePath) =>
            $"启动 {System.IO.Path.GetFileName(launch.FilePath)}",
        RunScriptAction script when !string.IsNullOrWhiteSpace(script.ScriptPath) =>
            $"运行 {System.IO.Path.GetFileName(script.ScriptPath)}",
        MediaControlAction media => $"媒体 {media.Command}",
        VolumeControlAction volume => $"音量 {volume.Command}",
        EmitSpecialKeyAction special => $"特殊键 {special.SlotNumber}",
        CopyInputToOutputAction => "复制到输出",
        MacroAction => "宏",
        _ => "映射动作"
    };

    private static string? DescribeMappingName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var separator = name.IndexOf('→');
        if (separator < 0)
        {
            separator = name.IndexOf("->", StringComparison.Ordinal);
            if (separator < 0)
            {
                return name.Trim();
            }

            var legacy = name[(separator + 2)..].Trim();
            return string.IsNullOrWhiteSpace(legacy) ? name.Trim() : legacy;
        }

        var target = name[(separator + 1)..].Trim();
        return string.IsNullOrWhiteSpace(target) ? name.Trim() : target;
    }

    private static InputProcessResultKind ResultKind(
        InputProcessDisposition disposition,
        InputMapping? mapping,
        InputEventPhase phase) =>
        disposition switch
        {
            InputProcessDisposition.InterceptAndRewrite
                when mapping?.Trigger.Kind == MappingTriggerKind.SinglePress
                     && phase != InputEventPhase.Released =>
                InputProcessResultKind.Pending,
            InputProcessDisposition.InterceptAndRewrite => InputProcessResultKind.Success,
            InputProcessDisposition.CannotInterceptOverlay => InputProcessResultKind.Overlay,
            InputProcessDisposition.KeepOriginalOverlay => InputProcessResultKind.Overlay,
            InputProcessDisposition.CannotInterceptRefused => InputProcessResultKind.Refused,
            InputProcessDisposition.PassThrough => InputProcessResultKind.Unmapped,
            _ => InputProcessResultKind.None
        };

    private static string ResultLabel(InputProcessResultKind kind) => kind switch
    {
        InputProcessResultKind.Success => "已执行",
        InputProcessResultKind.Pending => "待触发",
        InputProcessResultKind.Overlay => "已叠加",
        InputProcessResultKind.Refused => "已拒绝",
        InputProcessResultKind.Unmapped => "无映射",
        InputProcessResultKind.Failed => "失败",
        _ => "—"
    };
}

/// <summary>In-memory ring of recent input-process rows. Not persisted.</summary>
public sealed class InputProcessLog
{
    public const int Capacity = 800;
    public static readonly TimeSpan DedupWindow = TimeSpan.FromMilliseconds(40);

    private readonly List<InputProcessLogEntry> _entries = [];
    private readonly object _gate = new();

    public bool CollapseRepeats { get; set; } = true;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public InputProcessLogEntry? TryRecord(
        InputEvent inputEvent,
        string keyLabel,
        KeyPilotConfiguration configuration,
        bool originalWasSuppressed,
        DateTimeOffset localTimestamp)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        ArgumentNullException.ThrowIfNull(inputEvent.Source);
        ArgumentNullException.ThrowIfNull(configuration);
        if (inputEvent.IsInjected)
        {
            return null;
        }

        lock (_gate)
        {
            var correlationKey = InputProcessLogClassifier.CorrelationKey(inputEvent.Source);
            if (_entries.Count > 0)
            {
                var last = _entries[^1];
                if (string.Equals(last.CorrelationKey, correlationKey, StringComparison.Ordinal)
                    && last.Phase == inputEvent.Phase
                    && Abs(localTimestamp - last.TimestampLocal) <= DedupWindow)
                {
                    var incoming = InputProcessLogClassifier.Classify(
                        configuration,
                        inputEvent.Source,
                        originalWasSuppressed);
                    if (DispositionRank(incoming) <= DispositionRank(last.Disposition))
                    {
                        return null;
                    }

                    var upgraded = InputProcessLogClassifier.CreateEntry(
                        inputEvent,
                        keyLabel,
                        configuration,
                        originalWasSuppressed,
                        last.TimestampLocal,
                        last.Id);
                    upgraded.RepeatCount = last.RepeatCount;
                    _entries[^1] = upgraded;
                    return upgraded;
                }

                if (CollapseRepeats
                    && inputEvent.Phase == InputEventPhase.Repeated
                    && string.Equals(last.CorrelationKey, correlationKey, StringComparison.Ordinal)
                    && last.Phase is InputEventPhase.Pressed or InputEventPhase.Repeated)
                {
                    last.RepeatCount++;
                    return last;
                }
            }

            var entry = InputProcessLogClassifier.CreateEntry(
                inputEvent,
                keyLabel,
                configuration,
                originalWasSuppressed,
                localTimestamp);
            _entries.Add(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
            }

            return entry;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public IReadOnlyList<InputProcessLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public InputProcessLogEntry? Latest()
    {
        lock (_gate)
        {
            return _entries.Count == 0 ? null : _entries[^1];
        }
    }

    private static int DispositionRank(InputProcessDisposition disposition) => disposition switch
    {
        InputProcessDisposition.InterceptAndRewrite => 4,
        InputProcessDisposition.KeepOriginalOverlay => 3,
        InputProcessDisposition.CannotInterceptOverlay => 3,
        InputProcessDisposition.CannotInterceptRefused => 2,
        _ => 1
    };

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
}
