using System.Collections.Concurrent;
using System.Globalization;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.DriverClient;

namespace KeyPilot.App.Presentation;

internal enum DriverRuleIssueKind
{
    ExactDeviceResolutionFailed,
    UnsupportedSource,
    AmbiguousScanPrefix,
    ProtocolRejected,
    RuleLimitExceeded
}

internal sealed record DriverRuleIssue(
    DriverRuleIssueKind Kind,
    Guid MappingId,
    string MappingName,
    string Message);

internal sealed record DriverRuleBinding(
    KeyboardRule Rule,
    InputSource Source,
    IReadOnlyList<InputMapping> Mappings);

internal sealed record DriverRuleSetBuildResult(
    IReadOnlyList<KeyboardRule> Rules,
    IReadOnlyDictionary<ulong, DriverRuleBinding> Bindings,
    IReadOnlyList<DriverRuleIssue> Issues)
{
    public static DriverRuleSetBuildResult Empty { get; } = new(
        Array.Empty<KeyboardRule>(),
        new Dictionary<ulong, DriverRuleBinding>(),
        Array.Empty<DriverRuleIssue>());
}

/// <summary>
/// Projects an enabled Core profile onto protocol-v2 keyboard suppression rules. Exact-device
/// resolution is fail-closed: a failed path lookup is reported and is never widened to all keyboards.
/// </summary>
internal static class DriverSuppressionRuleBuilder
{
    private const ushort PrefixMask = 0x0006;
    private static readonly ConcurrentDictionary<string, byte[]> DeviceHashes =
        new(StringComparer.OrdinalIgnoreCase);

    public static DriverRuleSetBuildResult Build(
        KeyPilotConfiguration configuration,
        Func<string, byte[]>? deviceHashResolver = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.IsMappingEnabled)
        {
            return DriverRuleSetBuildResult.Empty;
        }

        var profile = configuration.ActiveProfileId is Guid activeId
            ? configuration.Profiles.FirstOrDefault(candidate => candidate.Id == activeId)
            : null;
        profile ??= configuration.Profiles.FirstOrDefault(candidate => candidate.IsEnabled);
        if (profile?.IsEnabled != true)
        {
            return DriverRuleSetBuildResult.Empty;
        }

        var rules = new List<KeyboardRule>();
        var bindings = new Dictionary<ulong, DriverRuleBinding>();
        var predicates = new Dictionary<
            (ushort MakeCode, KeyboardScanPrefix Prefix, string DeviceHash),
            ulong>();
        var issues = new List<DriverRuleIssue>();

        foreach (var mapping in profile.Mappings)
        {
            if (mapping is null || !mapping.IsEnabled || !mapping.SuppressOriginal)
            {
                continue;
            }

            var source = mapping.Source;
            if (source?.Device is null || source.Control is null ||
                source.Device.Kind != InputDeviceKind.Keyboard ||
                source.Control.Kind != InputControlKind.KeyboardScanCode)
            {
                AddIssue(
                    issues,
                    DriverRuleIssueKind.UnsupportedSource,
                    mapping,
                    "内核驱动当前只能抑制键盘扫描码；此来源不会被错误扩大。\n");
                continue;
            }

            if (source.Device.MatchMode is not (DeviceMatchMode.AnyOfKind or DeviceMatchMode.ExactDevice) ||
                source.Control.Code is <= 0 or > ushort.MaxValue ||
                !TryResolvePrefix(source.Control, out var prefix))
            {
                AddIssue(
                    issues,
                    DriverRuleIssueKind.AmbiguousScanPrefix,
                    mapping,
                    "扫描码或 RAWKEYBOARD 前缀不能精确表示为 Normal、E0 或 E1。");
                continue;
            }

            var makeCode = checked((ushort)source.Control.Code);
            var exactDeviceHash = ResolveExactDeviceHash(
                source,
                mapping,
                issues,
                deviceHashResolver);
            if (source.Device.MatchMode == DeviceMatchMode.ExactDevice && exactDeviceHash is null)
            {
                continue;
            }

            var devicePredicate = exactDeviceHash is null
                ? "*"
                : Convert.ToHexString(exactDeviceHash);
            var predicate = (makeCode, prefix, devicePredicate);
            if (predicates.TryGetValue(predicate, out var existingRuleId))
            {
                var existing = bindings[existingRuleId];
                bindings[existingRuleId] = existing with
                {
                    Mappings = existing.Mappings.Append(mapping).ToArray()
                };
                continue;
            }

            if (rules.Count >= KeyPilotDriverClient.MaximumRules)
            {
                AddIssue(
                    issues,
                    DriverRuleIssueKind.RuleLimitExceeded,
                    mapping,
                    $"内核协议最多接受 {KeyPilotDriverClient.MaximumRules} 条规则。");
                continue;
            }

            var ruleId = checked((ulong)rules.Count + 1UL);
            var rule = exactDeviceHash is null
                ? KeyboardRule.ForEveryKeyboard(makeCode, prefix, ruleId)
                : KeyboardRule.ForDevice(exactDeviceHash, makeCode, prefix, ruleId);
            try
            {
                // Validate the whole candidate set to catch wildcard/exact overlap and protected
                // emergency/SAS keys before a lease or live policy is modified.
                KeyPilotDriverClient.ValidateRules(rules.Append(rule).ToArray());
            }
            catch (ArgumentException exception)
            {
                AddIssue(
                    issues,
                    DriverRuleIssueKind.ProtocolRejected,
                    mapping,
                    $"内核协议拒绝此规则（可能重叠，或属于紧急旁路/SAS 路径）：{exception.Message}");
                continue;
            }

            rules.Add(rule);
            predicates.Add(predicate, ruleId);
            bindings.Add(ruleId, new DriverRuleBinding(rule, source, new[] { mapping }));
        }

