using System.Threading.Channels;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Triggers;

namespace KeyPilot.App.Presentation;

/// <summary>
/// Keeps input recognition responsive while mapping actions execute in deterministic FIFO order.
/// Ordinary configuration edits affect subsequent input while preserving activated work. A
/// foreground execution-context change can additionally cancel running and queued old-context
/// actions so synthetic input cannot spill into the next application.
/// </summary>
internal sealed class MappingExecutionCoordinator : IAsyncDisposable
{
    internal const int ActionQueueCapacity = 256;

    private readonly MappingActionPlanner _planner;
    private readonly Func<ActionPlan, CancellationToken, Task> _executeAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Channel<RuntimeCommand> _commands;
    private readonly Channel<PlannedActivation> _actions;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource _actionContextCancellation = new();
    private readonly Task _commandWorker;
    private readonly Task _actionWorker;
    private MappingTriggerStateMachine _triggers = new(Array.Empty<InputMapping>());
    private MappingTriggerStateMachine _passThroughTriggers = new(Array.Empty<InputMapping>());
    private IReadOnlyList<SpecialKeySlot> _specialKeySlots = Array.Empty<SpecialKeySlot>();
    private CancellationTokenSource? _deadlineCancellation;
    private DateTimeOffset? _logicalTimeUtc;
    private readonly Dictionary<long, TrackedInputState> _trackedInputs = new();
    private readonly Dictionary<long, DateTimeOffset> _recentSuccessfulCompletions = new();
    private long _nextActionOrdinal;
    private long _completedActionOrdinal;
    private long _progressCounter;
    private long _deadlineVersion;
    private long _submittedRuntimeRevision;
    private long _activeRuntimeRevision;
    private DateTimeOffset? _scheduledActionDeadlineUtc;
    private int _actionQueueSaturationReported;
    private int _stopped;

    public MappingExecutionCoordinator(
        MappingActionPlanner planner,
        Func<ActionPlan, CancellationToken, Task> executeAsync,
        Func<DateTimeOffset>? utcNow = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _commands = Channel.CreateUnbounded<RuntimeCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _actions = Channel.CreateBounded<PlannedActivation>(new BoundedChannelOptions(
            ActionQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            // TryWrite must return false at capacity. The recognition worker never waits for a
            // slow script/macro and therefore cannot block Raw Input or XInput dispatch.
            FullMode = BoundedChannelFullMode.Wait
        });
        _commandWorker = RunCommandWorkerAsync(_lifetimeCancellation.Token);
        _actionWorker = RunActionWorkerAsync(_lifetimeCancellation.Token);
    }

    public event EventHandler<MappingExecutionFaultEventArgs>? Faulted;

    /// <summary>
    /// Atomically replaces the trigger recognizer with the selected enabled profile. Pending
    /// long/double-press state is intentionally reset so it cannot cross a profile boundary.
    /// </summary>
    public bool TryApplyConfiguration(
        KeyPilotConfiguration configuration,
        bool cancelPendingActions = false,
        long runtimeRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var activeProfile = configuration.ActiveProfileId is Guid activeId
            ? configuration.Profiles.FirstOrDefault(profile => profile.Id == activeId)
            : null;
        activeProfile ??= configuration.Profiles.FirstOrDefault(profile => profile.IsEnabled);

        var mappings = configuration.IsMappingEnabled && activeProfile?.IsEnabled == true
            ? activeProfile.Mappings.Where(mapping => mapping is not null).ToArray()
            : Array.Empty<InputMapping>();
        var slots = configuration.SpecialKeySlots
            .Where(slot => slot is not null)
            .ToArray();

        return TryWrite(new ApplyConfigurationCommand(
            mappings,
            slots,
            cancelPendingActions,
            ResolveRuntimeRevision(runtimeRevision)));
    }

