namespace KeyPilot.Platform.Windows.Input;

/// <summary>One changed byte in an otherwise opaque HID input report.</summary>
public readonly record struct UnknownHidByteChange(
    int Offset,
    byte PreviousValue,
    byte CurrentValue);

/// <summary>
/// An immutable logical change event produced after a report baseline already exists.
/// Byte offsets are the only interpretation applied to the report payload.
/// </summary>
public sealed class UnknownHidReportChange
{
    private readonly IReadOnlyList<byte> _previousReport;
    private readonly IReadOnlyList<byte> _currentReport;
    private readonly IReadOnlyList<UnknownHidByteChange> _byteChanges;

    internal UnknownHidReportChange(
        UnknownHidReportIdentity identity,
        long sequenceNumber,
        DateTimeOffset timestampUtc,
        byte[] previousReport,
        byte[] currentReport,
        UnknownHidByteChange[] byteChanges)
    {
        Identity = identity;
        SequenceNumber = sequenceNumber;
        TimestampUtc = timestampUtc.ToUniversalTime();
        _previousReport = Array.AsReadOnly(previousReport);
        _currentReport = Array.AsReadOnly(currentReport);
        _byteChanges = Array.AsReadOnly(byteChanges);
    }

    public UnknownHidReportIdentity Identity { get; }

    public long SequenceNumber { get; }

    public DateTimeOffset TimestampUtc { get; }

    public IReadOnlyList<byte> PreviousReport => _previousReport;

    public IReadOnlyList<byte> CurrentReport => _currentReport;

    public IReadOnlyList<UnknownHidByteChange> ByteChanges => _byteChanges;
}
