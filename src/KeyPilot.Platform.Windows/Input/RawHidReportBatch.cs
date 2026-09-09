namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// One RAWHID message. Every report is copied out of the native message before this event is
/// raised; the bytes remain opaque and no button/axis meaning is inferred here.
/// </summary>
public sealed class RawHidReportBatch : EventArgs
{
    internal RawHidReportBatch(
        RawHidDeviceDescriptor device,
        int reportLength,
        IReadOnlyList<byte[]> reports,
        DateTimeOffset timestamp)
    {
        Device = device;
        ReportLength = reportLength;
        Reports = reports;
        Timestamp = timestamp;
    }

    public RawHidDeviceDescriptor Device { get; }

    public int ReportLength { get; }

    public IReadOnlyList<byte[]> Reports { get; }

    public DateTimeOffset Timestamp { get; }
}
