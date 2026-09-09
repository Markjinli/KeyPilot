using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;

namespace KeyPilot.Core.Triggers;

/// <summary>
/// Describes a trigger that became active. This type deliberately carries the configured
/// mapping but never executes its action.
/// </summary>
public sealed record MappingTriggerActivation
{
    public InputMapping Mapping { get; init; } = new();

    /// <summary>The physical event that started or completed this activation.</summary>
    public InputEvent OriginatingEvent { get; init; } = new();

    /// <summary>
    /// Logical activation time. For a long press this is the exact threshold, which can be
    /// earlier than the call that observed the elapsed time.
    /// </summary>
    public DateTimeOffset TriggeredAtUtc { get; init; }
}

/// <summary>
/// Pure, single-threaded trigger recognizer for normalized input events.
/// </summary>
/// <remarks>
/// Events must be supplied in nondecreasing timestamp order. No wall clock or operating-system
/// timer is read: callers advance time either by processing an event or by calling
/// <see cref="AdvanceTo"/>. A single press is completed on release, while KeyDown and KeyUp map
/// directly to their respective physical edges. Repeated and duplicate press events never create
/// another activation.
/// </remarks>
public sealed class MappingTriggerStateMachine
{
    private static readonly TimeSpan ChordPressWindow = TimeSpan.FromMilliseconds(250);

    private readonly Registration[] _registrations;
    private readonly Dictionary<StateKey, TriggerState> _states = new();
    private readonly Dictionary<int, ChordState> _chordStates = new();
    private readonly Dictionary<int, SequenceState> _sequenceStates = new();
    private DateTimeOffset? _currentTimeUtc;

    public MappingTriggerStateMachine(IEnumerable<InputMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        _registrations = mappings
            .Where(static mapping => mapping is not null && mapping.IsEnabled)
            .Select(static (mapping, order) => new Registration(mapping, order))
            .ToArray();
    }

    /// <summary>
    /// Returns the next long-press or double-press deadline, if one is currently armed.
    /// </summary>
    public DateTimeOffset? NextDeadlineUtc
    {
        get
        {
            DateTimeOffset? next = null;
            foreach (var state in _states.Values)
            {
                var deadline = state.GetDeadlineUtc();
                if (deadline.HasValue && (!next.HasValue || deadline.Value < next.Value))
                {
                    next = deadline;
                }
            }

            return next;
        }
    }

    /// <summary>
    /// Returns only the unresolved gesture deadline causally armed by one physical event. This is
    /// intentionally narrower than <see cref="NextDeadlineUtc"/> so a tracked input never waits
    /// for an unrelated key's long-press or double-press window.
    /// </summary>
    public DateTimeOffset? GetPendingDeadlineUtc(long originatingSequenceNumber)
    {
        if (originatingSequenceNumber <= 0)
        {
            return null;
        }

        DateTimeOffset? next = null;
        foreach (var state in _states.Values)
        {
            if (state.GetDeadlineOriginSequence() != originatingSequenceNumber)
            {
                continue;
            }

            var deadline = state.GetDeadlineUtc();
            if (deadline.HasValue && (!next.HasValue || deadline.Value < next.Value))
            {
                next = deadline;
            }
        }

        return next;
    }

    /// <summary>Processes one normalized event and returns every activation due at that time.</summary>
    public IReadOnlyList<MappingTriggerActivation> Process(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        ArgumentNullException.ThrowIfNull(inputEvent.Source);

        var activations = AdvanceCore(inputEvent.TimestampUtc);
        if (inputEvent.IsInjected)
        {
            return activations;
        }

        var physicalSourceKey = BuildPhysicalSourceKey(inputEvent.Source);
        foreach (var registration in _registrations)
        {
            if (TryGetSequenceSteps(registration.Mapping.Source, out var patternSteps))
            {
                ProcessSequenceEvent(
                    registration,
                    patternSteps,
                    inputEvent,
                    activations);
                continue;
            }

            if (TryGetChordMembers(registration.Mapping.Source, out var chordMembers))
            {
                ProcessChordEvent(
                    registration,
                    chordMembers,
                    physicalSourceKey,
                    inputEvent,
                    activations);
                continue;
            }

            if (!MatchesSource(registration.Mapping.Source, inputEvent.Source))
            {
                continue;
            }

            ProcessForMapping(registration, physicalSourceKey, inputEvent, activations);
        }

        return activations;
    }

