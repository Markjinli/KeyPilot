using KeyPilot.DriverBroker.Protocol;
using KeyPilot.DriverClient;

namespace KeyPilot.DriverBroker;

internal sealed class BrokerSession
{
    private readonly IBrokerTransport _transport;
    private readonly IBrokerDriverClient _driver;
    private readonly IParentLifetime _parent;
    private readonly DriverCapabilities _initialCapabilities;
    private readonly BrokerSessionTiming _timing;
    private readonly TimeProvider _timeProvider;
    private readonly BrokerProgressGate _progressGate;
    private ulong _activeGeneration;
    private ulong _lastDispatchedSequence;
    private ulong _lastSafelyAcknowledgedSequence;
    private bool _rulesExpectedActive;
    private readonly Queue<ulong> _unacknowledgedSequences = new();
    private long _nextHealthTimestamp;

    public BrokerSession(
        IBrokerTransport transport,
        IBrokerDriverClient driver,
        IParentLifetime parent,
        DriverCapabilities initialCapabilities,
        BrokerSessionTiming timing,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _initialCapabilities = initialCapabilities ?? throw new ArgumentNullException(nameof(initialCapabilities));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _progressGate = new BrokerProgressGate(_timeProvider);
        _nextHealthTimestamp = _timeProvider.GetTimestamp();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // This scope owns the exclusive driver handle. Every exit path (including protocol
            // errors and disconnects) releases the lease before control returns to diagnostics.
            _driver.Dispose();
        }
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        var pendingRead = _transport.ReadAsync(cancellationToken).AsTask();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_parent.HasExited)
            {
                throw new EndOfStreamException("The parent UI process exited.");
            }
            if (_progressGate.IsExpired())
            {
                throw new TimeoutException("The UI did not answer the broker health challenge.");
            }

            if (pendingRead.IsCompleted)
            {
                var frame = await pendingRead.ConfigureAwait(false);
                if (await HandleFrameAsync(frame, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                pendingRead = _transport.ReadAsync(cancellationToken).AsTask();
            }

            var now = _timeProvider.GetTimestamp();
            if (!_progressGate.IsPending &&
                _timeProvider.GetElapsedTime(_nextHealthTimestamp, now) >= TimeSpan.Zero)
            {
                var challenge = _progressGate.Issue(_lastDispatchedSequence, _timing.HealthAckTimeout);
                await WriteWithTimeoutAsync(
                    BrokerFrameCodec.EncodeHealthChallenge(challenge),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!_progressGate.IsPending)
            {
                await DispatchEventsAsync(cancellationToken).ConfigureAwait(false);
            }

            var delay = Task.Delay(_timing.PollInterval, _timeProvider, cancellationToken);
            await Task.WhenAny(pendingRead, delay).ConfigureAwait(false);
        }
    }

    private async Task<bool> HandleFrameAsync(BrokerFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case BrokerMessageType.ConfigureRules:
            {
                var configure = BrokerFrameCodec.DecodeConfigureRules(frame);
                if (configure.Generation <= _activeGeneration ||
                    configure.Rules.Count > _initialCapabilities.MaximumRules)
                {
                    throw new BrokerProtocolException("Rules generation or count is invalid for this lease.");
                }
                _driver.ReplaceRules(configure.Rules, configure.Generation);
                _activeGeneration = configure.Generation;
                _rulesExpectedActive = configure.Rules.Count > 0;
                var applied = _driver.GetCapabilities();
                ThrowIfDriverUnsafe(applied);
                if (applied.ActiveGeneration != _activeGeneration)
                {
                    throw new InvalidDataException("The driver did not confirm the requested atomic ruleset.");
                }
                await WriteWithTimeoutAsync(
                    BrokerFrameCodec.EncodeAck(
                        new BrokerAck(BrokerMessageType.ConfigureRules, _activeGeneration),
                        frame.CorrelationId),
                    cancellationToken).ConfigureAwait(false);
                await WriteWithTimeoutAsync(
                    BrokerFrameCodec.EncodeStatus(new BrokerStatus(
                        configure.Rules.Count > 0
                            ? BrokerStatusCode.RulesActive
                            : BrokerStatusCode.LeaseActive,
                        applied.State,
                        _activeGeneration,
                        _initialCapabilities.MaximumRules,
                        $"Driver suppression generation {_activeGeneration} is active.")),
                    cancellationToken).ConfigureAwait(false);
                return false;
            }
            case BrokerMessageType.HealthAck:
            {
                var acknowledgement = BrokerFrameCodec.DecodeHealthAck(frame);
                ValidateCompletionWatermark(acknowledgement.LastCompletedActionSequence);
                var acknowledgedSequence = _progressGate.Accept(acknowledgement);
                AdvanceCompletionWatermark(acknowledgedSequence);
                _driver.Heartbeat(acknowledgedSequence);
                var current = _driver.GetCapabilities();
                ThrowIfDriverUnsafe(current);
                if (current.ActiveGeneration != _activeGeneration)
                {
                    throw new InvalidDataException("The driver's active generation changed unexpectedly.");
                }
                _nextHealthTimestamp = AddTimestamp(_timeProvider.GetTimestamp(), _timing.HeartbeatInterval);
                return false;
            }
            case BrokerMessageType.Shutdown:
                BrokerFrameCodec.DecodeShutdown(frame);
                await WriteWithTimeoutAsync(
                    BrokerFrameCodec.EncodeAck(
                        new BrokerAck(BrokerMessageType.Shutdown, _activeGeneration),
                        frame.CorrelationId),
                    cancellationToken).ConfigureAwait(false);
                return true;
            default:
                throw new BrokerProtocolException("The UI sent a message type that is not valid in an active session.");
        }
    }

    private async Task DispatchEventsAsync(CancellationToken cancellationToken)
    {
        var events = _driver.ReadEvents(BrokerProtocol.MaximumEventsPerBatch);
        if (events.Count == 0)
        {
            return;
        }
        if (events.Count > BrokerProtocol.MaximumEventsPerBatch)
        {
            throw new InvalidDataException("The driver returned too many events.");
        }
        foreach (var input in events)
        {
            if (input.Sequence != checked(_lastDispatchedSequence + 1))
            {
                throw new InvalidDataException("Driver event sequence is not contiguous.");
            }
            _lastDispatchedSequence = input.Sequence;
            _unacknowledgedSequences.Enqueue(input.Sequence);
            if (_unacknowledgedSequences.Count > 4096)
            {
                throw new InvalidDataException("Too many driver events are awaiting a safe action acknowledgement.");
            }
        }
        await WriteWithTimeoutAsync(
            BrokerFrameCodec.EncodeEventBatch(new BrokerEventBatch(events)),
            cancellationToken).ConfigureAwait(false);
    }

    private void ValidateCompletionWatermark(ulong proposedSequence)
    {
        if (proposedSequence == _lastSafelyAcknowledgedSequence)
        {
            return;
        }
        if (proposedSequence < _lastSafelyAcknowledgedSequence ||
            !_unacknowledgedSequences.Contains(proposedSequence))
        {
            throw new BrokerProtocolException(
                "The completed-action watermark does not identify a continuously dispatched event.");
        }
    }

    private void AdvanceCompletionWatermark(ulong acceptedSequence)
    {
        if (acceptedSequence == _lastSafelyAcknowledgedSequence)
        {
            return;
        }
        while (_unacknowledgedSequences.TryPeek(out var sequence) && sequence <= acceptedSequence)
        {
            _ = _unacknowledgedSequences.Dequeue();
        }
        _lastSafelyAcknowledgedSequence = acceptedSequence;
    }

    private async Task WriteWithTimeoutAsync(BrokerFrame frame, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timing.WriteTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _transport.WriteAsync(frame, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The UI stopped draining broker messages.");
        }
    }

    private void ThrowIfDriverUnsafe(DriverCapabilities capabilities)
    {
        const DriverStateFlags unsafeStates =
            DriverStateFlags.QueueOverflowed |
            DriverStateFlags.InputTrackingLost |
            DriverStateFlags.ProgressTimeout |
            DriverStateFlags.EmergencyBypassActive;
        if ((capabilities.State & unsafeStates) != 0 ||
            (capabilities.State & DriverStateFlags.LeaseActive) == 0)
        {
            throw new InvalidDataException("The driver reported fail-open or lost input progress tracking.");
        }
        var rulesAreActive = (capabilities.State & DriverStateFlags.RulesActive) != 0;
        var isFailOpen = (capabilities.State & DriverStateFlags.FailOpen) != 0;
        if (_rulesExpectedActive
                ? !rulesAreActive || isFailOpen
                : rulesAreActive || !isFailOpen)
        {
            throw new InvalidDataException("The driver's fail-open/rules state is inconsistent with policy.");
        }
    }

    private long AddTimestamp(long timestamp, TimeSpan duration)
    {
        var delta = duration.TotalSeconds * _timeProvider.TimestampFrequency;
        return checked(timestamp + (long)Math.Ceiling(delta));
    }
}
