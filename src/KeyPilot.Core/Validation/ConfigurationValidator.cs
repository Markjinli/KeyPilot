using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;

namespace KeyPilot.Core.Validation;

public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Code,
    string Path,
    string Message);

public sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(IReadOnlyList<ValidationIssue> issues)
        : base(BuildMessage(issues))
    {
        Issues = issues;
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    private static string BuildMessage(IReadOnlyList<ValidationIssue> issues) =>
        "KeyPilot configuration is invalid: " + string.Join(
            "; ",
            issues.Where(issue => issue.Severity == ValidationSeverity.Error)
                .Select(issue => $"{issue.Path}: {issue.Message}"));
}

public static class ConfigurationValidator
{
    private const int MaximumActionDepth = 8;
    private const int MaximumMacroSteps = 256;
    private const int MaximumDelayMilliseconds = 600_000;
    private const int MaximumHoldMilliseconds = 10_000;
    private const int MaximumRawQualifierLength = 4_096;
    private const int MinimumChordMembers = 2;
    private const int MaximumChordMembers = 8;
    private const int MinimumPatternSteps = 2;
    private const int MaximumPatternSteps = InputPatternCaptureSession.MaximumEdgeCount;
    private const int MaximumPatternOffsetMilliseconds = 2_000;
    private const int MinimumLongPressMilliseconds = 100;
    private const int MaximumLongPressMilliseconds = 10_000;
    private const int MinimumDoublePressWindowMilliseconds = 100;
    private const int MaximumDoublePressWindowMilliseconds = 2_000;

    public static IReadOnlyList<ValidationIssue> Validate(KeyPilotConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issues = new List<ValidationIssue>();
        var slots = ValidateSlots(configuration.SpecialKeySlots, issues);
        ValidateProfiles(configuration, slots, issues);
        ValidateApplicationProfileBindings(configuration, issues);
        ValidateKnownApplications(configuration, issues);
        ValidateRecentForegroundApplications(configuration, issues);
        ValidateAppearanceTheme(configuration, issues);
        return issues;
    }

    public static void ValidateAndThrow(KeyPilotConfiguration configuration)
    {
        var issues = Validate(configuration);
        if (issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            throw new ConfigurationValidationException(issues);
        }
    }

    private static Dictionary<int, SpecialKeySlot> ValidateSlots(
        IReadOnlyList<SpecialKeySlot>? slots,
        ICollection<ValidationIssue> issues)
    {
        var validSlots = new Dictionary<int, SpecialKeySlot>();
        if (slots is null)
        {
            AddError(issues, "slots.null", "$.specialKeySlots", "特殊键槽位列表不能为空。");
            return validSlots;
        }

        for (var index = 0; index < slots.Count; index++)
        {
            var path = $"$.specialKeySlots[{index}]";
            var slot = slots[index];
            if (slot is null)
            {
                AddError(issues, "slot.null", path, "特殊键槽位不能为空。");
                continue;
            }

            if (slot.Number is < SpecialKeySlots.Minimum or > SpecialKeySlots.Maximum)
            {
                AddError(issues, "slot.number.range", $"{path}.number", "槽位编号必须介于 1 和 10 之间。");
            }
            else if (!validSlots.TryAdd(slot.Number, slot))
            {
                AddError(issues, "slot.number.duplicate", $"{path}.number", "槽位编号不能重复。");
            }

            if (string.IsNullOrWhiteSpace(slot.DisplayName))
            {
                AddError(issues, "slot.name.required", $"{path}.displayName", "槽位名称不能为空。");
            }

            if (slot.Source is not null)
            {
                ValidateSource(slot.Source, $"{path}.source", issues);
            }
        }

        foreach (var number in Enumerable.Range(SpecialKeySlots.Minimum, SpecialKeySlots.Count))
        {
            if (!validSlots.ContainsKey(number))
            {
                AddError(issues, "slot.number.missing", "$.specialKeySlots", $"缺少特殊键槽位 {number}。");
            }
        }

        return validSlots;
    }