    /// <summary>
    /// Advances logical time without fabricating an input event. This is how a caller observes a
    /// long press while the control remains held.
    /// </summary>
    public IReadOnlyList<MappingTriggerActivation> AdvanceTo(DateTimeOffset timestampUtc) =>
        AdvanceCore(timestampUtc);

    /// <summary>Clears all held and pending click state, for example during a profile switch.</summary>
    public void Reset()
    {
        _states.Clear();
        _chordStates.Clear();
        _sequenceStates.Clear();
        _currentTimeUtc = null;
    }

    private List<MappingTriggerActivation> AdvanceCore(DateTimeOffset timestampUtc)
    {
        EnsureTimeDoesNotMoveBackwards(timestampUtc);
        _currentTimeUtc = timestampUtc;

        // A chord that did not complete in time stays blocked until every participating
        // physical control is released. This prevents a late final key from starting a new
        // attempt while keys from the old attempt are still held.
        foreach (var chordState in _chordStates.Values)
        {
            if (!chordState.HasActivated
                && !chordState.IsBlockedUntilAllReleased
                && chordState.FirstPressedAtUtc.HasValue
                && timestampUtc > chordState.FirstPressedAtUtc.Value + ChordPressWindow)
            {
                chordState.IsBlockedUntilAllReleased = true;
            }
        }

        var expiredSequences = _sequenceStates
            .Where(pair => timestampUtc > pair.Value.ExpiresAtUtc)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (var registrationOrder in expiredSequences)
        {
            _sequenceStates.Remove(registrationOrder);
        }

        var due = new List<(DateTimeOffset Deadline, int Order, string SourceKey, MappingTriggerActivation Activation)>();
        var expired = new List<StateKey>();

        foreach (var pair in _states)
        {
            var state = pair.Value;
            if (state.Registration.Mapping.Trigger.Kind == MappingTriggerKind.LongPress
                && state.IsDown
                && !state.LongPressActivated)
            {
                var deadline = state.PressedAtUtc
                    + TimeSpan.FromMilliseconds(state.Registration.Mapping.Trigger.LongPressMilliseconds);
                if (deadline <= timestampUtc)
                {
                    state.LongPressActivated = true;
                    due.Add((
                        deadline,
                        state.Registration.Order,
                        pair.Key.PhysicalSourceKey,
                        CreateActivation(state.Registration.Mapping, state.PressEvent, deadline)));
                }
            }

            if (state.Registration.Mapping.Trigger.Kind == MappingTriggerKind.DoublePress
                && !state.IsDown
                && state.DoublePressDeadlineUtc.HasValue
                && state.DoublePressDeadlineUtc.Value < timestampUtc)
            {
                expired.Add(pair.Key);
            }
        }

        foreach (var key in expired)
        {
            _states.Remove(key);
        }

        return due
            .OrderBy(static item => item.Deadline)
            .ThenBy(static item => item.Order)
            .ThenBy(static item => item.SourceKey, StringComparer.Ordinal)
            .Select(static item => item.Activation)
            .ToList();
    }

    private void ProcessSequenceEvent(
        Registration registration,
        IReadOnlyList<InputPatternStep> steps,
        InputEvent inputEvent,
        ICollection<MappingTriggerActivation> activations)
    {
        // Typematic repeats are not recorded pattern edges and therefore neither advance nor
        // invalidate a candidate. Every other unrelated physical edge safely restarts matching.
        if (inputEvent.Phase == InputEventPhase.Repeated)
        {
            return;
        }

        _sequenceStates.TryGetValue(registration.Order, out var state);
        if (state is not null)
        {
            var expected = steps[state.NextStepIndex];
            var actualOffset = inputEvent.TimestampUtc - state.StartedAtUtc;
            if (MatchesPatternStep(expected, inputEvent.Source, inputEvent.Phase)
                && IsWithinSequenceTiming(
                    expected.OffsetMilliseconds,
                    actualOffset.TotalMilliseconds))
            {
                state.NextStepIndex++;
                if (state.NextStepIndex == steps.Count)
                {
                    _sequenceStates.Remove(registration.Order);
                    EmitLogicalClick(registration, inputEvent, activations);
                }

                return;
            }

            _sequenceStates.Remove(registration.Order);
        }

        // The mismatching edge may itself be the first edge of a new attempt. This gives the
        // recognizer deterministic overlap recovery without accepting any unrelated prefix.
        if (!MatchesPatternStep(steps[0], inputEvent.Source, inputEvent.Phase))
        {
            return;
        }

        _sequenceStates[registration.Order] = new SequenceState
        {
            StartedAtUtc = inputEvent.TimestampUtc,
            ExpiresAtUtc = inputEvent.TimestampUtc + SequenceMaximumDuration(steps),
            NextStepIndex = 1
        };
    }

