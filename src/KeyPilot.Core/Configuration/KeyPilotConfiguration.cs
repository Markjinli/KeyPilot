using KeyPilot.Core.Actions;
using KeyPilot.Core.Input;
using System.Text.Json.Serialization;

namespace KeyPilot.Core.Configuration;

public sealed record KeyPilotConfiguration
{
    public const int CurrentSchemaVersion = 7;

    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid? ActiveProfileId { get; set; }

    /// <summary>
    /// Explicit global safety switch. A missing value in an older configuration deserializes to
    /// false, so updating KeyPilot can never silently start executing mappings.
    /// </summary>
    public bool IsMappingEnabled { get; init; }

    /// <summary>
    /// Enables selection of the effective profile from the foreground process. The safe default is
    /// false so an older configuration cannot begin switching profiles after an upgrade.
    /// </summary>
    public bool IsAutomaticProfileSwitchingEnabled { get; init; }

    public List<MappingProfile> Profiles { get; init; } = new();

    public List<ApplicationProfileBinding> ApplicationProfileBindings { get; init; } = new();

    /// <summary>Reusable application identities shown in the software picker.</summary>
    public List<ApplicationIdentity> KnownApplications { get; init; } = new();

    /// <summary>Newest-first foreground applications recently observed outside KeyPilot.</summary>
    public List<RecentApplicationEntry> RecentForegroundApplications { get; init; } = new();

    public List<SpecialKeySlot> SpecialKeySlots { get; init; } =
        global::KeyPilot.Core.Input.SpecialKeySlots.CreateDefaults();

    /// <summary>
    /// Workbench appearance theme id. Unknown or empty values fall back to
    /// <see cref="AppearanceThemeCatalog.DefaultId"/> at display time.
    /// </summary>
    public string AppearanceThemeId { get; init; } = AppearanceThemeCatalog.DefaultId;

    public static KeyPilotConfiguration CreateDefault(string profileName = "默认方案")
    {
        var profile = new MappingProfile { Name = profileName };
        return new KeyPilotConfiguration
        {
            ActiveProfileId = profile.Id,
            Profiles = { profile }
        };
    }
}

public sealed record MappingProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;

    /// <summary>Optional, per-profile analog-stick mouse movement.</summary>
    public StickMouseSettings StickMouse { get; init; } = new();

    public List<InputMapping> Mappings { get; init; } = new();
}

public sealed record InputMapping
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;

    /// <summary>Whether the physical source must be swallowed after a successful match.</summary>
    public bool SuppressOriginal { get; init; } = true;

    public InputSource Source { get; init; } = new();

    public MappingTrigger Trigger { get; init; } = new();

    /// <summary>
    /// Application gate evaluated when the foreground or running-set changes. Timing policy stays
    /// on <see cref="Trigger"/>. Omitted JSON deserializes to Always.
    /// </summary>
    public MappingCondition Condition { get; init; } = new();

    public MappingAction Action { get; init; } = new SendKeyAction();
}
