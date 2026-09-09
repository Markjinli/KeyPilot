using System.Text.Json;
using System.Text.Json.Serialization;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Validation;

namespace KeyPilot.Core.Serialization;

public static class KeyPilotJson
{
    private static readonly JsonSerializerOptions BaseOptions = CreateOptions();

    public static string Serialize(KeyPilotConfiguration configuration, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ConfigurationValidator.ValidateAndThrow(configuration);

        var options = new JsonSerializerOptions(BaseOptions)
        {
            WriteIndented = indented
        };
        return JsonSerializer.Serialize(configuration, options);
    }

    public static KeyPilotConfiguration Deserialize(string json, bool validate = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var configuration = JsonSerializer.Deserialize<KeyPilotConfiguration>(json, BaseOptions)
            ?? throw new JsonException("JSON did not contain a KeyPilot configuration.");

        // Schemas 1 and 2 contain only atomic, chord and rotation sources. Schema 3 adds the
        // optional ordered PatternSteps property, schema 4 adds opt-in application profile
        // bindings, schema 5 adds opt-in per-profile stick mouse settings, schema 6 adds the
        // workbench appearance theme id, and schema 7 adds per-mapping application conditions
        // plus remembered application identities. All added properties have lossless, safe
        // defaults for older documents.
        if (configuration.SchemaVersion is >= 1 and < KeyPilotConfiguration.CurrentSchemaVersion)
        {
            configuration = configuration with
            {
                SchemaVersion = KeyPilotConfiguration.CurrentSchemaVersion
            };
        }

        if (string.IsNullOrWhiteSpace(configuration.AppearanceThemeId))
        {
            configuration = configuration with
            {
                AppearanceThemeId = AppearanceThemeCatalog.DefaultId
            };
        }

        if (configuration.KnownApplications is null)
        {
            configuration = configuration with { KnownApplications = [] };
        }

        if (configuration.RecentForegroundApplications is null)
        {
            configuration = configuration with { RecentForegroundApplications = [] };
        }

        if (validate)
        {
            ConfigurationValidator.ValidateAndThrow(configuration);
        }

        return configuration;
    }

    public static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
    };
}