    /// <summary>
    /// Queues one physical input edge. Injected events are rejected before they can advance or
    /// activate the recognizer, providing a second recursion guard behind the Raw Input marker.
    /// </summary>
    public bool TrySubmit(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        ArgumentNullException.ThrowIfNull(inputEvent.Source);
        return !inputEvent.IsInjected && TryWrite(new InputCommand(
            inputEvent,
            null,
            null,
            PassThroughOnly: false));
    }

    /// <summary>Queues an unsuppressed OS event for mappings that explicitly retain the original input.</summary>
    public bool TrySubmitPassThrough(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        ArgumentNullException.ThrowIfNull(inputEvent.Source);
        return !inputEvent.IsInjected && TryWrite(new InputCommand(
            inputEvent,
            null,
            null,
             PassThroughOnly: true));
    }

    /// <summary>Clears held/chord/gesture state after a device topology change.</summary>
    public bool TryResetInputState() => TryWrite(new ResetInputStateCommand());

    /// <summary>
    /// Queues an input and completes only after every action causally activated by it succeeds.
    /// Pending long/double-press deadlines are included; planning, queue or execution failures are
    /// returned to the caller so a suppression lease can fail open instead of acknowledging early.
    /// </summary>
    public Task<MappingInputCompletion> SubmitTrackedAsync(InputEvent inputEvent)
        => SubmitTracked(inputEvent).Completion;

    /// <summary>
    /// Queues a suppressed input and exposes two safety boundaries. Committed completes only
    /// after the trigger state machine accepted the edge; Completion additionally waits for all
    /// causally activated actions and gesture deadlines. A broker must never treat Committed as
    /// permission to advance the kernel completion acknowledgement.
    /// </summary>
    public MappingInputTracking SubmitTracked(
        InputEvent inputEvent,
        long expectedRuntimeRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        ArgumentNullException.ThrowIfNull(inputEvent.Source);
        var committed = new TaskCompletionSource<MappingInputCommit>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<MappingInputCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (inputEvent.IsInjected || inputEvent.SequenceNumber <= 0 ||
            !TryWrite(new InputCommand(
                inputEvent,
                committed,
                completion,
                PassThroughOnly: false,
                ExpectedRuntimeRevision: expectedRuntimeRevision)))
        {
            var exception = new InvalidOperationException(
                "The tracked input could not enter the mapping runtime.");
            var progress = Volatile.Read(ref _progressCounter);
            committed.TrySetResult(new MappingInputCommit(false, progress, exception));
            completion.TrySetResult(new MappingInputCompletion(
                false,
                progress,
                exception));
        }

        return new MappingInputTracking(committed.Task, completion.Task);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _commands.Writer.TryComplete();
        try
        {
            _deadlineCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The deadline callback completed concurrently.
        }

        _lifetimeCancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await Task.WhenAll(_commandWorker, _actionWorker).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _deadlineCancellation?.Dispose();
            _actionContextCancellation.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private bool TryWrite(RuntimeCommand command) =>
        Volatile.Read(ref _stopped) == 0 && _commands.Writer.TryWrite(command);

    private long ResolveRuntimeRevision(long requestedRevision)
    {
        if (requestedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedRevision),
                "A runtime revision cannot be negative.");
        }

        if (requestedRevision == 0)
        {
            return Interlocked.Increment(ref _submittedRuntimeRevision);
        }