    private static void ValidateProfiles(
        KeyPilotConfiguration configuration,
        IReadOnlyDictionary<int, SpecialKeySlot> slots,
        ICollection<ValidationIssue> issues)
    {
        if (configuration.SchemaVersion != KeyPilotConfiguration.CurrentSchemaVersion)
        {
            AddError(
                issues,
                "schema.unsupported",
                "$.schemaVersion",
                $"仅支持配置版本 {KeyPilotConfiguration.CurrentSchemaVersion}。");
        }

        if (configuration.Profiles is null || configuration.Profiles.Count == 0)
        {
            AddError(issues, "profiles.required", "$.profiles", "至少需要一个映射方案。");
            return;
        }

        var profileIds = new HashSet<Guid>();
        for (var profileIndex = 0; profileIndex < configuration.Profiles.Count; profileIndex++)
        {
            var profilePath = $"$.profiles[{profileIndex}]";
            var profile = configuration.Profiles[profileIndex];
            if (profile is null)
            {
                AddError(issues, "profile.null", profilePath, "映射方案不能为空。");
                continue;
            }

            if (profile.Id == Guid.Empty)
            {
                AddError(issues, "profile.id.empty", $"{profilePath}.id", "映射方案 ID 不能为空。");
            }
            else if (!profileIds.Add(profile.Id))
            {
                AddError(issues, "profile.id.duplicate", $"{profilePath}.id", "映射方案 ID 不能重复。");
            }

            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                AddError(issues, "profile.name.required", $"{profilePath}.name", "映射方案名称不能为空。");
            }

            ValidateStickMouse(profile.StickMouse, profilePath, issues);
            ValidateMappings(profile.Mappings, profilePath, slots, issues);
        }

