using System.Text.Json.Nodes;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Serialization;
using KeyPilot.Core.Validation;

internal static class ApplicationProfileTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
        new (string Name, Action Run)[]
        {
            ("应用方案：默认关闭自动切换", DefaultConfigurationDisablesAutomaticSwitching),
            ("应用方案：进程名规范化并回退默认方案", ResolverNormalizesAndFallsBack),
            ("应用方案：绑定设置可 JSON 往返", BindingsRoundTripThroughJson),
            ("应用方案：schema 3 安全迁移到当前版本", SchemaThreeMigratesWithSafeDefaults),
            ("应用方案：验证器拒绝重复和悬空绑定", ValidatorRejectsInvalidBindings),
            ("应用方案：验证器拒绝空绑定列表和元素", ValidatorRejectsNullBindings)
        };

    private static void DefaultConfigurationDisablesAutomaticSwitching()
    {
        var configuration = KeyPilotConfiguration.CreateDefault();

        Assert(!configuration.IsAutomaticProfileSwitchingEnabled, "自动切换必须默认关闭。");
        AssertEqual(0, configuration.ApplicationProfileBindings.Count, "default binding count");
        AssertEqual(
            configuration.ActiveProfileId,
            ApplicationProfileResolver.ResolveProfileId(configuration, "chrome"),
            "disabled fallback profile");
    }

    private static void ResolverNormalizesAndFallsBack()
    {
        var fallback = new MappingProfile { Name = "默认" };
        var chatGpt = new MappingProfile { Name = "ChatGPT" };
        var configuration = new KeyPilotConfiguration
        {
            ActiveProfileId = fallback.Id,
            IsAutomaticProfileSwitchingEnabled = true,
            Profiles = { fallback, chatGpt },
            ApplicationProfileBindings =
            {
                new ApplicationProfileBinding
                {
                    ProcessName = "ChatGPT.exe",
                    ProfileId = chatGpt.Id
                }
            }
        };

        AssertEqual(
            "chrome",
            ApplicationProfileResolver.NormalizeProcessName(
                @" C:\Program Files\Google\Chrome\chrome.EXE "),
            "normalized executable path");
        AssertEqual(
            chatGpt.Id,
            ApplicationProfileResolver.ResolveProfileId(
                configuration,
                @"C:\Program Files\WindowsApps\CHATGPT.EXE"),
            "case-insensitive bound profile");
        AssertEqual(
            fallback.Id,
            ApplicationProfileResolver.ResolveProfileId(configuration, "explorer.exe"),
            "unknown process fallback");
        AssertEqual(
            fallback.Id,
            ApplicationProfileResolver.ResolveProfileId(configuration, null),
            "unreadable process fallback");

        var disabled = configuration with { IsAutomaticProfileSwitchingEnabled = false };
        AssertEqual(
            fallback.Id,
            ApplicationProfileResolver.ResolveProfileId(disabled, "ChatGPT"),
            "disabled switching fallback");

        var invalidBinding = configuration with
        {
            ApplicationProfileBindings =
            [
                new ApplicationProfileBinding
                {
                    ProcessName = "missing",
                    ProfileId = Guid.NewGuid()
                }
            ]
        };
        AssertEqual(
            fallback.Id,
            ApplicationProfileResolver.ResolveProfileId(invalidBinding, "missing.exe"),
            "invalid binding fallback");
    }

    private static void BindingsRoundTripThroughJson()
    {
        var configuration = KeyPilotConfiguration.CreateDefault("默认");
        var chrome = new MappingProfile { Name = "Chrome" };
        configuration.Profiles.Add(chrome);
        configuration = configuration with
        {
            IsAutomaticProfileSwitchingEnabled = true,
            ApplicationProfileBindings =
            [
                new ApplicationProfileBinding
                {
                    ProcessName = "chrome.exe",
                    ProfileId = chrome.Id
                }
            ]
        };

        var json = KeyPilotJson.Serialize(configuration);
        var restored = KeyPilotJson.Deserialize(json);

        Assert(restored.IsAutomaticProfileSwitchingEnabled, "自动切换开关未完成 JSON 往返。");
        AssertEqual(1, restored.ApplicationProfileBindings.Count, "restored binding count");
        AssertEqual("chrome.exe", restored.ApplicationProfileBindings[0].ProcessName, "process name");
        AssertEqual(chrome.Id, restored.ApplicationProfileBindings[0].ProfileId, "bound profile ID");
    }

    private static void SchemaThreeMigratesWithSafeDefaults()
    {
        var currentJson = KeyPilotJson.Serialize(KeyPilotConfiguration.CreateDefault("schema 3"));
        var legacy = JsonNode.Parse(currentJson)?.AsObject()
            ?? throw new InvalidOperationException("Current configuration JSON was not an object.");
        legacy["schemaVersion"] = 3;
        legacy.Remove("isAutomaticProfileSwitchingEnabled");
        legacy.Remove("applicationProfileBindings");

        var migrated = KeyPilotJson.Deserialize(legacy.ToJsonString());

        AssertEqual(KeyPilotConfiguration.CurrentSchemaVersion, migrated.SchemaVersion, "migrated schema");
        Assert(!migrated.IsAutomaticProfileSwitchingEnabled, "旧配置迁移后不得自动启用切换。");
        AssertEqual(0, migrated.ApplicationProfileBindings.Count, "migrated binding count");
    }

    private static void ValidatorRejectsInvalidBindings()
    {
        var configuration = KeyPilotConfiguration.CreateDefault("默认");
        var target = new MappingProfile { Name = "目标" };
        configuration.Profiles.Add(target);
        configuration = configuration with
        {
            ApplicationProfileBindings =
            [
                new ApplicationProfileBinding { ProcessName = " ", ProfileId = target.Id },
                new ApplicationProfileBinding { ProcessName = "chrome", ProfileId = target.Id },
                new ApplicationProfileBinding
                {
                    ProcessName = @"C:\Apps\CHROME.EXE",
                    ProfileId = target.Id
                },
                new ApplicationProfileBinding
                {
                    ProcessName = "missing",
                    ProfileId = Guid.NewGuid()
                },
                new ApplicationProfileBinding { ProcessName = "empty", ProfileId = Guid.Empty }
            ]
        };

        var issues = ConfigurationValidator.Validate(configuration);

        AssertHasError(issues, "applicationProfileBinding.processName.required");
        AssertHasError(issues, "applicationProfileBinding.processName.duplicate");
        AssertHasError(issues, "applicationProfileBinding.profile.missing");
        AssertHasError(issues, "applicationProfileBinding.profileId.empty");
    }

    private static void ValidatorRejectsNullBindings()
    {
        var configuration = KeyPilotConfiguration.CreateDefault();
        var nullList = configuration with { ApplicationProfileBindings = null! };
        AssertHasError(
            ConfigurationValidator.Validate(nullList),
            "applicationProfileBindings.null");

        var nullElement = configuration with
        {
            ApplicationProfileBindings = [null!]
        };
        AssertHasError(
            ConfigurationValidator.Validate(nullElement),
            "applicationProfileBinding.null");
    }

    private static void AssertHasError(
        IReadOnlyList<ValidationIssue> issues,
        string code) =>
        Assert(
            issues.Any(issue =>
                issue.Code == code && issue.Severity == ValidationSeverity.Error),
            $"缺少预期验证错误：{code}。");

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
