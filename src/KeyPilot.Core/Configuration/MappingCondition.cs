namespace KeyPilot.Core.Configuration;

public enum MappingConditionKind
{
    Always,
    ForegroundIs,
    AppRunning,
    ForegroundIsNot
}

/// <summary>
/// Application gate for a mapping. Timing (single/double/long press) stays on
/// <see cref="MappingTrigger"/>. An empty application list is ignored for Always and fails closed
/// for every other kind.
/// </summary>
public sealed record MappingCondition
{
    public const int MaximumApplications = 12;

    public MappingConditionKind Kind { get; init; } = MappingConditionKind.Always;

    public List<ApplicationIdentity> Applications { get; init; } = new();

    public static MappingCondition Always { get; } = new();

    public bool IsRestricted => Kind != MappingConditionKind.Always;

    public string Summary
    {
        get
        {
            if (Kind == MappingConditionKind.Always)
            {
                return "始终生效";
            }

            var names = FormatApplicationNames();
            return Kind switch
            {
                MappingConditionKind.ForegroundIs => $"仅 {names} 前台",
                MappingConditionKind.AppRunning => $"仅当 {names} 在运行",
                MappingConditionKind.ForegroundIsNot => $"排除 {names} 前台",
                _ => "始终生效"
            };
        }
    }

    private string FormatApplicationNames()
    {
        var names = Applications
            .Where(application => application is not null && application.HasMatchKey)
            .Select(application => application.EffectiveDisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            return "指定软件";
        }

        if (names.Count <= 3)
        {
            return string.Join("、", names);
        }

        return $"{names[0]}、{names[1]} 等 {names.Count} 个软件";
    }
}

/// <summary>Win32-free snapshot of the current desktop used to arm mappings.</summary>
public sealed record MappingApplicationContext
{
    public string? ForegroundProcessName { get; init; }

    public string? ForegroundPackageFamilyName { get; init; }

    public IReadOnlyCollection<string> RunningProcessNames { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<string> RunningPackageFamilyNames { get; init; } = Array.Empty<string>();
}

public static class MappingConditionMatcher
{
    public static bool Matches(MappingCondition? condition, MappingApplicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (condition is null || condition.Kind == MappingConditionKind.Always)
        {
            return true;
        }

        var applications = condition.Applications?
            .Where(application => application is not null && application.HasMatchKey)
            .ToList() ?? [];
        if (applications.Count == 0)
        {
            return false;
        }

        return condition.Kind switch
        {
            MappingConditionKind.ForegroundIs =>
                applications.Any(application =>
                    application.Matches(context.ForegroundProcessName, context.ForegroundPackageFamilyName)),
            MappingConditionKind.ForegroundIsNot =>
                !applications.Any(application =>
                    application.Matches(context.ForegroundProcessName, context.ForegroundPackageFamilyName)),
            MappingConditionKind.AppRunning =>
                applications.Any(application => IsRunning(application, context)),
            _ => false
        };
    }

    public static KeyPilotConfiguration Arm(
        KeyPilotConfiguration configuration,
        MappingApplicationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);

        return configuration with
        {
            Profiles = configuration.Profiles
                .Select(profile => ArmProfile(profile, context))
                .ToList()
        };
    }

    public static MappingProfile ArmProfile(
        MappingProfile profile,
        MappingApplicationContext context)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(context);

        return profile with
        {
            Mappings = profile.Mappings
                .Where(mapping => mapping is not null)
                .Select(mapping => Matches(mapping.Condition, context)
                    ? mapping
                    : mapping with { IsEnabled = false })
                .ToList()
        };
    }

    private static bool IsRunning(ApplicationIdentity application, MappingApplicationContext context)
    {
        if (application.NormalizedPackageFamilyName.Length > 0)
        {
            if (application.Matches(context.ForegroundProcessName, context.ForegroundPackageFamilyName))
            {
                return true;
            }

            foreach (var runningFamily in context.RunningPackageFamilyNames)
            {
                if (application.Matches(processName: null, runningFamily))
                {
                    return true;
                }
            }

            return false;
        }

        if (application.Matches(context.ForegroundProcessName, context.ForegroundPackageFamilyName))
        {
            return true;
        }

        foreach (var processName in context.RunningProcessNames)
        {
            if (application.Matches(processName, packageFamilyName: null))
            {
                return true;
            }
        }

        return false;
    }
}
