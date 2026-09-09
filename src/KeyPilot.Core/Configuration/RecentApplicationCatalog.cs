namespace KeyPilot.Core.Configuration;

public static class RecentApplicationCatalog
{
    public const int MaximumRecentEntries = 30;

    public const int MaximumKnownApplications = 64;

    public static IReadOnlyList<RecentApplicationEntry> RememberForeground(
        IReadOnlyList<RecentApplicationEntry>? current,
        ApplicationIdentity identity,
        DateTimeOffset utcNow)
    {
        if (!identity.HasMatchKey)
        {
            return current?.Where(entry => entry is not null).ToList()
                ?? [];
        }

        var remembered = new List<RecentApplicationEntry>
        {
            new()
            {
                Application = Merge(identity, null),
                LastForegroundUtc = utcNow
            }
        };

        foreach (var entry in current ?? [])
        {
            if (entry?.Application is null || !entry.Application.HasMatchKey)
            {
                continue;
            }

            if (entry.Application.Matches(identity))
            {
                remembered[0] = remembered[0] with
                {
                    Application = Merge(identity, entry.Application)
                };
                continue;
            }

            if (remembered.Count < MaximumRecentEntries)
            {
                remembered.Add(entry);
            }
        }

        return remembered;
    }

    public static IReadOnlyList<ApplicationIdentity> RememberKnown(
        IReadOnlyList<ApplicationIdentity>? current,
        ApplicationIdentity identity)
    {
        if (!identity.HasMatchKey)
        {
            return current?.Where(item => item is not null && item.HasMatchKey).ToList()
                ?? [];
        }

        var remembered = new List<ApplicationIdentity> { Merge(identity, null) };
        foreach (var item in current ?? [])
        {
            if (item is null || !item.HasMatchKey)
            {
                continue;
            }

            if (item.Matches(identity))
            {
                remembered[0] = Merge(identity, item);
                continue;
            }

            if (remembered.Count < MaximumKnownApplications)
            {
                remembered.Add(item);
            }
        }

        return remembered;
    }

    public static ApplicationIdentity Merge(ApplicationIdentity incoming, ApplicationIdentity? existing)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (existing is null)
        {
            return incoming with
            {
                ProcessName = incoming.NormalizedProcessName,
                DisplayName = incoming.DisplayName.Trim(),
                PackageFamilyName = incoming.NormalizedPackageFamilyName
            };
        }

        return new ApplicationIdentity
        {
            ProcessName = FirstNonEmpty(
                incoming.NormalizedProcessName,
                existing.NormalizedProcessName),
            DisplayName = PreferRicherDisplayName(incoming, existing),
            PackageFamilyName = FirstNonEmpty(
                incoming.NormalizedPackageFamilyName,
                existing.NormalizedPackageFamilyName)
        };
    }

    private static string PreferRicherDisplayName(
        ApplicationIdentity incoming,
        ApplicationIdentity existing)
    {
        var incomingName = incoming.DisplayName.Trim();
        var existingName = existing.DisplayName.Trim();
        if (incomingName.Length == 0)
        {
            return existingName;
        }

        if (existingName.Length == 0)
        {
            return incomingName;
        }

        var incomingLooksLikeProcess = string.Equals(
            incomingName,
            incoming.NormalizedProcessName,
            StringComparison.OrdinalIgnoreCase);
        var existingLooksLikeProcess = string.Equals(
            existingName,
            existing.NormalizedProcessName,
            StringComparison.OrdinalIgnoreCase);
        if (incomingLooksLikeProcess && !existingLooksLikeProcess)
        {
            return existingName;
        }

        return incomingName;
    }

    private static string FirstNonEmpty(string first, string second) =>
        first.Length > 0 ? first : second;
}