    private void EmitLogicalClick(
        Registration registration,
        InputEvent finalPhysicalEvent,
        ICollection<MappingTriggerActivation> activations)
    {
        var physicalSourceKey = BuildSequencePhysicalSourceKey(registration.Mapping.Source);
        var logicalPress = finalPhysicalEvent with
        {
            Source = registration.Mapping.Source,
            Phase = InputEventPhase.Pressed
        };
        ProcessForMapping(
            registration,
            physicalSourceKey,
            logicalPress,
            activations);

        var logicalRelease = logicalPress with { Phase = InputEventPhase.Released };
        ProcessForMapping(
            registration,
            physicalSourceKey,
            logicalRelease,
            activations);
    }

    private static bool MatchesPatternStep(
        InputPatternStep expected,
        InputSource actualSource,
        InputEventPhase actualPhase) =>
        expected.Phase == actualPhase && MatchesSource(expected.Source, actualSource);

    private static bool IsWithinSequenceTiming(
        int expectedOffsetMilliseconds,
        double actualOffsetMilliseconds)
    {
        var tolerance = SequenceTimingTolerance(expectedOffsetMilliseconds);
        return Math.Abs(actualOffsetMilliseconds - expectedOffsetMilliseconds) <= tolerance;
    }

    private static TimeSpan SequenceMaximumDuration(IReadOnlyList<InputPatternStep> steps)
    {
        var lastOffset = steps[^1].OffsetMilliseconds;
        return TimeSpan.FromMilliseconds(lastOffset + SequenceTimingTolerance(lastOffset));
    }

    private static int SequenceTimingTolerance(int expectedOffsetMilliseconds) =>
        Math.Clamp(expectedOffsetMilliseconds / 3, 150, 400);

    private void ProcessChordEvent(
        Registration registration,
        IReadOnlyList<InputSource> members,
        string physicalSourceKey,
        InputEvent inputEvent,
        ICollection<MappingTriggerActivation> activations)
    {
        // Chords are assembled only from atomic physical edges. Repeated edges must not extend
        // the 250 ms window or fabricate another logical press.
        if (inputEvent.Phase == InputEventPhase.Repeated
            || inputEvent.Source.Control.Kind == InputControlKind.InputChord
            || inputEvent.Source.Device.Kind == InputDeviceKind.Composite)
        {
            return;
        }

        var matchingMemberIndexes = GetMatchingMemberIndexes(members, inputEvent.Source);
        if (matchingMemberIndexes.Count == 0)
        {
            return;
        }

        if (!_chordStates.TryGetValue(registration.Order, out var chordState))
        {
            chordState = new ChordState();
            _chordStates.Add(registration.Order, chordState);
        }

        if (inputEvent.Phase == InputEventPhase.Pressed)
        {
            // A duplicate press from the same physical control is typematic noise, even when it
            // was normalized as Pressed instead of Repeated.
            if (!chordState.DownSources.TryAdd(physicalSourceKey, inputEvent.Source))
            {
                return;
            }

            chordState.FirstPressedAtUtc ??= inputEvent.TimestampUtc;
            if (chordState.HasActivated || chordState.IsBlockedUntilAllReleased)
            {
                return;
            }

            if (inputEvent.TimestampUtc > chordState.FirstPressedAtUtc.Value + ChordPressWindow)
            {
                chordState.IsBlockedUntilAllReleased = true;
                return;
            }

            if (!TryAssignEveryMember(members, chordState.DownSources, out var activationKeys))
            {
                return;
            }

            chordState.HasActivated = true;
            chordState.ActivationPhysicalKeys.Clear();
            chordState.ActivationPhysicalKeys.UnionWith(activationKeys);

            var logicalPress = inputEvent with
            {
                Source = registration.Mapping.Source,
                Phase = InputEventPhase.Pressed
            };
            ProcessForMapping(
                registration,
                BuildChordPhysicalSourceKey(registration.Mapping.Source),
                logicalPress,
                activations);
            return;
        }

        if (inputEvent.Phase != InputEventPhase.Released
            || !chordState.DownSources.Remove(physicalSourceKey))
        {
            return;
        }

        // Only a release belonging to the physical controls that completed this activation can
        // release it. A same-named key released on another device is deliberately harmless.
        if (chordState.HasActivated
            && chordState.ActivationPhysicalKeys.Contains(physicalSourceKey))
        {
            chordState.HasActivated = false;
            chordState.IsBlockedUntilAllReleased = true;

            var logicalRelease = inputEvent with
            {
                Source = registration.Mapping.Source,
                Phase = InputEventPhase.Released
            };
            ProcessForMapping(
                registration,
                BuildChordPhysicalSourceKey(registration.Mapping.Source),
                logicalRelease,
                activations);
        }

        if (chordState.DownSources.Count == 0)
        {
            _chordStates.Remove(registration.Order);
        }
    }

