using System.Text.Json.Nodes;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Serialization;
using KeyPilot.Core.Validation;

internal static class AppearanceThemeTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
        new (string Name, Action Run)[]
        {
            ("外观主题：默认夜航", DefaultThemeIsNightPilot),
            ("外观主题：目录标识唯一且色值有效", CatalogIdsAreUniqueAndColorsParse),
            ("外观主题：未知标识回退到默认", UnknownIdFallsBackToDefault),
            ("外观主题：设置可以 JSON 往返", ThemeIdRoundTripsThroughJson),
            ("外观主题：schema 5 安全迁移到 schema 6", SchemaFiveMigratesWithDefaultTheme),
            ("外观主题：验证器拒绝过长标识", ValidatorRejectsOversizedThemeId)
        };

    private static void DefaultThemeIsNightPilot()
    {
        var configuration = KeyPilotConfiguration.CreateDefault();

        AssertEqual(AppearanceThemeCatalog.DefaultId, configuration.AppearanceThemeId, "default theme");
        AssertEqual("夜航", AppearanceThemeCatalog.Resolve(configuration.AppearanceThemeId).DisplayName, "default name");
        AssertNoErrors(ConfigurationValidator.Validate(configuration));
    }

    private static void CatalogIdsAreUniqueAndColorsParse()
    {
        Assert(AppearanceThemeCatalog.All.Count >= 8, "主题数量不足，至少需要 8 套。");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in AppearanceThemeCatalog.All)
        {
            Assert(!string.IsNullOrWhiteSpace(theme.Id), "主题标识不能为空。");
            Assert(theme.Id.Length <= AppearanceThemeCatalog.MaximumIdLength, $"主题标识过长：{theme.Id}");
            Assert(ids.Add(theme.Id), $"主题标识重复：{theme.Id}");
            Assert(!string.IsNullOrWhiteSpace(theme.DisplayName), $"主题名称不能为空：{theme.Id}");
            AssertRgb(theme.Background, theme.Id, nameof(theme.Background));
            AssertRgb(theme.Sidebar, theme.Id, nameof(theme.Sidebar));
            AssertRgb(theme.Panel, theme.Id, nameof(theme.Panel));
            AssertRgb(theme.PanelSecondary, theme.Id, nameof(theme.PanelSecondary));
            AssertRgb(theme.Line, theme.Id, nameof(theme.Line));
            AssertRgb(theme.Text, theme.Id, nameof(theme.Text));
            AssertRgb(theme.Muted, theme.Id, nameof(theme.Muted));
            AssertRgb(theme.Dim, theme.Id, nameof(theme.Dim));
            AssertRgb(theme.Accent, theme.Id, nameof(theme.Accent));
            AssertRgb(theme.OnAccent, theme.Id, nameof(theme.OnAccent));
            AssertRgb(theme.OnAccentDeep, theme.Id, nameof(theme.OnAccentDeep));
            AssertRgb(theme.Blue, theme.Id, nameof(theme.Blue));
            AssertRgb(theme.BlueBright, theme.Id, nameof(theme.BlueBright));
            AssertRgb(theme.Amber, theme.Id, nameof(theme.Amber));
            AssertRgb(theme.AmberBright, theme.Id, nameof(theme.AmberBright));
            AssertRgb(theme.Red, theme.Id, nameof(theme.Red));
            AssertRgb(theme.Selection, theme.Id, nameof(theme.Selection));
        }

        AssertEqual(AppearanceThemeCatalog.DefaultId, AppearanceThemeCatalog.All[0].Id, "catalog default slot");
        Assert(AppearanceThemeCatalog.All.Count(theme => !theme.IsDark) >= 2, "至少需要两套浅色主题。");
    }

    private static void UnknownIdFallsBackToDefault()
    {
        AssertEqual(AppearanceThemeCatalog.DefaultId, AppearanceThemeCatalog.Resolve(null).Id, "null");
        AssertEqual(AppearanceThemeCatalog.DefaultId, AppearanceThemeCatalog.Resolve(" ").Id, "whitespace");
        AssertEqual(AppearanceThemeCatalog.DefaultId, AppearanceThemeCatalog.Resolve("not-a-theme").Id, "unknown");
        AssertEqual("mist", AppearanceThemeCatalog.Resolve("MIST").Id, "case-insensitive resolve");
    }

    private static void ThemeIdRoundTripsThroughJson()
    {
        var configuration = KeyPilotConfiguration.CreateDefault("主题") with
        {
            AppearanceThemeId = "ember"
        };

        var json = KeyPilotJson.Serialize(configuration);
        var restored = KeyPilotJson.Deserialize(json);

        Assert(json.Contains("\"appearanceThemeId\": \"ember\"", StringComparison.Ordinal), "JSON 缺少外观主题。");
        AssertEqual("ember", restored.AppearanceThemeId, "restored theme");
        AssertEqual(KeyPilotConfiguration.CurrentSchemaVersion, restored.SchemaVersion, "schema");
    }

    private static void SchemaFiveMigratesWithDefaultTheme()
    {
        var currentJson = KeyPilotJson.Serialize(KeyPilotConfiguration.CreateDefault("legacy theme"));
        var legacy = JsonNode.Parse(currentJson)?.AsObject()
            ?? throw new InvalidOperationException("Current configuration JSON was not an object.");
        legacy["schemaVersion"] = 5;
        legacy.Remove("appearanceThemeId");

        var migrated = KeyPilotJson.Deserialize(legacy.ToJsonString());

        AssertEqual(KeyPilotConfiguration.CurrentSchemaVersion, migrated.SchemaVersion, "migrated schema");
        AssertEqual(AppearanceThemeCatalog.DefaultId, migrated.AppearanceThemeId, "migrated theme");
    }

    private static void ValidatorRejectsOversizedThemeId()
    {
        var configuration = KeyPilotConfiguration.CreateDefault() with
        {
            AppearanceThemeId = new string('a', AppearanceThemeCatalog.MaximumIdLength + 1)
        };

        var issues = ConfigurationValidator.Validate(configuration);
        Assert(
            issues.Any(issue => issue.Code == "appearanceTheme.id.length" &&
                issue.Severity == ValidationSeverity.Error),
            "验证器未拒绝过长的外观主题标识。");
    }

    private static void AssertRgb(string hex, string themeId, string field)
    {
        Assert(
            AppearanceThemeCatalog.TryParseRgb(hex, out _, out _, out _),
            $"主题 {themeId} 的 {field} 不是 #RRGGBB：{hex}");
    }

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