        while (true)
        {
            var current = Volatile.Read(ref _submittedRuntimeRevision);
            if (requestedRevision <= current)
            {
                return requestedRevision;
            }

            if (Interlocked.CompareExchange(
                    ref _submittedRuntimeRevision,
                    requestedRevision,
                    current) == current)
            {
                return requestedRevision;
            }
        }
    }

    private async Task RunCommandWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                switch (command)
                {
                    case ApplyConfigurationCommand apply:
                        CancelDeadline();
                        if (apply.CancelPendingActions)
                        {
                            CancelActionContext();
                        }
                        _triggers = new MappingTriggerStateMachine(apply.Mappings);
                        _passThroughTriggers = new MappingTriggerStateMachine(
                            apply.Mappings.Where(mapping => !mapping.SuppressOriginal));
                        _specialKeySlots = apply.SpecialKeySlots;
                        _activeRuntimeRevision = apply.RuntimeRevision;
                        _logicalTimeUtc = null;
                        foreach (var tracked in _trackedInputs.Values)
                        {
                            // Configuration replacement intentionally cancels pending gestures.
                            // Activated work either continues under the ordinary edit policy or
                            // completes as failed after an explicit context cancellation.
                            tracked.WaitUntilUtc = null;
                        }
                        TryCompleteTrackedInputs();
                        break;

                    case ResetInputStateCommand:
                        CancelDeadline();
                        _triggers.Reset();
                        _passThroughTriggers.Reset();
                        _logicalTimeUtc = null;
                        foreach (var tracked in _trackedInputs.Values)
                        {
                            tracked.WaitUntilUtc = null;
                        }

                        TryCompleteTrackedInputs();
                        ScheduleNextDeadline();
                        break;

                    case InputCommand input when !input.Event.IsInjected:
                        if (input.ExpectedRuntimeRevision > 0 &&
                            input.ExpectedRuntimeRevision != _activeRuntimeRevision)
                        {
                            var exception = new InvalidOperationException(
                                "The suppressed input belongs to an obsolete runtime context.");
                            var progress = ++_progressCounter;
                            input.Committed?.TrySetResult(new MappingInputCommit(
                                false,
                                progress,
                                exception));
                            input.Completion?.TrySetResult(new MappingInputCompletion(
                                false,
                                progress,
                                exception));
                            break;
                        }

                        if (input.Completion is not null)
                        {
                            if (!_trackedInputs.TryAdd(
                                    input.Event.SequenceNumber,
                                    new TrackedInputState(input.Committed!, input.Completion)
                                    {
                                        LivenessDeadlineUtc = AddClamped(
                                            _utcNow(),
                                            TimeSpan.FromSeconds(2))
                                    }))
                            {
                                var exception = new InvalidOperationException(
                                    "Tracked input sequence is not unique.");
                                var progress = ++_progressCounter;
                                input.Committed!.TrySetResult(new MappingInputCommit(
                                    false,
                                    progress,
                                    exception));
                                input.Completion.TrySetResult(new MappingInputCompletion(
                                    false,
                                    progress,
                                    exception));
                                break;
                            }
                        }

                        var normalizedEvent = input.Event with
                        {
                            TimestampUtc = NormalizeTimestamp(input.Event.TimestampUtc)
                        };
                        if (input.PassThroughOnly)
                        {
                            QueueActivations(_triggers.AdvanceTo(normalizedEvent.TimestampUtc));
                            QueueActivations(_passThroughTriggers.Process(normalizedEvent));
                        }
                        else
                        {
                            QueueActivations(_passThroughTriggers.AdvanceTo(normalizedEvent.TimestampUtc));
                            QueueActivations(_triggers.Process(normalizedEvent));
                        }
                        RefreshTrackedDeadlines();
                        if (input.Completion is not null &&
                            _trackedInputs.TryGetValue(input.Event.SequenceNumber, out var trackedInput))
                        {
                            trackedInput.Committed.TrySetResult(new MappingInputCommit(
                                true,
                                ++_progressCounter,
                                null)
                            {
                                VerifyLivenessAsync = cancellationToken =>
                                    VerifyTrackedLivenessAsync(
                                        input.Event.SequenceNumber,
                                        cancellationToken)
                            });
                        }
                        TryCompleteTrackedInputs();
                        ScheduleNextDeadline();
                        break;

                    case AdvanceDeadlineCommand advance
                        when advance.Version == Volatile.Read(ref _deadlineVersion):
                        var advancedTime = NormalizeTimestamp(advance.DeadlineUtc);
                        QueueActivations(_triggers.AdvanceTo(advancedTime));
                        QueueActivations(_passThroughTriggers.AdvanceTo(advancedTime));
                        RefreshTrackedDeadlines();
                        TryCompleteTrackedInputs();
                        ScheduleNextDeadline();
                        break;

                    case ActionCompletedCommand completed:
                        if (completed.Ordinal != _completedActionOrdinal + 1)
                        {
                            throw new InvalidOperationException("Action completion order is not FIFO.");
                        }

                        _completedActionOrdinal = completed.Ordinal;
                        if (_completedActionOrdinal == _nextActionOrdinal)
                        {
                            _scheduledActionDeadlineUtc = _utcNow();
                        }
                        _progressCounter++;
                        if (!completed.Success &&
                            _trackedInputs.TryGetValue(completed.OriginSequence, out var failedInput))
                        {
                            failedInput.Failure = completed.Exception ??
                                new InvalidOperationException("The mapped action failed.");
                        }
                        TryCompleteTrackedInputs();
                        break;

                    case VerifyLivenessCommand verify:
                        if (_trackedInputs.TryGetValue(verify.OriginSequence, out var live) &&
                            live.Failure is null &&
                            _utcNow() <= live.LivenessDeadlineUtc)
                        {
                            verify.Completion.TrySetResult(++_progressCounter);
                        }
                        else if (_recentSuccessfulCompletions.TryGetValue(
                                     verify.OriginSequence,
                                     out var retainedUntil) &&
                                 _utcNow() <= retainedUntil)
                        {
                            // Completion continuations report to the broker asynchronously. Keep
                            // a short success tombstone so a challenge landing in that handoff
                            // cannot mistake a successfully completed action for a stalled one.
                            verify.Completion.TrySetResult(++_progressCounter);
                        }
                        else
                        {
                            verify.Completion.TrySetException(new InvalidOperationException(
                                "The tracked input is no longer inside a declared bounded wait."));
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception) when (!IsFatalProcessException(exception))
        {
            ReportFault(MappingExecutionStage.InputRecognition, null, exception);
        }
        finally
        {
            CancelDeadline();
            CompleteAllTrackedAsFailed(new OperationCanceledException(
                "The mapping runtime stopped before tracked input completed."));
            _actions.Writer.TryComplete();
        }
    }

    private async Task RunActionWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var activation in _actions.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _actionQueueSaturationReported, 0);
                Exception? failure = null;
                using var actionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    activation.ContextCancellationToken);
                try
                {
                    actionCancellation.Token.ThrowIfCancellationRequested();
                    await _executeAsync(activation.Plan, actionCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                    when (activation.ContextCancellationToken.IsCancellationRequested)
                {
                    failure = new OperationCanceledException(
                        "The mapped action was cancelled because the foreground execution context changed.",
                        activation.ContextCancellationToken);
                }
                catch (Exception exception) when (!IsFatalProcessException(exception))
                {
                    failure = exception;
                    ReportFault(MappingExecutionStage.ActionExecution, activation.Mapping, exception);
                }

                if (!TryWrite(new ActionCompletedCommand(
                        activation.Ordinal,
                        activation.OriginSequence,
                        failure is null,
                        failure)))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception) when (!IsFatalProcessException(exception))
        {
            ReportFault(MappingExecutionStage.ActionExecution, null, exception);
        }
    }

    private void QueueActivations(IEnumerable<MappingTriggerActivation> activations)
    {
        foreach (var activation in activations)
        {
            try
            {
                var plan = _planner.Plan(activation.Mapping.Action, _specialKeySlots);
                var ordinal = checked(_nextActionOrdinal + 1);
                if (!_actions.Writer.TryWrite(new PlannedActivation(
                        activation.Mapping,
                        plan,
                        ordinal,
                        activation.OriginatingEvent.SequenceNumber,
                        _actionContextCancellation.Token)))
                {
                    FailTrackedInput(
                        activation.OriginatingEvent.SequenceNumber,
                        new InvalidOperationException(
                            $"The action queue reached its {ActionQueueCapacity}-item safety limit."));
                    if (Interlocked.Exchange(ref _actionQueueSaturationReported, 1) == 0)
                    {
                        ReportFault(
                            MappingExecutionStage.ActionExecution,
                            activation.Mapping,
                            new InvalidOperationException(
                                $"The action queue reached its {ActionQueueCapacity}-item safety limit; " +
                                "new activations are being discarded until execution catches up."));
                    }
                }
                else
                {
                    _nextActionOrdinal = ordinal;
                    var scheduledFrom = _scheduledActionDeadlineUtc is { } prior && prior > _utcNow()
                        ? prior
                        : _utcNow();
                    _scheduledActionDeadlineUtc = AddClamped(
                        scheduledFrom,
                        EstimateMaximumExecutionTime(plan));
                    if (_trackedInputs.TryGetValue(
                            activation.OriginatingEvent.SequenceNumber,
                            out var tracked))
                    {
                        tracked.RequiredActionOrdinal = ordinal;
                        if (_scheduledActionDeadlineUtc > tracked.LivenessDeadlineUtc)
                        {
                            tracked.LivenessDeadlineUtc = _scheduledActionDeadlineUtc.Value;
                        }
                    }
                }
            }
            catch (Exception exception) when (!IsFatalProcessException(exception))
            {
                FailTrackedInput(activation.OriginatingEvent.SequenceNumber, exception);
                ReportFault(MappingExecutionStage.ActionPlanning, activation.Mapping, exception);
            }
        }
    }

    private DateTimeOffset NormalizeTimestamp(DateTimeOffset timestampUtc)
    {
        var normalized = timestampUtc;
        if (_logicalTimeUtc.HasValue && normalized < _logicalTimeUtc.Value)
        {
            normalized = _logicalTimeUtc.Value;
        }

        _logicalTimeUtc = normalized;
        return normalized;
    }

    private void ScheduleNextDeadline()
    {
        CancelDeadline();
        var deadline = NextCoordinatorDeadlineUtc();
        if (!deadline.HasValue || Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        var version = Interlocked.Increment(ref _deadlineVersion);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _deadlineCancellation = cancellation;
        _ = PostDeadlineAsync(deadline.Value, version, cancellation);
    }

    private async Task PostDeadlineAsync(
        DateTimeOffset deadlineUtc,
        long version,
        CancellationTokenSource cancellation)
    {
        try
        {
            var delay = deadlineUtc - _utcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            }

            // Double-press windows are inclusive, so advancing one tick past the deadline both
            // expires them and avoids repeatedly scheduling the same due timestamp. Long-press
            // activations still report their exact threshold from the state machine.
            TryWrite(new AdvanceDeadlineCommand(deadlineUtc.AddTicks(1), version));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Superseded deadline or normal shutdown.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void CancelDeadline()
    {
        Interlocked.Increment(ref _deadlineVersion);
        var cancellation = _deadlineCancellation;
        _deadlineCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The callback owns disposal and may have just completed.
        }
    }

    private DateTimeOffset? NextCoordinatorDeadlineUtc()
    {
        var deadline = _triggers.NextDeadlineUtc;
        var passThroughDeadline = _passThroughTriggers.NextDeadlineUtc;
        if (passThroughDeadline.HasValue &&
            (!deadline.HasValue || passThroughDeadline.Value < deadline.Value))
        {
            deadline = passThroughDeadline;
        }

        foreach (var tracked in _trackedInputs.Values)
        {
            if (tracked.WaitUntilUtc.HasValue &&
                (!deadline.HasValue || tracked.WaitUntilUtc.Value < deadline.Value))
            {
                deadline = tracked.WaitUntilUtc;
            }
        }

        return deadline;
    }

    private void RefreshTrackedDeadlines()
    {
        foreach (var pair in _trackedInputs)
        {
            var deadline = _triggers.GetPendingDeadlineUtc(pair.Key);
            pair.Value.WaitUntilUtc = deadline?.AddTicks(1);
            if (deadline.HasValue)
            {
                var livenessDeadline = AddClamped(deadline.Value, TimeSpan.FromSeconds(2));
                if (livenessDeadline > pair.Value.LivenessDeadlineUtc)
                {
                    pair.Value.LivenessDeadlineUtc = livenessDeadline;
                }
            }
        }
    }

    private Task<long> VerifyTrackedLivenessAsync(
        long originSequence,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryWrite(new VerifyLivenessCommand(originSequence, completion)))
        {
            completion.TrySetException(new InvalidOperationException(
                "The mapping runtime cannot verify tracked-input liveness."));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private static TimeSpan EstimateMaximumExecutionTime(ActionPlan plan)
    {
        long milliseconds = 5_000;
        foreach (var delay in plan.Operations.OfType<DelayOperation>())
        {
            milliseconds = milliseconds > long.MaxValue - delay.Milliseconds
                ? long.MaxValue
                : milliseconds + delay.Milliseconds;
        }

        return milliseconds == long.MaxValue
            ? TimeSpan.MaxValue
            : TimeSpan.FromMilliseconds(milliseconds);
    }

    private static DateTimeOffset AddClamped(DateTimeOffset value, TimeSpan duration)
    {
        if (duration == TimeSpan.MaxValue || value.Ticks > DateTimeOffset.MaxValue.Ticks - duration.Ticks)
        {
            return DateTimeOffset.MaxValue;
        }

        return value + duration;
    }

    private void TryCompleteTrackedInputs()
    {
        if (_trackedInputs.Count == 0)
        {
            return;
        }

        var completed = new List<long>();
        foreach (var pair in _trackedInputs)
        {
            var tracked = pair.Value;
            if (tracked.Failure is not null)
            {
                tracked.Completion.TrySetResult(new MappingInputCompletion(
                    false,
                    ++_progressCounter,
                    tracked.Failure));
                completed.Add(pair.Key);
                continue;
            }

            if (tracked.WaitUntilUtc.HasValue &&
                (!_logicalTimeUtc.HasValue || _logicalTimeUtc.Value < tracked.WaitUntilUtc.Value))
            {
                continue;
            }

            if (tracked.RequiredActionOrdinal > _completedActionOrdinal)
            {
                continue;
            }

            var completionProgress = ++_progressCounter;
            tracked.Completion.TrySetResult(new MappingInputCompletion(
                true,
                completionProgress,
                null));
            RememberSuccessfulCompletion(pair.Key);
            completed.Add(pair.Key);
        }

        foreach (var sequence in completed)
        {
            _trackedInputs.Remove(sequence);
        }
    }

    private void FailTrackedInput(long originSequence, Exception exception)
    {
        if (_trackedInputs.TryGetValue(originSequence, out var tracked))
        {
            tracked.Failure ??= exception;
        }
    }

    private void CancelActionContext()
    {
        var previous = _actionContextCancellation;
        _actionContextCancellation = new CancellationTokenSource();
        try
        {
            previous.Cancel();
        }
        finally
        {
            previous.Dispose();
        }
    }

    private void RememberSuccessfulCompletion(long originSequence)
    {
        if (_recentSuccessfulCompletions.Count >= 2_048)
        {
            var now = _utcNow();
            foreach (var expired in _recentSuccessfulCompletions
                         .Where(pair => pair.Value < now)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _recentSuccessfulCompletions.Remove(expired);
            }

            if (_recentSuccessfulCompletions.Count >= 2_048)
            {
                _recentSuccessfulCompletions.Remove(_recentSuccessfulCompletions.Keys.First());
            }
        }

        _recentSuccessfulCompletions[originSequence] = AddClamped(
            _utcNow(),
            TimeSpan.FromSeconds(2));
    }

    private void CompleteAllTrackedAsFailed(Exception exception)
    {
        foreach (var tracked in _trackedInputs.Values)
        {
            var progress = ++_progressCounter;
            tracked.Committed.TrySetResult(new MappingInputCommit(
                false,
                progress,
                exception));
            tracked.Completion.TrySetResult(new MappingInputCompletion(
                false,
                progress,
                exception));
        }

        _trackedInputs.Clear();
    }

    private void ReportFault(
        MappingExecutionStage stage,
        InputMapping? mapping,
        Exception exception)
    {
        var handlers = Faulted;
        if (handlers is null)
        {
            return;
        }

        var args = new MappingExecutionFaultEventArgs(stage, mapping, exception);
        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<MappingExecutionFaultEventArgs>)subscriber)(this, args);
            }
            catch
            {
                // Diagnostics must not stop input recognition or action cleanup.
            }
        }
    }

    private static bool IsFatalProcessException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private abstract record RuntimeCommand;

    private sealed record ApplyConfigurationCommand(
        IReadOnlyList<InputMapping> Mappings,
        IReadOnlyList<SpecialKeySlot> SpecialKeySlots,
        bool CancelPendingActions,
        long RuntimeRevision) : RuntimeCommand;

    private sealed record InputCommand(
        InputEvent Event,
        TaskCompletionSource<MappingInputCommit>? Committed,
        TaskCompletionSource<MappingInputCompletion>? Completion,
        bool PassThroughOnly,
        long ExpectedRuntimeRevision = 0) : RuntimeCommand;

    private sealed record ResetInputStateCommand : RuntimeCommand;

    private sealed record AdvanceDeadlineCommand(
        DateTimeOffset DeadlineUtc,
        long Version) : RuntimeCommand;

    private sealed record ActionCompletedCommand(
        long Ordinal,
        long OriginSequence,
        bool Success,
        Exception? Exception) : RuntimeCommand;

    private sealed record VerifyLivenessCommand(
        long OriginSequence,
        TaskCompletionSource<long> Completion) : RuntimeCommand;

    private sealed record PlannedActivation(
        InputMapping Mapping,
        ActionPlan Plan,
        long Ordinal,
        long OriginSequence,
        CancellationToken ContextCancellationToken);

    private sealed class TrackedInputState(
        TaskCompletionSource<MappingInputCommit> committed,
        TaskCompletionSource<MappingInputCompletion> completion)
    {
        public TaskCompletionSource<MappingInputCommit> Committed { get; } = committed;

        public TaskCompletionSource<MappingInputCompletion> Completion { get; } = completion;

        public long RequiredActionOrdinal { get; set; }

        public DateTimeOffset? WaitUntilUtc { get; set; }

        public DateTimeOffset LivenessDeadlineUtc { get; set; }

        public Exception? Failure { get; set; }
    }
}

internal sealed record MappingInputTracking(
    Task<MappingInputCommit> Committed,
    Task<MappingInputCompletion> Completion);

internal sealed record MappingInputCommit(
    bool Success,
    long ProgressCounter,
    Exception? Exception)
{
    public Func<CancellationToken, Task<long>>? VerifyLivenessAsync { get; init; }
}

internal sealed record MappingInputCompletion(
    bool Success,
    long ProgressCounter,
    Exception? Exception);

internal enum MappingExecutionStage
{
    InputRecognition,
    ActionPlanning,
    ActionExecution
}

internal sealed class MappingExecutionFaultEventArgs(
    MappingExecutionStage stage,
    InputMapping? mapping,
    Exception exception) : EventArgs
{
    public MappingExecutionStage Stage { get; } = stage;

    public InputMapping? Mapping { get; } = mapping;

    public Exception Exception { get; } = exception;
}