    private static bool TryGetChordMembers(
        InputSource source,
        out IReadOnlyList<InputSource> members)
    {
        if (source.Control.Kind == InputControlKind.InputChord
            && source.Device.Kind == InputDeviceKind.Composite
            && source.ChordMembers is { Count: >= 2 and <= 8 }
            && source.ChordMembers.All(static member =>
                member is not null
                && member.Device is not null
                && member.Control is not null
                && member.Device.Kind != InputDeviceKind.Composite
                && member.Control.Kind != InputControlKind.InputChord))
        {
            members = source.ChordMembers;
            return true;
        }

        members = Array.Empty<InputSource>();
        return false;
    }

    private static bool TryGetSequenceSteps(
        InputSource source,
        out IReadOnlyList<InputPatternStep> steps)
    {
        if (source.Control.Kind == InputControlKind.InputSequence
            && source.Device.Kind == InputDeviceKind.Composite
            && source.PatternSteps is
            {
                Count: >= 2 and <= InputPatternCaptureSession.MaximumEdgeCount
            }
            && source.PatternSteps.All(static step =>
                step is not null
                && step.Phase != InputEventPhase.Repeated
                && step.Source is not null
                && step.Source.Device is not null
                && step.Source.Control is not null
                && step.Source.Device.Kind != InputDeviceKind.Composite
                && step.Source.Control.Kind is not InputControlKind.InputChord
                    and not InputControlKind.InputSequence))
        {
            steps = source.PatternSteps;
            return true;
        }

        steps = Array.Empty<InputPatternStep>();
        return false;
    }

    private static IReadOnlyList<int> GetMatchingMemberIndexes(
        IReadOnlyList<InputSource> members,
        InputSource actual)
    {
        var matches = new List<int>(members.Count);
        for (var index = 0; index < members.Count; index++)
        {
            if (MatchesSource(members[index], actual))
            {
                matches.Add(index);
            }
        }

        return matches;
    }

