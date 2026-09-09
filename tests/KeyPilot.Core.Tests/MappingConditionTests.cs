using System.Text.Json.Nodes;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Serialization;
using KeyPilot.Core.Validation;

internal static class MappingConditionTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
        new (string Name, Action Run)[]
        {
            ("生效范围：默认始终生效且历史为空", DefaultIsAlwaysWithEmptyHistory),
            ("生效范围：前台匹配与排除", ForegroundIsAndIsNotMatch),
            ("生效范围：运行中集合匹配", AppRunningMatchesForegroundOrBackground),
            ("生效范围：带包名的 UWP 不会撞 ApplicationFrameHost", PackagedAppsDoNotCollideOnHostProcess),
            ("生效范围：空列表对非始终条件失败关闭", RestrictedEmptyListFailsClosed),
            ("生效范围：未命中时只禁用映射", ArmingDisablesUnmatchedMappings),
            ("生效范围：最近历史去重并封顶", RecentHistoryDedupesAndCaps),
            ("生效范围：条件可以 JSON 往返", ConditionRoundTripsThroughJson),
            ("生效范围：schema 6 安全迁移到 schema 7", SchemaSixMigratesWithAlways),
            ("生效范围：验证器拒绝缺少软件的限制条件", ValidatorRejectsRestrictedWithoutApps),
            ("生效范围：验证器拒绝重复软件", ValidatorRejectsDuplicateIdentities)
        };

    private static void DefaultIsAlwaysWithEmptyHistory()
    {
        var configuration = KeyPilotConfiguration.CreateDefault();

        AssertEqual(KeyPilotConfiguration.CurrentSchemaVersion, configuration.SchemaVersion, "schema");
        AssertEqual(0, configuration.KnownApplications.Count, "known");
        AssertEqual(0, configuration.RecentForegroundApplications.Count, "recent");
        AssertNoErrors(ConfigurationValidator.Validate(configuration));

        var mapping = CreateMapping("中键");
        AssertEqual(MappingConditionKind.Always, mapping.Condition.Kind, "default kind");
        AssertEqual("始终生效", mapping.Condition.Summary, "summary");
        Assert(
            MappingConditionMatcher.Matches(mapping.Condition, new MappingApplicationContext()),
            "Always 必须在任意上下文中命中。");
    }

    private static void ForegroundIsAndIsNotMatch()
    {
        var photoshop = new ApplicationIdentity { ProcessName = "Photoshop.exe", DisplayName = "Photoshop" };
        var foregroundIs = new MappingCondition
        {
            Kind = MappingConditionKind.ForegroundIs,
            Applications = { photoshop }
        };
        var foregroundIsNot = new MappingCondition
        {
            Kind = MappingConditionKind.ForegroundIsNot,
            Applications = { photoshop }
        };
        var photoshopContext = new MappingApplicationContext { ForegroundProcessName = "photoshop" };
        var chromeContext = new MappingApplicationContext { ForegroundProcessName = "chrome" };
        var unknownContext = new MappingApplicationContext();

        Assert(MappingConditionMatcher.Matches(foregroundIs, photoshopContext), "前台命中");
        Assert(!MappingConditionMatcher.Matches(foregroundIs, chromeContext), "其他前台不应命中");
        Assert(!MappingConditionMatcher.Matches(foregroundIs, unknownContext), "未知前台应对 ForegroundIs 失败关闭");
        Assert(!MappingConditionMatcher.Matches(foregroundIsNot, photoshopContext), "排除条件不应命中自身");
        Assert(MappingConditionMatcher.Matches(foregroundIsNot, chromeContext), "排除条件应命中其他前台");
        Assert(MappingConditionMatcher.Matches(foregroundIsNot, unknownContext), "未知前台应对 ForegroundIsNot 视为非目标软件");
        AssertEqual("仅 Photoshop 前台", foregroundIs.Summary, "foreground summary");
    }

    private static void AppRunningMatchesForegroundOrBackground()
    {
        var chrome = new ApplicationIdentity { ProcessName = "chrome" };
        var condition = new MappingCondition
        {
            Kind = MappingConditionKind.AppRunning,
            Applications = { chrome }
        };

        Assert(
            MappingConditionMatcher.Matches(
                condition,
                new MappingApplicationContext { ForegroundProcessName = "chrome" }),
            "前台也算在运行");
        Assert(
            MappingConditionMatcher.Matches(
                condition,
                new MappingApplicationContext
                {
                    ForegroundProcessName = "photoshop",
                    RunningProcessNames = ["explorer", "CHROME"]
                }),
            "后台运行应命中");
        Assert(
            !MappingConditionMatcher.Matches(
                condition,
                new MappingApplicationContext
                {
                    ForegroundProcessName = "photoshop",
                    RunningProcessNames = ["explorer"]
                }),
            "未运行不应命中");
        Assert(
            MappingConditionMatcher.Matches(
                condition,
                new MappingApplicationContext
                {
                    RunningPackageFamilyNames = [],
                    RunningProcessNames = ["chrome.exe"]
                }),
            "带 .exe 的运行中名称应规范化");
    }

    private static void PackagedAppsDoNotCollideOnHostProcess()
    {
        var calculator = new ApplicationIdentity
        {
            ProcessName = "ApplicationFrameHost",
            DisplayName = "Calculator",
            PackageFamilyName = "Microsoft.WindowsCalculator_8wekyb3d8bbwe"
        };
        var photosContext = new MappingApplicationContext
        {
            ForegroundProcessName = "ApplicationFrameHost",
            ForegroundPackageFamilyName = "Microsoft.Windows.Photos_8wekyb3d8bbwe",
            RunningProcessNames = ["ApplicationFrameHost"],
            RunningPackageFamilyNames = ["Microsoft.Windows.Photos_8wekyb3d8bbwe"]
        };
        var calculatorContext = new MappingApplicationContext
        {
            ForegroundProcessName = "chrome",
            RunningProcessNames = ["ApplicationFrameHost", "chrome"],
            RunningPackageFamilyNames = ["Microsoft.WindowsCalculator_8wekyb3d8bbwe"]
        };

        var foregroundIs = new MappingCondition
        {
            Kind = MappingConditionKind.ForegroundIs,
            Applications = { calculator }
        };
        var running = new MappingCondition
        {
            Kind = MappingConditionKind.AppRunning,
            Applications = { calculator }
        };

        Assert(!MappingConditionMatcher.Matches(foregroundIs, photosContext),
            "不同包族的 ApplicationFrameHost 前台不能命中");
        Assert(!MappingConditionMatcher.Matches(running, photosContext),
            "其他 UWP 在运行不能算计算器在运行");
        Assert(MappingConditionMatcher.Matches(running, calculatorContext),
            "计算器包族在运行时应命中");
        Assert(
            !new ApplicationIdentity { ProcessName = "ApplicationFrameHost" }
                .Matches("ApplicationFrameHost", packageFamilyName: null),
            "没有包族的 ApplicationFrameHost 不能当匹配键");
    }

    private static void RestrictedEmptyListFailsClosed()
    {
        var empty = new MappingCondition { Kind = MappingConditionKind.ForegroundIs };
        Assert(
            !MappingConditionMatcher.Matches(
                empty,
                new MappingApplicationContext { ForegroundProcessName = "chrome" }),
            "空软件列表必须失败关闭");
    }

    private static void ArmingDisablesUnmatchedMappings()
    {
        var always = CreateMapping("始终");
        var gated = CreateMapping("仅PS") with
        {
            Condition = new MappingCondition
            {
                Kind = MappingConditionKind.ForegroundIs,
                Applications = { new ApplicationIdentity { ProcessName = "Photoshop" } }
            }
        };
        var profile = new MappingProfile
        {
            Name = "混合",
            Mappings = { always, gated }
        };
        var configuration = new KeyPilotConfiguration
        {
            ActiveProfileId = profile.Id,
            Profiles = { profile }
        };

        var armed = MappingConditionMatcher.Arm(
            configuration,
            new MappingApplicationContext { ForegroundProcessName = "chrome" });
        var armedProfile = armed.Profiles.Single();
        Assert(armedProfile.Mappings[0].IsEnabled, "始终生效的映射必须保持启用");
        Assert(!armedProfile.Mappings[1].IsEnabled, "未命中条件的映射必须被运行时禁用");
        Assert(gated.IsEnabled, "持久化映射不得被 Arm 改写");
    }

    private static void RecentHistoryDedupesAndCaps()
    {
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        IReadOnlyList<RecentApplicationEntry> history = [];
        for (var index = 0; index < RecentApplicationCatalog.MaximumRecentEntries + 5; index++)
        {
            history = RecentApplicationCatalog.RememberForeground(
                history,
                new ApplicationIdentity { ProcessName = $"app{index}", DisplayName = $"App {index}" },
                now.AddMinutes(index));
        }

        AssertEqual(RecentApplicationCatalog.MaximumRecentEntries, history.Count, "cap");
        AssertEqual("app34", history[0].Application.ProcessName, "newest first");
        AssertEqual("App 34", history[0].Application.DisplayName, "display");

        history = RecentApplicationCatalog.RememberForeground(
            history,
            new ApplicationIdentity { ProcessName = "app34.exe" },
            now.AddHours(1));
        AssertEqual(RecentApplicationCatalog.MaximumRecentEntries, history.Count, "dedupe cap");
        AssertEqual("app34", history[0].Application.ProcessName, "moved to front");
        AssertEqual("App 34", history[0].Application.DisplayName, "keep richer name");
    }

    private static void ConditionRoundTripsThroughJson()
    {
        var mapping = CreateMapping("中键撤销") with
        {
            Condition = new MappingCondition
            {
                Kind = MappingConditionKind.ForegroundIs,
                Applications =
                {
                    new ApplicationIdentity
                    {
                        ProcessName = "Photoshop.exe",
                        DisplayName = "Photoshop"
                    }
                }
            }
        };
        var profile = new MappingProfile { Name = "条件", Mappings = { mapping } };
        var configuration = new KeyPilotConfiguration
        {
            ActiveProfileId = profile.Id,
            Profiles = { profile },
            KnownApplications = { mapping.Condition.Applications[0] },
            RecentForegroundApplications =
            {
                new RecentApplicationEntry
                {
                    Application = mapping.Condition.Applications[0],
                    LastForegroundUtc = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero)
                }
            }
        };

        var json = KeyPilotJson.Serialize(configuration);
        var restored = KeyPilotJson.Deserialize(json);

        Assert(json.Contains("\"kind\": \"foregroundIs\"", StringComparison.Ordinal), "JSON 缺少条件类型");
        var restoredMapping = restored.Profiles.Single().Mappings.Single();
        AssertEqual(MappingConditionKind.ForegroundIs, restoredMapping.Condition.Kind, "kind");
        AssertEqual("Photoshop", restoredMapping.Condition.Applications.Single().NormalizedProcessName, "process");
        AssertEqual("Photoshop", restored.KnownApplications.Single().DisplayName, "known");
        AssertEqual(1, restored.RecentForegroundApplications.Count, "recent");
    }

    private static void SchemaSixMigratesWithAlways()
    {
        var currentJson = KeyPilotJson.Serialize(KeyPilotConfiguration.CreateDefault("legacy condition"));
        var legacy = JsonNode.Parse(currentJson)?.AsObject()
            ?? throw new InvalidOperationException("Current configuration JSON was not an object.");
        legacy["schemaVersion"] = 6;
        legacy.Remove("knownApplications");
        legacy.Remove("recentForegroundApplications");
        var mappings = legacy["profiles"]?[0]?["mappings"]?.AsArray();
        mappings?.Clear();

        var migrated = KeyPilotJson.Deserialize(legacy.ToJsonString());

        AssertEqual(KeyPilotConfiguration.CurrentSchemaVersion, migrated.SchemaVersion, "migrated schema");
        AssertEqual(0, migrated.KnownApplications.Count, "known default");
        AssertEqual(0, migrated.RecentForegroundApplications.Count, "recent default");
        AssertNoErrors(ConfigurationValidator.Validate(migrated));
    }

    private static void ValidatorRejectsRestrictedWithoutApps()
    {
        var configuration = KeyPilotConfiguration.CreateDefault();
        configuration.Profiles.Single().Mappings.Add(CreateMapping("缺软件") with
        {
            Condition = new MappingCondition { Kind = MappingConditionKind.AppRunning }
        });

        var issues = ConfigurationValidator.Validate(configuration);
        Assert(
            issues.Any(issue => issue.Code == "condition.applications.required" &&
                issue.Severity == ValidationSeverity.Error),
            "验证器未拒绝缺少软件的限制条件。");
    }

    private static void ValidatorRejectsDuplicateIdentities()
    {
        var chrome = new ApplicationIdentity { ProcessName = "chrome" };
        var configuration = KeyPilotConfiguration.CreateDefault() with
        {
            KnownApplications = [chrome, new ApplicationIdentity { ProcessName = "chrome.exe" }]
        };

        var issues = ConfigurationValidator.Validate(configuration);
        Assert(
            issues.Any(issue => issue.Code == "knownApplication.duplicate" &&
                issue.Severity == ValidationSeverity.Error),
            "验证器未拒绝重复的已知软件。");
    }

    private static InputMapping CreateMapping(string name) => new()
    {
        Name = name,
        Source = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Keyboard,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.KeyboardScanCode,
                Code = 0x1E
            }
        },
        Action = new SendKeyAction
        {
            Target = new InputControlId
            {
                Kind = InputControlKind.KeyboardScanCode,
                Code = 0x20
            }
        }
    };

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
            throw new InvalidOperationException($"{description}: expected {expected}, actual {actual}.");
        }
    }
}
