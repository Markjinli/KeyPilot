using KeyPilot.DriverBroker.Protocol;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace KeyPilot.DriverBroker;

internal sealed class BrokerProgressGate(TimeProvider timeProvider)
{
    private ulong _challengeId;
    private ulong _expectedSequence;
    private long _issuedTimestamp;
    private TimeSpan _timeout;
    private ulong _lastActionProgressCounter;
    private ulong _lastSafeAcknowledgedSequence;

    public bool IsPending => _challengeId != 0;

    public BrokerHealthChallenge Issue(ulong lastDispatchedSequence, TimeSpan timeout)
    {
        if (IsPending)
        {
            throw new InvalidOperationException("A health challenge is already pending.");
        }
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Span<byte> random = stackalloc byte[sizeof(ulong)];
        do
        {
            RandomNumberGenerator.Fill(random);
            _challengeId = BinaryPrimitives.ReadUInt64LittleEndian(random);
        }
        while (_challengeId == 0);

        _expectedSequence = lastDispatchedSequence;
        _issuedTimestamp = timeProvider.GetTimestamp();
        _timeout = timeout;
        return new BrokerHealthChallenge(
            _challengeId,
            _expectedSequence,
            checked((uint)Math.Ceiling(timeout.TotalMilliseconds)));
    }

    public ulong Accept(BrokerHealthAck acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!IsPending || acknowledgement.ChallengeId != _challengeId)
        {
            throw new BrokerProtocolException("Health acknowledgement is stale or unsolicited.");
        }
        if (IsExpired())
        {
            throw new TimeoutException("The UI health acknowledgement missed its deadline.");
        }

        var required = BrokerHealthFlags.StateMachineCommitted | BrokerHealthFlags.ActionQueueHealthy;
        if ((acknowledgement.Flags & required) != required ||
            acknowledgement.LastCommittedEventSequence != _expectedSequence ||
            acknowledgement.LastCompletedActionSequence < _lastSafeAcknowledgedSequence ||
            acknowledgement.LastCompletedActionSequence > _expectedSequence ||
            acknowledgement.ActionProgressCounter < _lastActionProgressCounter)
        {
            throw new BrokerProtocolException("The UI did not prove a healthy, fully committed event pipeline.");
        }

        var safeSequenceAdvanced =
            acknowledgement.LastCompletedActionSequence > _lastSafeAcknowledgedSequence;
        if (acknowledgement.LastCompletedActionSequence < _expectedSequence &&
            !safeSequenceAdvanced &&
            ((acknowledgement.Flags & BrokerHealthFlags.ActionProgressVerified) == 0 ||
             acknowledgement.ActionProgressCounter <= _lastActionProgressCounter))
        {
            throw new BrokerProtocolException("The UI action pipeline made no verifiable bounded progress.");
        }

        _lastActionProgressCounter = acknowledgement.ActionProgressCounter;
        _lastSafeAcknowledgedSequence = acknowledgement.LastCompletedActionSequence;
        var acceptedSequence = _lastSafeAcknowledgedSequence;
        _challengeId = 0;
        _expectedSequence = 0;
        _issuedTimestamp = 0;
        _timeout = default;
        return acceptedSequence;
    }

    public bool IsExpired() =>
        IsPending && timeProvider.GetElapsedTime(_issuedTimestamp, timeProvider.GetTimestamp()) >= _timeout;
}