        KeyPilotDriverClient.ValidateRules(rules);
        return new DriverRuleSetBuildResult(
            rules.ToArray(),
            new Dictionary<ulong, DriverRuleBinding>(bindings),
            issues.ToArray());
    }

    internal static bool TryResolvePrefix(
        InputControlId control,
        out KeyboardScanPrefix prefix)
    {
        ArgumentNullException.ThrowIfNull(control);
        prefix = default;
        if (string.IsNullOrWhiteSpace(control.RawQualifier))
        {
            return false;
        }

        var values = control.RawQualifier
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.StartsWith("PREFIX=", StringComparison.OrdinalIgnoreCase))
            .Select(part => part["PREFIX=".Length..])
            .ToArray();
        if (values.Length != 1 || values[0].Length != 4 ||
            !ushort.TryParse(
                values[0],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var raw) ||
            (raw & ~PrefixMask) != 0)
        {
            return false;
        }

        prefix = raw switch
        {
            0x0000 => KeyboardScanPrefix.None,
            0x0002 => KeyboardScanPrefix.E0,
            0x0004 => KeyboardScanPrefix.E1,
            _ => (KeyboardScanPrefix)(-1)
        };
        return Enum.IsDefined(prefix) &&
            control.IsExtended == (prefix != KeyboardScanPrefix.None);
    }

    internal static bool RuleMatchesSource(KeyboardRule rule, InputSource source)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Device?.Kind != InputDeviceKind.Keyboard ||
            source.Control?.Kind != InputControlKind.KeyboardScanCode ||
            source.Control.Code != rule.MakeCode ||
            !TryResolvePrefix(source.Control, out var prefix) ||
            !PrefixMatchesRule(prefix, rule))
        {
            return false;
        }

        if (IsZeroHash(rule.DeviceHash.Span))
        {
            return true;
        }

        if (source.Device.MatchMode != DeviceMatchMode.ExactDevice ||
            string.IsNullOrWhiteSpace(source.Device.DeviceId))
        {
            return false;
        }

        try
        {
            var hash = DeviceHashes.GetOrAdd(
                source.Device.DeviceId,
                static path => KeyPilotDeviceHash.ResolveDeviceInterfaceHash(path));
            return hash.AsSpan().SequenceEqual(rule.DeviceHash.Span);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return false;
        }
    }

    internal static bool EventMatchesRule(DriverInputEvent input, KeyboardRule rule) =>
        input.MakeCode == rule.MakeCode &&
        (input.Flags & rule.RequiredFlags) == rule.RequiredFlags &&
        (input.Flags & rule.IgnoredFlags) == 0 &&
        (IsZeroHash(rule.DeviceHash.Span) ||
            input.DeviceHash is { Length: KeyPilotDriverClient.DeviceHashLength } &&
            input.DeviceHash.AsSpan().SequenceEqual(rule.DeviceHash.Span));

    private static byte[]? ResolveExactDeviceHash(
        InputSource source,
        InputMapping mapping,
        ICollection<DriverRuleIssue> issues,
        Func<string, byte[]>? deviceHashResolver)
    {
        if (source.Device.MatchMode != DeviceMatchMode.ExactDevice)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(source.Device.DeviceId))
        {
            AddIssue(
                issues,
                DriverRuleIssueKind.ExactDeviceResolutionFailed,
                mapping,
                "精确设备映射缺少 Raw Input 设备接口路径；已拒绝且不会扩大为所有键盘。");
            return null;
        }

        try
        {
            var resolver = deviceHashResolver ?? KeyPilotDeviceHash.ResolveDeviceInterfaceHash;
            var resolved = resolver(source.Device.DeviceId);
            if (resolved is null ||
                resolved.Length != KeyPilotDriverClient.DeviceHashLength ||
                resolved.All(value => value == 0))
            {
                throw new InvalidDataException("Resolved device hash is invalid.");
            }

            var retained = (byte[])resolved.Clone();
            DeviceHashes[source.Device.DeviceId] = retained;
            return retained;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            AddIssue(
                issues,
                DriverRuleIssueKind.ExactDeviceResolutionFailed,
                mapping,
                $"无法解析精确键盘的设备实例哈希；已拒绝且不会扩大为所有键盘：{exception.Message}");
            return null;
        }
    }

    private static bool PrefixMatchesRule(KeyboardScanPrefix prefix, KeyboardRule rule)
    {
        var flags = prefix switch
        {
            KeyboardScanPrefix.None => (ushort)0,
            KeyboardScanPrefix.E0 => (ushort)0x0002,
            KeyboardScanPrefix.E1 => (ushort)0x0004,
            _ => ushort.MaxValue
        };
        return flags != ushort.MaxValue &&
            (flags & rule.RequiredFlags) == rule.RequiredFlags &&
            (flags & rule.IgnoredFlags) == 0;
    }

    private static bool IsZeroHash(ReadOnlySpan<byte> hash)
    {
        foreach (var value in hash)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddIssue(
        ICollection<DriverRuleIssue> issues,
        DriverRuleIssueKind kind,
        InputMapping mapping,
        string message) =>
        issues.Add(new DriverRuleIssue(kind, mapping.Id, mapping.Name, message.Trim()));
}
