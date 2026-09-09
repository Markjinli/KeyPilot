namespace KeyPilot.Core.Configuration;

/// <summary>
/// A user-facing application identity. Matching is by normalized process name, or by package family
/// when both sides have one. DisplayName is never used as an identity key.
/// </summary>
public sealed record ApplicationIdentity
{
    public const string ApplicationFrameHostProcessName = "ApplicationFrameHost";

    public string ProcessName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string PackageFamilyName { get; init; } = string.Empty;

    public string NormalizedProcessName =>
        ApplicationProfileResolver.NormalizeProcessName(ProcessName);

    public string NormalizedPackageFamilyName => PackageFamilyName.Trim();

    public string EffectiveDisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName))
            {
                return DisplayName.Trim();
            }

            var processName = NormalizedProcessName;
            return processName.Length == 0 ? ProcessName.Trim() : processName;
        }
    }

    public bool HasMatchKey =>
        NormalizedProcessName.Length > 0 || NormalizedPackageFamilyName.Length > 0;

    public bool Matches(ApplicationIdentity? other)
    {
        if (other is null)
        {
            return false;
        }

        return Matches(other.ProcessName, other.PackageFamilyName);
    }

    public bool Matches(string? processName, string? packageFamilyName)
    {
        var family = NormalizedPackageFamilyName;
        var otherFamily = packageFamilyName?.Trim() ?? string.Empty;
        if (family.Length > 0 || otherFamily.Length > 0)
        {
            return family.Length > 0 &&
                string.Equals(family, otherFamily, StringComparison.OrdinalIgnoreCase);
        }

        var process = NormalizedProcessName;
        if (process.Length == 0)
        {
            return false;
        }

        if (string.Equals(
                process,
                ApplicationFrameHostProcessName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(
            process,
            ApplicationProfileResolver.NormalizeProcessName(processName),
            StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One recently observed foreground application, newest first in the persisted list.</summary>
public sealed record RecentApplicationEntry
{
    public ApplicationIdentity Application { get; init; } = new();

    public DateTimeOffset LastForegroundUtc { get; init; }
}