    /// <summary>
    /// Finds a one-to-one assignment between configured members and currently held physical
    /// sources. The small (maximum eight member) bipartite match also handles chords containing
    /// the same control on two exact devices without allowing one press to satisfy both members.
    /// </summary>
    private static bool TryAssignEveryMember(
        IReadOnlyList<InputSource> members,
        IReadOnlyDictionary<string, InputSource> downSources,
        out IReadOnlyCollection<string> assignedPhysicalKeys)
    {
        if (downSources.Count < members.Count)
        {
            assignedPhysicalKeys = Array.Empty<string>();
            return false;
        }

        var physicalEntries = downSources
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        var physicalToMember = Enumerable.Repeat(-1, physicalEntries.Length).ToArray();

        bool TryAssignMember(int memberIndex, bool[] visitedPhysical)
        {
            for (var physicalIndex = 0; physicalIndex < physicalEntries.Length; physicalIndex++)
            {
                if (visitedPhysical[physicalIndex]
                    || !MatchesSource(members[memberIndex], physicalEntries[physicalIndex].Value))
                {
                    continue;
                }

                visitedPhysical[physicalIndex] = true;
                if (physicalToMember[physicalIndex] < 0
                    || TryAssignMember(physicalToMember[physicalIndex], visitedPhysical))
                {
                    physicalToMember[physicalIndex] = memberIndex;
                    return true;
                }
            }

            return false;
        }

        // Exact-device members are assigned first, leaving flexible AnyOfKind controls available
        // for the remaining members whenever both selectors overlap.
        var memberOrder = Enumerable.Range(0, members.Count)
            .OrderBy(index => members[index].Device.MatchMode == DeviceMatchMode.ExactDevice ? 0 : 1)
            .ThenBy(index => members[index].CanonicalKey, StringComparer.Ordinal)
            .ToArray();

        foreach (var memberIndex in memberOrder)
        {
            if (!TryAssignMember(memberIndex, new bool[physicalEntries.Length]))
            {
                assignedPhysicalKeys = Array.Empty<string>();
                return false;
            }
        }

        assignedPhysicalKeys = physicalToMember
            .Select((memberIndex, physicalIndex) => (memberIndex, physicalIndex))
            .Where(static item => item.memberIndex >= 0)
            .Select(item => physicalEntries[item.physicalIndex].Key)
            .ToArray();
        return true;
    }

    private static string BuildChordPhysicalSourceKey(InputSource source) =>
        $"CHORD:{source.CanonicalKey}";

    private static string BuildSequencePhysicalSourceKey(InputSource source) =>
        $"SEQUENCE:{source.CanonicalKey}";

    private void ProcessForMapping(
        Registration registration,
        string physicalSourceKey,
        InputEvent inputEvent,
        ICollection<MappingTriggerActivation> activations)
    {
        var key = new StateKey(registration.Order, physicalSourceKey);

        if (inputEvent.Phase == InputEventPhase.Repeated)
        {
            return;
        }

        _states.TryGetValue(key, out var state);
        if (inputEvent.Phase == InputEventPhase.Pressed)
        {
            if (state?.IsDown == true)
            {
                return;
            }

            state ??= new TriggerState(registration);
            state.IsDown = true;
            state.PressedAtUtc = inputEvent.TimestampUtc;
            state.PressEvent = inputEvent;
            state.LongPressActivated = false;

            if (registration.Mapping.Trigger.Kind == MappingTriggerKind.DoublePress)
            {
                state.IsSecondClick = state.DoublePressDeadlineUtc.HasValue
                    && inputEvent.TimestampUtc <= state.DoublePressDeadlineUtc.Value;
                state.DoublePressDeadlineUtc = null;
                state.DeadlineOriginSequence = 0;
            }

            _states[key] = state;

            if (registration.Mapping.Trigger.Kind == MappingTriggerKind.KeyDown)
            {
                activations.Add(CreateActivation(registration.Mapping, inputEvent, inputEvent.TimestampUtc));
            }

            return;
        }

        if (inputEvent.Phase != InputEventPhase.Released || state?.IsDown != true)
        {
            return;
        }

        state.IsDown = false;
        switch (registration.Mapping.Trigger.Kind)
        {
            case MappingTriggerKind.SinglePress:
                activations.Add(CreateActivation(registration.Mapping, inputEvent, inputEvent.TimestampUtc));
                _states.Remove(key);
                break;

            case MappingTriggerKind.DoublePress when state.IsSecondClick:
                activations.Add(CreateActivation(registration.Mapping, inputEvent, inputEvent.TimestampUtc));
                _states.Remove(key);
                break;

            case MappingTriggerKind.DoublePress:
                state.DoublePressDeadlineUtc = inputEvent.TimestampUtc
                    + TimeSpan.FromMilliseconds(registration.Mapping.Trigger.DoublePressWindowMilliseconds);
                state.DeadlineOriginSequence = inputEvent.SequenceNumber;
                state.IsSecondClick = false;
                break;

            case MappingTriggerKind.KeyUp:
                activations.Add(CreateActivation(registration.Mapping, inputEvent, inputEvent.TimestampUtc));
                _states.Remove(key);
                break;

            case MappingTriggerKind.KeyDown:
            case MappingTriggerKind.LongPress:
                _states.Remove(key);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported mapping trigger kind: {registration.Mapping.Trigger.Kind}.");
        }
    }

