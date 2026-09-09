namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Keeps the last opaque input report for each exact HID identity and emits byte-level deltas.
/// This class performs no device discovery, Raw Input registration, output report writing, or
/// control-semantic inference.
/// </summary>
public sealed class UnknownHidReportDiffer
{
    public const int DefaultMaximumReportLength = 4_096;
    public const int AbsoluteMaximumReportLength = 65_535;
    public const int DefaultMaximumTrackedReportCount = 256;
    public const int AbsoluteMaximumTrackedReportCount = 4_096;

    private readonly object _gate = new();
    private readonly Dictionary<UnknownHidReportIdentity, byte[]> _lastReports = new();
    private readonly int _maximumReportLength;
    private readonly int _maximumTrackedReportCount;
    private long _sequenceNumber;

    public UnknownHidReportDiffer(
        int maximumReportLength = DefaultMaximumReportLength,
        int maximumTrackedReportCount = DefaultMaximumTrackedReportCount)
    {
        if (maximumReportLength is <= 0 or > AbsoluteMaximumReportLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumReportLength),
                maximumReportLength,
                $"The maximum report length must be between 1 and {AbsoluteMaximumReportLength} bytes.");
        }

        if (maximumTrackedReportCount is <= 0 or > AbsoluteMaximumTrackedReportCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTrackedReportCount),
                maximumTrackedReportCount,
                $"The tracked report count must be between 1 and {AbsoluteMaximumTrackedReportCount}.");
        }

        _maximumReportLength = maximumReportLength;
        _maximumTrackedReportCount = maximumTrackedReportCount;
    }

    public int TrackedReportCount
    {
        get
        {
            lock (_gate)
            {
                return _lastReports.Count;
            }
        }
    }

    /// <summary>
    /// Observes one complete report. The first report for an identity establishes a baseline and
    /// returns null; later changed reports return one logical event containing every changed byte.
    /// </summary>
    public UnknownHidReportChange? Observe(
        UnknownHidReportIdentity identity,
        ReadOnlySpan<byte> report,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateReport(identity, report.Length);

        lock (_gate)
        {
            if (!_lastReports.TryGetValue(identity, out var previousReport))
            {
                if (_lastReports.Count >= _maximumTrackedReportCount)
                {
                    throw new InvalidOperationException(
                        $"The unknown HID report limit of {_maximumTrackedReportCount} has been reached. " +
                        "Disconnect a device or clear the tracker before adding another report identity.");
                }

                _lastReports.Add(identity, report.ToArray());
                return null;
            }

            List<UnknownHidByteChange>? changes = null;
            for (var index = 0; index < report.Length; index++)
            {
                var currentValue = report[index];
                var previousValue = previousReport[index];
                if (currentValue == previousValue)
                {
                    continue;
                }

                changes ??= new List<UnknownHidByteChange>();
                changes.Add(new UnknownHidByteChange(index, previousValue, currentValue));
            }

            if (changes is null)
            {
                return null;
            }

            var currentReport = report.ToArray();
            _lastReports[identity] = currentReport;
            return new UnknownHidReportChange(
                identity,
                checked(++_sequenceNumber),
                timestamp,
                previousReport,
                currentReport,
                changes.ToArray());
        }
    }

    /// <summary>Copies the retained baseline so callers cannot mutate tracker state.</summary>
    public bool TryGetLastReport(UnknownHidReportIdentity identity, out byte[] report)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            if (_lastReports.TryGetValue(identity, out var retained))
            {
                report = (byte[])retained.Clone();
                return true;
            }
        }

        report = Array.Empty<byte>();
        return false;
    }

    /// <summary>Removes every report identity belonging to one disconnected physical device.</summary>
    public int RemoveDevice(string devicePath)
    {
        UnknownHidReportIdentity.RequireDevicePath(devicePath, nameof(devicePath));
        lock (_gate)
        {
            var identities = _lastReports.Keys
                .Where(identity => StringComparer.OrdinalIgnoreCase.Equals(identity.DevicePath, devicePath))
                .ToArray();
            foreach (var identity in identities)
            {
                _lastReports.Remove(identity);
            }

            return identities.Length;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lastReports.Clear();
        }
    }

    private void ValidateReport(UnknownHidReportIdentity identity, int actualLength)
    {
        if (identity.ReportLength > _maximumReportLength)
        {
            throw new InvalidDataException(
                $"HID report length {identity.ReportLength} exceeds the configured limit " +
                $"of {_maximumReportLength} bytes.");
        }

        if (actualLength != identity.ReportLength)
        {
            throw new InvalidDataException(
                $"HID report length mismatch: identity declares {identity.ReportLength} bytes, " +
                $"but {actualLength} bytes were supplied.");
        }
    }
}
