namespace KeyPilot.Platform.Windows.Input;

/// <summary>The user-visible stage of one explicitly armed opaque HID capture.</summary>
public enum OpaqueHidCaptureStage
{
    Idle,
    WaitingForCandidate,
    WaitingForBaselineReturn,
    WaitingForConfirmation
}

/// <summary>The result of advancing an opaque HID capture with one report change.</summary>
public enum OpaqueHidCaptureProgress
{
    Ignored,
    CandidateDetected,
    BaselineRestored,
    Confirmed,
    CandidateRejected,
    TimedOut
}

public sealed record OpaqueHidCaptureResult(
    OpaqueHidCaptureProgress Progress,
    UnknownHidReportChange? ConfirmedChange = null);

/// <summary>
/// Confirms that an opaque HID transition is repeatable before it can occupy a special-key slot.
/// Bytes are never assigned button semantics: a candidate must change, return to its prior values,
/// and then reproduce the same changed values once more.
/// </summary>
public sealed class OpaqueHidButtonCaptureSession
{
    public const int DefaultMaximumChangedByteCount = 64;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly int _maximumChangedByteCount;
    private readonly TimeSpan _timeout;
    private UnknownHidReportChange? _candidate;
    private DateTimeOffset _armedAtUtc;

    public OpaqueHidButtonCaptureSession(
        int maximumChangedByteCount = DefaultMaximumChangedByteCount,
        TimeSpan? timeout = null)
    {
        if (maximumChangedByteCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumChangedByteCount),
                maximumChangedByteCount,
                "The changed-byte limit must be positive.");
        }

        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                effectiveTimeout,
                "The capture timeout must be positive and no longer than one minute.");
        }

        _maximumChangedByteCount = maximumChangedByteCount;
        _timeout = effectiveTimeout;
    }

    public OpaqueHidCaptureStage Stage { get; private set; }

    public bool IsArmed => Stage != OpaqueHidCaptureStage.Idle;

    public DateTimeOffset ArmedAtUtc => _armedAtUtc;

    public DateTimeOffset DeadlineUtc => IsArmed ? _armedAtUtc + _timeout : default;

    public void Arm(DateTimeOffset timestampUtc)
    {
        _armedAtUtc = timestampUtc.ToUniversalTime();
        _candidate = null;
        Stage = OpaqueHidCaptureStage.WaitingForCandidate;
    }

    public void Cancel()
    {
        _candidate = null;
        _armedAtUtc = default;
        Stage = OpaqueHidCaptureStage.Idle;
    }

    public bool TryExpire(DateTimeOffset timestampUtc)
    {
        if (!IsArmed || timestampUtc.ToUniversalTime() <= DeadlineUtc)
        {
            return false;
        }

        Cancel();
        return true;
    }

    public OpaqueHidCaptureResult Observe(UnknownHidReportChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!IsArmed || change.TimestampUtc < _armedAtUtc)
        {
            return new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.Ignored);
        }

        if (TryExpire(change.TimestampUtc))
        {
            return new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.TimedOut);
        }

        if (change.ByteChanges.Count == 0 || change.ByteChanges.Count > _maximumChangedByteCount)
        {
            return RejectCandidate();
        }

        if (Stage == OpaqueHidCaptureStage.WaitingForCandidate)
        {
            _candidate = change;
            Stage = OpaqueHidCaptureStage.WaitingForBaselineReturn;
            return new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.CandidateDetected);
        }

        if (_candidate is null || !_candidate.Identity.Equals(change.Identity))
        {
            return new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.Ignored);
        }

        if (Stage == OpaqueHidCaptureStage.WaitingForBaselineReturn)
        {
            if (MatchesCandidateValues(change.CurrentReport, useActiveValues: false))
            {
                Stage = OpaqueHidCaptureStage.WaitingForConfirmation;
                return new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.BaselineRestored);
            }

            return MatchesCandidateValues(change.CurrentReport, useActiveValues: true)
                ? new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.Ignored)
                : RejectCandidate();
        }

        if (Stage == OpaqueHidCaptureStage.WaitingForConfirmation)
        {
            if (MatchesCandidateValues(change.CurrentReport, useActiveValues: true))
            {
                var confirmed = _candidate;
                Cancel();
                return new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.Confirmed, confirmed);
            }

            return MatchesCandidateValues(change.CurrentReport, useActiveValues: false)
                ? new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.Ignored)
                : RejectCandidate();
        }

        return new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.Ignored);
    }

    private bool MatchesCandidateValues(IReadOnlyList<byte> report, bool useActiveValues)
    {
        if (_candidate is null || report.Count != _candidate.Identity.ReportLength)
        {
            return false;
        }

        foreach (var change in _candidate.ByteChanges)
        {
            var expected = useActiveValues ? change.CurrentValue : change.PreviousValue;
            if (report[change.Offset] != expected)
            {
                return false;
            }
        }

        return true;
    }

    private OpaqueHidCaptureResult RejectCandidate()
    {
        _candidate = null;
        Stage = OpaqueHidCaptureStage.WaitingForCandidate;
        return new OpaqueHidCaptureResult(OpaqueHidCaptureProgress.CandidateRejected);
    }
}