    private void EnsureTimeDoesNotMoveBackwards(DateTimeOffset timestampUtc)
    {
        if (_currentTimeUtc.HasValue && timestampUtc < _currentTimeUtc.Value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampUtc),
                timestampUtc,
                "Trigger state-machine time cannot move backwards.");
        }
    }

    private static MappingTriggerActivation CreateActivation(
        InputMapping mapping,
        InputEvent originatingEvent,
        DateTimeOffset triggeredAtUtc) =>
        new()
        {
            Mapping = mapping,
            OriginatingEvent = originatingEvent,
            TriggeredAtUtc = triggeredAtUtc
        };

    public static bool MatchesSource(InputSource configured, InputSource actual)
    {
        if (configured is null
            || configured.Device is null
            || configured.Control is null
            || actual.Device is null
            || actual.Control is null
            || configured.Device.Kind != actual.Device.Kind
            || !string.Equals(
                configured.Control.CanonicalKey,
                actual.Control.CanonicalKey,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (configured.Device.MatchMode == DeviceMatchMode.AnyOfKind)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configured.Device.DeviceId)
            && !string.Equals(
                configured.Device.DeviceId.Trim(),
                actual.Device.DeviceId?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return (!configured.Device.VendorId.HasValue
                || configured.Device.VendorId == actual.Device.VendorId)
            && (!configured.Device.ProductId.HasValue
                || configured.Device.ProductId == actual.Device.ProductId);
    }

    private static string BuildPhysicalSourceKey(InputSource source)
    {
        var device = source.Device;
        var deviceId = string.IsNullOrWhiteSpace(device.DeviceId)
            ? "*"
            : device.DeviceId.Trim().ToUpperInvariant();

        return string.Join(
            ":",
            device.Kind,
            deviceId,
            device.VendorId?.ToString("X4") ?? "----",
            device.ProductId?.ToString("X4") ?? "----",
            source.Control.CanonicalKey);
    }

    private readonly record struct StateKey(int RegistrationOrder, string PhysicalSourceKey);

    private sealed record Registration(InputMapping Mapping, int Order);

    private sealed class ChordState
    {
        public Dictionary<string, InputSource> DownSources { get; } =
            new(StringComparer.Ordinal);

        public HashSet<string> ActivationPhysicalKeys { get; } =
            new(StringComparer.Ordinal);

        public DateTimeOffset? FirstPressedAtUtc { get; set; }

        public bool HasActivated { get; set; }

        public bool IsBlockedUntilAllReleased { get; set; }
    }

    private sealed class SequenceState
    {
        public DateTimeOffset StartedAtUtc { get; init; }

        public DateTimeOffset ExpiresAtUtc { get; init; }

        public int NextStepIndex { get; set; }
    }

    private sealed class TriggerState
    {
        public TriggerState(Registration registration)
        {
            Registration = registration;
        }

        public Registration Registration { get; }

        public bool IsDown { get; set; }

        public DateTimeOffset PressedAtUtc { get; set; }

        public InputEvent PressEvent { get; set; } = new();

        public bool LongPressActivated { get; set; }

        public bool IsSecondClick { get; set; }

        public DateTimeOffset? DoublePressDeadlineUtc { get; set; }

        public long DeadlineOriginSequence { get; set; }

        public DateTimeOffset? GetDeadlineUtc()
        {
            if (Registration.Mapping.Trigger.Kind == MappingTriggerKind.LongPress
                && IsDown
                && !LongPressActivated)
            {
                return PressedAtUtc
                    + TimeSpan.FromMilliseconds(Registration.Mapping.Trigger.LongPressMilliseconds);
            }

            return Registration.Mapping.Trigger.Kind == MappingTriggerKind.DoublePress && !IsDown
                ? DoublePressDeadlineUtc
                : null;
        }

        public long GetDeadlineOriginSequence()
        {
            if (Registration.Mapping.Trigger.Kind == MappingTriggerKind.LongPress
                && IsDown
                && !LongPressActivated)
            {
                return PressEvent.SequenceNumber;
            }

            return Registration.Mapping.Trigger.Kind == MappingTriggerKind.DoublePress && !IsDown
                ? DeadlineOriginSequence
                : 0;
        }
    }
}
