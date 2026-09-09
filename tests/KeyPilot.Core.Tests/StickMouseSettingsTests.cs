using System.Text.Json.Nodes;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Serialization;
using KeyPilot.Core.Validation;

internal static class StickMouseSettingsTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
        new (string Name, Action Run)[]
        {
            ("摇杆鼠标：默认安全关闭", DefaultSettingsAreSafe),
            ("摇杆鼠标：设置可以 JSON 往返", SettingsRoundTripThroughJson),
            ("摇杆鼠标：schema 1-4 安全迁移到 schema 5", LegacySchemasMigrateWithSafeDefaults),
            ("摇杆鼠标：验证器接受边界值", ValidatorAcceptsBoundaryValues),
            ("摇杆鼠标：验证器拒绝无效来源", ValidatorRejectsInvalidSource),
            ("摇杆鼠标：验证器拒绝超范围速度", ValidatorRejectsOutOfRangeSpeed),
            ("摇杆鼠标：验证器拒绝超范围死区", ValidatorRejectsOutOfRangeDeadzone),
            ("摇杆鼠标：验证器拒绝空设置", ValidatorRejectsNullSettings)
        };

    private static void DefaultSettingsAreSafe()
    {
        var settings = KeyPilotConfiguration.CreateDefault().Profiles.Single().StickMouse;

        Assert(!settings.IsEnabled, "摇杆鼠标必须默认关闭。");
        AssertEqual(StickMouseSource.Left, settings.Source, "default source");
        AssertEqual(StickMouseSettings.DefaultSpeedPixelsPerSecond, settings.SpeedPixelsPerSecond, "default speed");
        AssertEqual(StickMouseSettings.DefaultDeadzone, settings.Deadzone, "default deadzone");
    }

    private static void SettingsRoundTripThroughJson()
    {
        var configuration = WithSettings(new StickMouseSettings
        {
            IsEnabled = true,
            Source = StickMouseSource.Right,
            SpeedPixelsPerSecond = 1_275,
            Deadzone = 0.18
        });

        var json = KeyPilotJson.Serialize(configuration);
        var restored = KeyPilotJson.Deserialize(json).Profiles.Single().StickMouse;

        Assert(json.Contains("\"stickMouse\"", StringComparison.Ordinal), "JSON 缺少摇杆鼠标设置。");
        Assert(restored.IsEnabled, "启用状态未完成 JSON 往返。");
        AssertEqual(StickMouseSource.Right, restored.Source, "restored source");
        AssertEqual(1_275, restored.SpeedPixelsPerSecond, "restored speed");
        AssertEqual(0.18, restored.Deadzone, "restored deadzone");
    }

    private static void LegacySchemasMigrateWithSafeDefaults()
    {
        var currentJson = KeyPilotJson.Serialize(KeyPilotConfiguration.CreateDefault("legacy"));

        foreach (var legacySchema in Enumerable.Range(1, 4))
        {
            var legacy = JsonNode.Parse(currentJson)?.AsObject()
                ?? throw new InvalidOperationException("Current configuration JSON was not an object.");
            legacy["schemaVersion"] = legacySchema;

            foreach (var profile in legacy["profiles"]?.AsArray() ?? [])
            {
                profile?.AsObject().Remove("stickMouse");
            }

            var migrated = KeyPilotJson.Deserialize(legacy.ToJsonString());
            var settings = migrated.Profiles.Single().StickMouse;

            AssertEqual(KeyPilotConfiguration.CurrentSchemaVersion, migrated.SchemaVersion, $"schema {legacySchema} migration");
            Assert(!settings.IsEnabled, $"schema {legacySchema} migration enabled the mouse unexpectedly");
            AssertEqual(StickMouseSource.Left, settings.Source, $"schema {legacySchema} default source");
            AssertEqual(StickMouseSettings.DefaultSpeedPixelsPerSecond, settings.SpeedPixelsPerSecond, $"schema {legacySchema} default speed");
            AssertEqual(StickMouseSettings.DefaultDeadzone, settings.Deadzone, $"schema {legacySchema} default deadzone");
        }
    }

    private static void ValidatorAcceptsBoundaryValues()
    {
        AssertNoErrors(ConfigurationValidator.Validate(WithSettings(new StickMouseSettings
        {
            Source = StickMouseSource.Left,
            SpeedPixelsPerSecond = StickMouseSettings.MinimumSpeedPixelsPerSecond,
            Deadzone = StickMouseSettings.MinimumDeadzone
        })));
        AssertNoErrors(ConfigurationValidator.Validate(WithSettings(new StickMouseSettings
        {
            Source = StickMouseSource.Right,
            SpeedPixelsPerSecond = StickMouseSettings.MaximumSpeedPixelsPerSecond,
            Deadzone = StickMouseSettings.MaximumDeadzone
        })));
    }

    private static void ValidatorRejectsInvalidSource()
    {
        var issues = ConfigurationValidator.Validate(WithSettings(new StickMouseSettings
        {
            Source = (StickMouseSource)99
        }));

        AssertHasError(issues, "profile.stickMouse.source.invalid");
    }

    private static void ValidatorRejectsOutOfRangeSpeed()
    {
        AssertHasError(
            ConfigurationValidator.Validate(WithSettings(new StickMouseSettings
            {
                SpeedPixelsPerSecond = StickMouseSettings.MinimumSpeedPixelsPerSecond - 1
            })),
            "profile.stickMouse.speed.range");
        AssertHasError(
            ConfigurationValidator.Validate(WithSettings(new StickMouseSettings
            {
                SpeedPixelsPerSecond = StickMouseSettings.MaximumSpeedPixelsPerSecond + 1
            })),
            "profile.stickMouse.speed.range");
    }

    private static void ValidatorRejectsOutOfRangeDeadzone()
    {
        foreach (var deadzone in new[]
                 {
                     StickMouseSettings.MinimumDeadzone - 0.01,
                     StickMouseSettings.MaximumDeadzone + 0.01,
                     double.NaN,
                     double.PositiveInfinity
                 })
        {
            AssertHasError(
                ConfigurationValidator.Validate(WithSettings(new StickMouseSettings
                {
                    Deadzone = deadzone
                })),
                "profile.stickMouse.deadzone.range");
        }
    }

    private static void ValidatorRejectsNullSettings()
    {
        var configuration = KeyPilotConfiguration.CreateDefault();
        configuration.Profiles[0] = configuration.Profiles[0] with { StickMouse = null! };

        AssertHasError(
            ConfigurationValidator.Validate(configuration),
            "profile.stickMouse.null");
    }

    private static KeyPilotConfiguration WithSettings(StickMouseSettings settings)
    {
        var configuration = KeyPilotConfiguration.CreateDefault();
        configuration.Profiles[0] = configuration.Profiles[0] with { StickMouse = settings };
        return configuration;
    }

    private static void AssertHasError(
        IReadOnlyList<ValidationIssue> issues,
        string code) =>
        Assert(
            issues.Any(issue =>
                issue.Code == code && issue.Severity == ValidationSeverity.Error),
            $"缺少预期验证错误：{code}。");

    private static void AssertNoErrors(IReadOnlyList<ValidationIssue> issues) =>
        Assert(
            !issues.Any(issue => issue.Severity == ValidationSeverity.Error),
            "配置包含意外的验证错误。");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}.");
        }
    }
}
