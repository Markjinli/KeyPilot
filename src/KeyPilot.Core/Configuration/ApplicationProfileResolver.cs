namespace KeyPilot.Core.Configuration;

/// <summary>Normalizes process names and resolves the profile selected for an application.</summary>
public static class ApplicationProfileResolver
{
    public static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        var normalized = processName.Trim();
        var separatorIndex = Math.Max(
            normalized.LastIndexOf('\\'),
            normalized.LastIndexOf('/'));
        if (separatorIndex >= 0)
        {
            normalized = normalized[(separatorIndex + 1)..];
        }

        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.Trim();
    }

    /// <summary>
    /// Resolves a foreground process to a configured profile. Disabled switching, an unknown or
    /// unreadable process, and an invalid binding all fall back to the durable active profile.
    /// </summary>
    public static Guid? ResolveProfileId(
        KeyPilotConfiguration configuration,
        string? processName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.IsAutomaticProfileSwitchingEnabled)
        {
            return configuration.ActiveProfileId;
        }

        var normalized = NormalizeProcessName(processName);
        if (normalized.Length == 0 || configuration.ApplicationProfileBindings is null)
        {
            return configuration.ActiveProfileId;
        }

        var binding = configuration.ApplicationProfileBindings.FirstOrDefault(candidate =>
            candidate is not null &&
            string.Equals(
                NormalizeProcessName(candidate.ProcessName),
                normalized,
                StringComparison.OrdinalIgnoreCase));
        if (binding is null ||
            configuration.Profiles is null ||
            !configuration.Profiles.Any(profile => profile?.Id == binding.ProfileId))
        {
            return configuration.ActiveProfileId;
        }

        return binding.ProfileId;
    }
}
