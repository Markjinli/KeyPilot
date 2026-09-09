namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Identifies one opaque input report without assigning button or axis semantics to its bytes.
/// The device path is retained in full so reports from otherwise identical devices stay isolated.
/// </summary>
public sealed class UnknownHidReportIdentity : IEquatable<UnknownHidReportIdentity>
{
    public const int MaximumDevicePathLength = 32_767;

    public UnknownHidReportIdentity(
        string devicePath,
        ushort usagePage,
        ushort usage,
        byte reportId,
        int reportLength)
    {
        DevicePath = RequireDevicePath(devicePath, nameof(devicePath));
        if (reportLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reportLength),
                reportLength,
                "A HID report length must be positive.");
        }

        UsagePage = usagePage;
        Usage = usage;
        ReportId = reportId;
        ReportLength = reportLength;
    }

    public string DevicePath { get; }

    public ushort UsagePage { get; }

    public ushort Usage { get; }

    public byte ReportId { get; }

    public int ReportLength { get; }

    public bool Equals(UnknownHidReportIdentity? other) =>
        other is not null &&
        StringComparer.OrdinalIgnoreCase.Equals(DevicePath, other.DevicePath) &&
        UsagePage == other.UsagePage &&
        Usage == other.Usage &&
        ReportId == other.ReportId &&
        ReportLength == other.ReportLength;

    public override bool Equals(object? obj) => Equals(obj as UnknownHidReportIdentity);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DevicePath, StringComparer.OrdinalIgnoreCase);
        hash.Add(UsagePage);
        hash.Add(Usage);
        hash.Add(ReportId);
        hash.Add(ReportLength);
        return hash.ToHashCode();
    }

    internal static string RequireDevicePath(string devicePath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            throw new ArgumentException(
                "A full HID device path is required; a handle or synthesized identifier is not sufficient.",
                parameterName);
        }

        if (devicePath.Length > MaximumDevicePathLength)
        {
            throw new ArgumentException(
                $"The HID device path exceeds {MaximumDevicePathLength} characters.",
                parameterName);
        }

        return devicePath;
    }
}