        if (configuration.ActiveProfileId is Guid activeId && !profileIds.Contains(activeId))
        {
            AddError(issues, "profile.active.missing", "$.activeProfileId", "当前映射方案不存在。");
        }
    }

    private static void ValidateStickMouse(
        StickMouseSettings? settings,
        string profilePath,
        ICollection<ValidationIssue> issues)
    {
        var path = $"{profilePath}.stickMouse";
        if (settings is null)
        {
            AddError(issues, "profile.stickMouse.null", path, "摇杆鼠标设置不能为空。");
            return;
        }

        if (!Enum.IsDefined(settings.Source))
        {
            AddError(
                issues,
                "profile.stickMouse.source.invalid",
                $"{path}.source",
                "摇杆鼠标来源必须是左摇杆或右摇杆。");
        }

        if (settings.SpeedPixelsPerSecond is
            < StickMouseSettings.MinimumSpeedPixelsPerSecond or
            > StickMouseSettings.MaximumSpeedPixelsPerSecond)
        {
            AddError(
                issues,
                "profile.stickMouse.speed.range",
                $"{path}.speedPixelsPerSecond",
                $"摇杆鼠标速度必须介于 {StickMouseSettings.MinimumSpeedPixelsPerSecond} 和 {StickMouseSettings.MaximumSpeedPixelsPerSecond} 像素/秒之间。");
        }

        if (!double.IsFinite(settings.Deadzone) ||
            settings.Deadzone is
                < StickMouseSettings.MinimumDeadzone or
                > StickMouseSettings.MaximumDeadzone)
        {
            AddError(
                issues,
                "profile.stickMouse.deadzone.range",
                $"{path}.deadzone",
                $"摇杆鼠标死区必须介于 {StickMouseSettings.MinimumDeadzone:P0} 和 {StickMouseSettings.MaximumDeadzone:P0} 之间。");
        }
    }

    private static void ValidateMappings(
        IReadOnlyList<InputMapping>? mappings,
        string profilePath,
        IReadOnlyDictionary<int, SpecialKeySlot> slots,
        ICollection<ValidationIssue> issues)
    {
        if (mappings is null)
        {
            AddError(issues, "mappings.null", $"{profilePath}.mappings", "映射列表不能为空。");
            return;
        }

        var mappingIds = new HashSet<Guid>();
        var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var mappingIndex = 0; mappingIndex < mappings.Count; mappingIndex++)
        {
            var path = $"{profilePath}.mappings[{mappingIndex}]";
            var mapping = mappings[mappingIndex];
            if (mapping is null)
            {
                AddError(issues, "mapping.null", path, "映射不能为空。");
                continue;
            }

            if (mapping.Id == Guid.Empty)
            {
                AddError(issues, "mapping.id.empty", $"{path}.id", "映射 ID 不能为空。");
            }
            else if (!mappingIds.Add(mapping.Id))
            {
                AddError(issues, "mapping.id.duplicate", $"{path}.id", "映射 ID 不能重复。");
            }

            if (string.IsNullOrWhiteSpace(mapping.Name))
            {
                AddWarning(issues, "mapping.name.empty", $"{path}.name", "建议为映射命名，便于识别。");
            }

            ValidateSource(mapping.Source, $"{path}.source", issues);
            if (mapping.SuppressOriginal &&
                mapping.Source?.Control?.Kind is InputControlKind.InputChord
                    or InputControlKind.InputSequence
                    or InputControlKind.GamepadRotation)
            {
                AddError(
                    issues,
                    "mapping.suppression.unsupportedLogicalSource",
                    $"{path}.suppressOriginal",
                    "组合、序列和摇杆整圈手势是逻辑输入，不能安全屏蔽其原始成员。");
            }

            if (mapping.Source is not null
                && mapping.Source.Device is not null
                && mapping.Source.Control is not null
                && !sourcePaths.TryAdd(mapping.Source.CanonicalKey, path))
            {
                AddError(
                    issues,
                    "mapping.source.duplicate",
                    $"{path}.source",
                    $"同一方案内的输入源不能重复；它已用于 {sourcePaths[mapping.Source.CanonicalKey]}。");
            }

            ValidateTrigger(mapping.Trigger, $"{path}.trigger", issues);
            ValidateCondition(mapping.Condition, $"{path}.condition", issues);

            ValidateAction(
                mapping.Action,
                $"{path}.action",
                slots,
                issues,
                depth: 0,
                new HashSet<MappingAction>(ReferenceEqualityComparer.Instance));
        }
    }

    private static void ValidateApplicationProfileBindings(
        KeyPilotConfiguration configuration,
        ICollection<ValidationIssue> issues)
    {
        if (configuration.ApplicationProfileBindings is null)
        {
            AddError(
                issues,
                "applicationProfileBindings.null",
                "$.applicationProfileBindings",
                "应用与方案绑定列表不能为空。");
            return;
        }

        var profileIds = configuration.Profiles?
            .Where(profile => profile is not null && profile.Id != Guid.Empty)
            .Select(profile => profile.Id)
            .ToHashSet() ?? new HashSet<Guid>();
        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < configuration.ApplicationProfileBindings.Count; index++)
        {
            var path = $"$.applicationProfileBindings[{index}]";
            var binding = configuration.ApplicationProfileBindings[index];
            if (binding is null)
            {
                AddError(issues, "applicationProfileBinding.null", path, "应用与方案绑定不能为空。");
                continue;
            }

            var normalizedProcessName =
                ApplicationProfileResolver.NormalizeProcessName(binding.ProcessName);
            if (normalizedProcessName.Length == 0)
            {
                AddError(
                    issues,
                    "applicationProfileBinding.processName.required",
                    $"{path}.processName",
                    "进程名称不能为空。");
            }
            else if (!processNames.Add(normalizedProcessName))
            {
                AddError(
                    issues,
                    "applicationProfileBinding.processName.duplicate",
                    $"{path}.processName",
                    "同一进程只能绑定一个映射方案。");
            }

            if (binding.ProfileId == Guid.Empty)
            {
                AddError(
                    issues,
                    "applicationProfileBinding.profileId.empty",
                    $"{path}.profileId",
                    "绑定的映射方案 ID 不能为空。");
            }
            else if (!profileIds.Contains(binding.ProfileId))
            {
                AddError(
                    issues,
                    "applicationProfileBinding.profile.missing",
                    $"{path}.profileId",
                    "绑定的映射方案不存在。");
            }
        }
    }

    private static void ValidateCondition(
        MappingCondition? condition,
        string path,
        ICollection<ValidationIssue> issues)
    {
        if (condition is null)
        {
            AddError(issues, "condition.null", path, "生效范围不能为空。");
            return;
        }

        if (!Enum.IsDefined(condition.Kind))
        {
            AddError(issues, "condition.kind.invalid", $"{path}.kind", "生效范围类型不受支持。");
            return;
        }

        if (condition.Applications is null)
        {
            AddError(issues, "condition.applications.null", $"{path}.applications", "生效范围的软件列表不能为空。");
            return;
        }

        if (condition.Kind == MappingConditionKind.Always)
        {
            return;
        }

        if (condition.Applications.Count == 0)
        {
            AddError(
                issues,
                "condition.applications.required",
                $"{path}.applications",
                "限制生效范围时必须选择至少一个软件。");
            return;
        }

        if (condition.Applications.Count > MappingCondition.MaximumApplications)
        {
            AddError(
                issues,
                "condition.applications.count",
                $"{path}.applications",
                $"一条映射最多指定 {MappingCondition.MaximumApplications} 个软件。");
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < condition.Applications.Count; index++)
        {
            ValidateIdentity(
                condition.Applications[index],
                $"{path}.applications[{index}]",
                "condition.application",
                keys,
                issues);
        }
    }

    private static void ValidateKnownApplications(
        KeyPilotConfiguration configuration,
        ICollection<ValidationIssue> issues)
    {
        if (configuration.KnownApplications is null)
        {
            AddError(issues, "knownApplications.null", "$.knownApplications", "已知软件列表不能为空。");
            return;
        }

        if (configuration.KnownApplications.Count > RecentApplicationCatalog.MaximumKnownApplications)
        {
            AddError(
                issues,
                "knownApplications.count",
                "$.knownApplications",
                $"已知软件不能超过 {RecentApplicationCatalog.MaximumKnownApplications} 个。");
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < configuration.KnownApplications.Count; index++)
        {
            ValidateIdentity(
                configuration.KnownApplications[index],
                $"$.knownApplications[{index}]",
                "knownApplication",
                keys,
                issues);
        }
    }

    private static void ValidateRecentForegroundApplications(
        KeyPilotConfiguration configuration,
        ICollection<ValidationIssue> issues)
    {
        if (configuration.RecentForegroundApplications is null)
        {
            AddError(
                issues,
                "recentForegroundApplications.null",
                "$.recentForegroundApplications",
                "最近前台软件列表不能为空。");
            return;
        }

        if (configuration.RecentForegroundApplications.Count > RecentApplicationCatalog.MaximumRecentEntries)
        {
            AddError(
                issues,
                "recentForegroundApplications.count",
                "$.recentForegroundApplications",
                $"最近前台软件不能超过 {RecentApplicationCatalog.MaximumRecentEntries} 个。");
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < configuration.RecentForegroundApplications.Count; index++)
        {
            var path = $"$.recentForegroundApplications[{index}]";
            var entry = configuration.RecentForegroundApplications[index];
            if (entry is null)
            {
                AddError(issues, "recentForegroundApplication.null", path, "最近前台软件记录不能为空。");
                continue;
            }

            ValidateIdentity(
                entry.Application,
                $"{path}.application",
                "recentForegroundApplication",
                keys,
                issues);
        }
    }

    private static void ValidateIdentity(
        ApplicationIdentity? identity,
        string path,
        string codePrefix,
        ISet<string> uniqueKeys,
        ICollection<ValidationIssue> issues)
    {
        if (identity is null)
        {
            AddError(issues, $"{codePrefix}.null", path, "软件标识不能为空。");
            return;
        }

        if (!identity.HasMatchKey)
        {
            AddError(
                issues,
                $"{codePrefix}.processName.required",
                $"{path}.processName",
                "进程名称不能为空。");
            return;
        }

        var key = identity.NormalizedPackageFamilyName.Length > 0
            ? $"pfn:{identity.NormalizedPackageFamilyName}"
            : $"exe:{identity.NormalizedProcessName}";
        if (!uniqueKeys.Add(key))
        {
            AddError(
                issues,
                $"{codePrefix}.duplicate",
                path,
                "同一软件不能重复出现。");
        }
    }

    private static void ValidateAppearanceTheme(
        KeyPilotConfiguration configuration,
        ICollection<ValidationIssue> issues)
    {
        var themeId = configuration.AppearanceThemeId;
        if (themeId is null)
        {
            AddError(
                issues,
                "appearanceTheme.id.required",
                "$.appearanceThemeId",
                "外观主题不能为空。");
            return;
        }

        if (themeId.Length > AppearanceThemeCatalog.MaximumIdLength)
        {
            AddError(
                issues,
                "appearanceTheme.id.length",
                "$.appearanceThemeId",
                $"外观主题标识不能超过 {AppearanceThemeCatalog.MaximumIdLength} 个字符。");
        }
    }

    private static void ValidateTrigger(
        MappingTrigger? trigger,
        string path,
        ICollection<ValidationIssue> issues)
    {
        if (trigger is null)
        {
            AddError(issues, "trigger.null", path, "触发条件不能为空。");
            return;
        }

        if (trigger.Kind == MappingTriggerKind.LongPress
            && trigger.LongPressMilliseconds is < MinimumLongPressMilliseconds or > MaximumLongPressMilliseconds)
        {
            AddError(
                issues,
                "trigger.longPress.range",
                $"{path}.longPressMilliseconds",
                $"长按阈值必须介于 {MinimumLongPressMilliseconds} 和 {MaximumLongPressMilliseconds} 毫秒之间。");
        }

        if (trigger.Kind == MappingTriggerKind.DoublePress
            && trigger.DoublePressWindowMilliseconds is < MinimumDoublePressWindowMilliseconds
                or > MaximumDoublePressWindowMilliseconds)
        {
            AddError(
                issues,
                "trigger.doublePress.range",
                $"{path}.doublePressWindowMilliseconds",
                $"双击间隔必须介于 {MinimumDoublePressWindowMilliseconds} 和 {MaximumDoublePressWindowMilliseconds} 毫秒之间。");
        }
    }

    private static void ValidateSource(
        InputSource? source,
        string path,
        ICollection<ValidationIssue> issues) => ValidateSource(
            source,
            path,
            issues,
            allowLogicalSource: true,
            new HashSet<InputSource>(ReferenceEqualityComparer.Instance));

    private static void ValidateSource(
        InputSource? source,
        string path,
        ICollection<ValidationIssue> issues,
        bool allowLogicalSource,
        ISet<InputSource> ancestors)
    {
        if (source is null)
        {
            AddError(issues, "source.null", path, "输入源不能为空。");
            return;
        }

        if (!ancestors.Add(source))
        {
            AddError(issues, "source.chord.cycle", path, "组合输入不能循环引用自身。");
            return;
        }

        var isChord = source.Control?.Kind == InputControlKind.InputChord;
        var isSequence = source.Control?.Kind == InputControlKind.InputSequence;
        var isLogicalSource = isChord || isSequence;

        if (source.Device is null)
        {
            AddError(issues, "device.null", $"{path}.device", "设备选择器不能为空。");
        }
        else
        {
            var device = source.Device;
            if (device.Kind == InputDeviceKind.Unknown)
            {
                AddWarning(issues, "device.kind.unknown", $"{path}.device.kind", "未知设备类型可能无法稳定匹配。");
            }

            if (device.MatchMode == DeviceMatchMode.ExactDevice
                && string.IsNullOrWhiteSpace(device.DeviceId)
                && !(device.VendorId.HasValue && device.ProductId.HasValue))
            {
                AddError(
                    issues,
                    "device.identity.required",
                    $"{path}.device",
                    "精确设备匹配需要 DeviceId，或同时提供 VID 和 PID。");
            }

            if (isLogicalSource && (device.Kind != InputDeviceKind.Composite ||
                                    device.MatchMode != DeviceMatchMode.AnyOfKind))
            {
                AddError(
                    issues,
                    isChord ? "source.chord.device" : "source.sequence.device",
                    $"{path}.device",
                    "组合或序列输入必须使用 Composite / AnyOfKind 逻辑设备。");
            }
            else if (!isLogicalSource && device.Kind == InputDeviceKind.Composite)
            {
                AddError(
                    issues,
                    "source.composite.requiresChord",
                    $"{path}.device.kind",
                    "Composite 设备只能用于组合或序列输入。");
            }
        }

        ValidateControl(source.Control, $"{path}.control", issues);

        if (!isLogicalSource)
        {
            if (source.ChordMembers is { Count: > 0 })
            {
                AddError(
                    issues,
                    "source.chordMembers.unexpected",
                    $"{path}.chordMembers",
                    "只有 InputChord 控制可以包含组合成员。");
            }

            if (source.PatternSteps is { Count: > 0 })
            {
                AddError(
                    issues,
                    "source.patternSteps.unexpected",
                    $"{path}.patternSteps",
                    "只有 InputSequence 控制可以包含模式边沿。");
            }

            ancestors.Remove(source);
            return;
        }

        if (!allowLogicalSource)
        {
            AddError(
                issues,
                isChord ? "source.chord.nested" : "source.sequence.nested",
                path,
                "组合或序列输入的成员不能再次嵌套逻辑输入。");
            ancestors.Remove(source);
            return;
        }

        if (isSequence)
        {
            ValidateSequenceSource(source, path, issues, ancestors);
            ancestors.Remove(source);
            return;
        }

        if (source.PatternSteps is { Count: > 0 })
        {
            AddError(
                issues,
                "source.patternSteps.unexpected",
                $"{path}.patternSteps",
                "InputChord 不能同时包含有序模式边沿。");
        }

        if (source.ChordMembers is null ||
            source.ChordMembers.Count is < MinimumChordMembers or > MaximumChordMembers)
        {
            AddError(
                issues,
                "source.chord.count",
                $"{path}.chordMembers",
                $"组合输入必须包含 {MinimumChordMembers} 至 {MaximumChordMembers} 个成员。");
            ancestors.Remove(source);
            return;
        }

        var uniqueMembers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < source.ChordMembers.Count; index++)
        {
            var member = source.ChordMembers[index];
            var memberPath = $"{path}.chordMembers[{index}]";
            ValidateSource(member, memberPath, issues, allowLogicalSource: false, ancestors);
            if (member is null || member.Device is null || member.Control is null ||
                member.Control.Kind is InputControlKind.InputChord or InputControlKind.InputSequence)
            {
                continue;
            }

            if (!uniqueMembers.Add(member.CanonicalKey))
            {
                AddError(
                    issues,
                    "source.chord.member.duplicate",
                    memberPath,
                    "组合输入不能重复包含同一个成员。");
            }
        }

        ancestors.Remove(source);
    }

    private static void ValidateSequenceSource(
        InputSource source,
        string path,
        ICollection<ValidationIssue> issues,
        ISet<InputSource> ancestors)
    {
        if (source.ChordMembers is { Count: > 0 })
        {
            AddError(
                issues,
                "source.chordMembers.unexpected",
                $"{path}.chordMembers",
                "InputSequence 不能同时包含无序组合成员。");
        }

        if (source.PatternSteps is null ||
            source.PatternSteps.Count is < MinimumPatternSteps or > MaximumPatternSteps)
        {
            AddError(
                issues,
                "source.sequence.count",
                $"{path}.patternSteps",
                $"序列输入必须包含 {MinimumPatternSteps} 至 {MaximumPatternSteps} 个边沿。");
            return;
        }

        var down = new HashSet<string>(StringComparer.Ordinal);
        var previousOffset = -1;
        for (var index = 0; index < source.PatternSteps.Count; index++)
        {
            var step = source.PatternSteps[index];
            var stepPath = $"{path}.patternSteps[{index}]";
            if (step is null)
            {
                AddError(issues, "source.sequence.step.null", stepPath, "序列边沿不能为空。");
                continue;
            }

            ValidateSource(
                step.Source,
                $"{stepPath}.source",
                issues,
                allowLogicalSource: false,
                ancestors);

            if (step.Phase == InputEventPhase.Repeated)
            {
                AddError(
                    issues,
                    "source.sequence.phase",
                    $"{stepPath}.phase",
                    "序列只能记录 Pressed 或 Released 边沿。");
            }

            if (step.OffsetMilliseconds is < 0 or > MaximumPatternOffsetMilliseconds)
            {
                AddError(
                    issues,
                    "source.sequence.offset.range",
                    $"{stepPath}.offsetMilliseconds",
                    $"序列边沿时间必须介于 0 和 {MaximumPatternOffsetMilliseconds} 毫秒之间。");
            }
            else if (index == 0 && step.OffsetMilliseconds != 0)
            {
                AddError(
                    issues,
                    "source.sequence.offset.first",
                    $"{stepPath}.offsetMilliseconds",
                    "首个序列边沿的相对时间必须为 0。");
            }
            else if (step.OffsetMilliseconds < previousOffset)
            {
                AddError(
                    issues,
                    "source.sequence.offset.order",
                    $"{stepPath}.offsetMilliseconds",
                    "序列边沿时间必须单调递增。");
            }

            previousOffset = Math.Max(previousOffset, step.OffsetMilliseconds);
            if (step.Source?.Device is null || step.Source.Control is null ||
                step.Source.Device.Kind == InputDeviceKind.Composite ||
                step.Source.Control.Kind is InputControlKind.InputChord or InputControlKind.InputSequence)
            {
                continue;
            }

            var sourceKey = step.Source.CanonicalKey;
            if (step.Phase == InputEventPhase.Pressed)
            {
                if (!down.Add(sourceKey))
                {
                    AddError(
                        issues,
                        "source.sequence.edge.duplicatePress",
                        stepPath,
                        "同一控制在释放前不能再次按下。");
                }
            }
            else if (step.Phase == InputEventPhase.Released && !down.Remove(sourceKey))
            {
                AddError(
                    issues,
                    "source.sequence.edge.unmatchedRelease",
                    stepPath,
                    "释放边沿必须对应此前的按下边沿。");
            }
        }

        if (down.Count > 0)
        {
            AddError(
                issues,
                "source.sequence.edge.unreleased",
                $"{path}.patternSteps",
                "序列结束前必须释放所有已按下的控制。");
        }
    }

    private static void ValidateControl(
        InputControlId? control,
        string path,
        ICollection<ValidationIssue> issues)
    {
        if (control is null)
        {
            AddError(issues, "control.null", path, "输入控制不能为空。");
            return;
        }

        switch (control.Kind)
        {
            case InputControlKind.KeyboardScanCode when control.Code is <= 0 or > 0xFFFF:
                AddError(issues, "control.scanCode.range", $"{path}.code", "键盘扫描码必须介于 1 和 65535 之间。");
                break;
            case InputControlKind.VirtualKey when control.Code is <= 0 or > 0xFF:
                AddError(issues, "control.virtualKey.range", $"{path}.code", "虚拟键码必须介于 1 和 255 之间。");
                break;
            case InputControlKind.HidUsage when control.UsagePage is null || control.Usage is null:
                AddError(issues, "control.hidUsage.required", path, "HID 控制必须同时提供 Usage Page 和 Usage。");
                break;
            case InputControlKind.GamepadButton when control.Code is < 0 or > ushort.MaxValue:
                AddError(issues, "control.gamepadButton.range", $"{path}.code", "手柄按钮编码必须介于 0 和 65535 之间。");
                break;
            case InputControlKind.GamepadAxisDirection when control.Code is < 1 or > 10:
                AddError(issues, "control.gamepadAxisDirection.range", $"{path}.code", "手柄模拟输入编码必须介于 1 和 10 之间。");
                break;
            case InputControlKind.GamepadRotation when control.Code is < 1 or > 4:
                AddError(issues, "control.gamepadRotation.range", $"{path}.code", "手柄旋转手势编码必须介于 1 和 4 之间。");
                break;
            case InputControlKind.InputChord when control.Code != 1:
                AddError(issues, "control.inputChord.code", $"{path}.code", "组合输入版本编码必须为 1。");
                break;
            case InputControlKind.InputSequence when control.Code != 1:
                AddError(issues, "control.inputSequence.code", $"{path}.code", "序列输入版本编码必须为 1。");
                break;
            case InputControlKind.RawCode when control.Code < 0:
                AddError(issues, "control.rawCode.range", $"{path}.code", "原始代码不能为负数。");
                break;
        }

        if (control.RawQualifier?.Length > MaximumRawQualifierLength)
        {
            AddError(
                issues,
                "control.rawQualifier.length",
                $"{path}.rawQualifier",
                $"原始 HID 限定信息不能超过 {MaximumRawQualifierLength} 个字符。");
        }

        if (control.ActivationQualifier?.Length > MaximumRawQualifierLength)
        {
            AddError(
                issues,
                "control.activationQualifier.length",
                $"{path}.activationQualifier",
                $"HID 激活方向信息不能超过 {MaximumRawQualifierLength} 个字符。");
        }
    }

    private static void ValidateAction(
        MappingAction? action,
        string path,
        IReadOnlyDictionary<int, SpecialKeySlot> slots,
        ICollection<ValidationIssue> issues,
        int depth,
        ISet<MappingAction> ancestors)
    {
        if (action is null)
        {
            AddError(issues, "action.null", path, "映射动作不能为空。");
            return;
        }

        if (depth > MaximumActionDepth)
        {
            AddError(issues, "action.depth", path, $"动作嵌套不能超过 {MaximumActionDepth} 层。");
            return;
        }

        if (!ancestors.Add(action))
        {
            AddError(issues, "action.cycle", path, "宏动作不能循环引用自身。");
            return;
        }

        switch (action)
        {
            case SendKeyAction sendKey:
                ValidateControl(sendKey.Target, $"{path}.target", issues);
                ValidateHold(sendKey.HoldMilliseconds, sendKey.Transition, path, issues);
                break;

            case ShortcutAction shortcut:
                if (shortcut.Keys is null || shortcut.Keys.Count is < 1 or > 8)
                {
                    AddError(issues, "shortcut.keys.count", $"{path}.keys", "快捷键必须包含 1 至 8 个按键。");
                }
                else
                {
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    for (var index = 0; index < shortcut.Keys.Count; index++)
                    {
                        var keyPath = $"{path}.keys[{index}]";
                        var key = shortcut.Keys[index];
                        ValidateControl(key, keyPath, issues);
                        if (key is not null && !keys.Add(key.CanonicalKey))
                        {
                            AddError(issues, "shortcut.key.duplicate", keyPath, "快捷键内不能重复同一个按键。");
                        }
                    }
                }

                ValidateHold(shortcut.HoldMilliseconds, KeyTransition.Press, path, issues);
                break;

            case CopyInputToOutputAction copied:
                if (!LogicalOutputTargetCodec.TryDecode(
                        copied.EncodedTarget,
                        out _,
                        out var copiedTargetError))
                {
                    AddError(
                        issues,
                        "copiedOutput.target.invalid",
                        $"{path}.encodedTarget",
                        $"复制的按键输出目标无效：{copiedTargetError}");
                }

                ValidateHold(copied.HoldMilliseconds, KeyTransition.Press, path, issues);
                break;

            case MediaControlAction media:
                if (!Enum.IsDefined(media.Command))
                {
                    AddError(
                        issues,
                        "media.command.invalid",
                        $"{path}.command",
                        "媒体控制命令不受支持。");
                }
                break;

            case VolumeControlAction volume:
                if (!Enum.IsDefined(volume.Command))
                {
                    AddError(
                        issues,
                        "volume.command.invalid",
                        $"{path}.command",
                        "音量控制命令不受支持。");
                }
                else if (volume.Command == VolumeControlCommand.SetLevelPercent)
                {
                    if (volume.LevelPercent is null or < 0 or > 100)
                    {
                        AddError(
                            issues,
                            "volume.level.range",
                            $"{path}.levelPercent",
                            "精确音量必须介于 0% 和 100% 之间。");
                    }
                }
                else if (volume.LevelPercent.HasValue)
                {
                    AddError(
                        issues,
                        "volume.level.unexpected",
                        $"{path}.levelPercent",
                        "只有“设置精确音量”命令可以包含音量百分比。");
                }
                break;

            case LaunchProgramAction launch:
                if (string.IsNullOrWhiteSpace(launch.FilePath))
                {
                    AddError(issues, "program.path.required", $"{path}.filePath", "程序路径不能为空。");
                }
                break;

            case OpenUriAction openUri:
                if (!System.Uri.TryCreate(openUri.Uri, UriKind.Absolute, out var parsedUri)
                    || parsedUri is null
                    || string.IsNullOrWhiteSpace(parsedUri.Scheme))
                {
                    AddError(issues, "uri.invalid", $"{path}.uri", "网址或协议地址必须是有效的绝对 URI。");
                }
                break;

            case RunScriptAction script:
                if (string.IsNullOrWhiteSpace(script.ScriptPath))
                {
                    AddError(issues, "script.path.required", $"{path}.scriptPath", "脚本路径不能为空。");
                }
                break;

            case EmitSpecialKeyAction special:
                if (!slots.TryGetValue(special.SlotNumber, out var slot))
                {
                    AddError(issues, "specialKey.slot.missing", $"{path}.slotNumber", "引用的特殊键槽位不存在。");
                }
                else if (slot.Source is null)
                {
                    AddError(issues, "specialKey.slot.unbound", $"{path}.slotNumber", "引用的特殊键槽位尚未采集输入。");
                }
                break;

            case DelayAction delay:
                if (delay.Milliseconds is < 0 or > MaximumDelayMilliseconds)
                {
                    AddError(
                        issues,
                        "delay.range",
                        $"{path}.milliseconds",
                        $"延迟必须介于 0 和 {MaximumDelayMilliseconds} 毫秒之间。");
                }
                break;

            case MacroAction macro:
                if (macro.Steps is null || macro.Steps.Count is < 1 or > MaximumMacroSteps)
                {
                    AddError(
                        issues,
                        "macro.steps.count",
                        $"{path}.steps",
                        $"宏必须包含 1 至 {MaximumMacroSteps} 个步骤。");
                }
                else
                {
                    for (var index = 0; index < macro.Steps.Count; index++)
                    {
                        ValidateAction(
                            macro.Steps[index],
                            $"{path}.steps[{index}]",
                            slots,
                            issues,
                            depth + 1,
                            ancestors);
                    }
                }
                break;

            default:
                AddError(issues, "action.unsupported", path, $"不支持动作类型 {action.GetType().Name}。");
                break;
        }

        ancestors.Remove(action);
    }

    private static void ValidateHold(
        int milliseconds,
        KeyTransition transition,
        string path,
        ICollection<ValidationIssue> issues)
    {
        if (transition == KeyTransition.Press
            && milliseconds is < 1 or > MaximumHoldMilliseconds)
        {
            AddError(
                issues,
                "key.hold.range",
                $"{path}.holdMilliseconds",
                $"按键保持时间必须介于 1 和 {MaximumHoldMilliseconds} 毫秒之间。");
        }
    }

    private static void AddError(
        ICollection<ValidationIssue> issues,
        string code,
        string path,
        string message) => issues.Add(new ValidationIssue(ValidationSeverity.Error, code, path, message));

    private static void AddWarning(
        ICollection<ValidationIssue> issues,
        string code,
        string path,
        string message) => issues.Add(new ValidationIssue(ValidationSeverity.Warning, code, path, message));
}
