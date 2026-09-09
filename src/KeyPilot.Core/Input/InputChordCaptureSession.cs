namespace KeyPilot.Core.Input;

/// <summary>Lifecycle of an explicitly armed input-chord recording session.</summary>
public enum InputChordCaptureState
{
    Idle,
    Armed,
    Completed,
    Cancelled,
    Expired
}

/// <summary>
/// Pure recorder that converts the first press of two to eight atomic controls into one
/// order-independent composite input source.
/// </summary>
/// <remarks>
/// The caller must explicitly call <see cref="Arm"/>. A one-key press/release is intentionally
/// kept pending so the user can add a second member. Once at least two distinct members have been
/// captured, releasing every captured key completes the session.
/// </remarks>
public sealed class InputChordCaptureSession
{
    public const int MaximumMemberCount = 8;

    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, InputSource> _members =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _downMemberKeys =
        new(StringComparer.Ordinal);

    private DateTimeOffset? _expiresAtUtc;

    public InputChordCaptureState State { get; private set; } = InputChordCaptureState.Idle;

    public DateTimeOffset? ExpiresAtUtc => _expiresAtUtc;

    /// <summary>Members in first-captured order. A new snapshot is returned to callers.</summary>
    public IReadOnlyList<InputSource> CurrentMembers => _members.Values.ToArray();

    /// <summary>The completed composite source, or null until recording succeeds.</summary>
    public InputSource? CompletedSource { get; private set; }

    /// <summary>Starts a fresh ten-second recording window and discards any previous result.</summary>
    public void Arm(DateTimeOffset armedAtUtc)
    {
        _members.Clear();
        _downMemberKeys.Clear();
        CompletedSource = null;
        _expiresAtUtc = armedAtUtc + CaptureTimeout;
        State = InputChordCaptureState.Armed;
    }

    /// <summary>
    /// Observes one normalized atomic edge and returns the session state after processing it.
    /// Injected input and repeat edges never become chord members.
    /// </summary>
    public InputChordCaptureState Observe(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        ArgumentNullException.ThrowIfNull(inputEvent.Source);

        if (State != InputChordCaptureState.Armed)
        {
            return State;
        }

        if (TryExpire(inputEvent.TimestampUtc))
        {
            return State;
        }

        if (inputEvent.IsInjected
            || inputEvent.Phase == InputEventPhase.Repeated
            || inputEvent.Source.Device is null
            || inputEvent.Source.Control is null
            || inputEvent.Source.Device.Kind == InputDeviceKind.Composite
            || inputEvent.Source.Control.Kind is InputControlKind.InputChord or InputControlKind.InputSequence)
        {
            return State;
        }

        var memberKey = inputEvent.Source.CanonicalKey;
        if (inputEvent.Phase == InputEventPhase.Pressed)
        {
            if (!_members.ContainsKey(memberKey) && _members.Count < MaximumMemberCount)
            {
                _members.Add(memberKey, inputEvent.Source);
            }

            if (_members.ContainsKey(memberKey))
            {
                _downMemberKeys.Add(memberKey);
            }

            return State;
        }

        if (inputEvent.Phase != InputEventPhase.Released || !_members.ContainsKey(memberKey))
        {
            return State;
        }

        _downMemberKeys.Remove(memberKey);
        if (_members.Count < 2 || _downMemberKeys.Count != 0)
        {
            return State;
        }

        CompletedSource = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Composite,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.InputChord,
                Code = 1
            },
            ChordMembers = _members.Values.ToList()
        };
        _expiresAtUtc = null;
        State = InputChordCaptureState.Completed;
        return State;
    }

    /// <summary>Cancels an active session. Completed and expired sessions remain unchanged.</summary>
    public void Cancel()
    {
        if (State != InputChordCaptureState.Armed)
        {
            return;
        }

        _downMemberKeys.Clear();
        _expiresAtUtc = null;
        State = InputChordCaptureState.Cancelled;
    }

    /// <summary>Expires an armed session when its ten-second deadline has been reached.</summary>
    public bool TryExpire(DateTimeOffset timestampUtc)
    {
        if (State != InputChordCaptureState.Armed
            || !_expiresAtUtc.HasValue
            || timestampUtc < _expiresAtUtc.Value)
        {
            return false;
        }

        _downMemberKeys.Clear();
        _expiresAtUtc = null;
        State = InputChordCaptureState.Expired;
        return true;
    }
}

/// <summary>Lifecycle of a fixed-window input-pattern recording session.</summary>
public enum InputPatternCaptureState
{
    Idle,
    Armed,
    Completed,
    Cancelled,
    Rejected
}

/// <summary>
/// Records up to 32 physical down/up edges during a fixed two-second window. Patterns whose
/// distinct controls are all held at the same instant become unordered chords; every other valid
/// pattern remains an ordered sequence with relative timing.
/// </summary>
public sealed class InputPatternCaptureSession
{
    public const int MaximumEdgeCount = 32;

    public static readonly TimeSpan CaptureDuration = TimeSpan.FromSeconds(2);

    private readonly List<CapturedEdge> _edges = new();
    private readonly HashSet<string> _downSources = new(StringComparer.Ordinal);
    private DateTimeOffset? _completesAtUtc;
    private DateTimeOffset? _firstEdgeAtUtc;
    private bool _overflowed;

    public InputPatternCaptureState State { get; private set; } = InputPatternCaptureState.Idle;

    public DateTimeOffset? DeadlineUtc => _completesAtUtc;

    public int RecordedEdgeCount => _edges.Count;

    public InputSource? CompletedSource { get; private set; }

    /// <summary>A snapshot of accepted edges, normalized relative to the first edge.</summary>
    public IReadOnlyList<InputPatternStep> CurrentSteps => _edges
        .Select(static edge => new InputPatternStep
        {
            Source = edge.Source,
            Phase = edge.Phase,
            OffsetMilliseconds = edge.OffsetMilliseconds
        })
        .ToArray();

    public void Arm(DateTimeOffset armedAtUtc)
    {
        _edges.Clear();
        _downSources.Clear();
        _firstEdgeAtUtc = null;
        _overflowed = false;
        CompletedSource = null;
        _completesAtUtc = armedAtUtc + CaptureDuration;
        State = InputPatternCaptureState.Armed;
    }

    /// <summary>
    /// Observes one atomic edge. Repeats, injected input, stray releases and logical composite
    /// sources are ignored and never consume the 32-edge budget.
    /// </summary>
    public InputPatternCaptureState Observe(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        ArgumentNullException.ThrowIfNull(inputEvent.Source);

        if (State != InputPatternCaptureState.Armed)
        {
            return State;
        }

        if (TryComplete(inputEvent.TimestampUtc) != InputPatternCaptureState.Armed)
        {
            return State;
        }

        if (inputEvent.IsInjected
            || inputEvent.Phase == InputEventPhase.Repeated
            || inputEvent.Source.Device is null
            || inputEvent.Source.Control is null
            || inputEvent.Source.Device.Kind == InputDeviceKind.Composite
            || inputEvent.Source.Control.Kind is InputControlKind.InputChord or InputControlKind.InputSequence)
        {
            return State;
        }

        var sourceKey = inputEvent.Source.CanonicalKey;
        if (inputEvent.Phase == InputEventPhase.Pressed)
        {
            if (_downSources.Contains(sourceKey))
            {
                return State;
            }

            _downSources.Add(sourceKey);
            AddEdge(inputEvent.Source, inputEvent.Phase, inputEvent.TimestampUtc);
            return State;
        }

        if (inputEvent.Phase != InputEventPhase.Released || !_downSources.Remove(sourceKey))
        {
            return State;
        }

        AddEdge(inputEvent.Source, inputEvent.Phase, inputEvent.TimestampUtc);
        return State;
    }

    /// <summary>
    /// Completes the recording once the two-second deadline is reached. The return value means
    /// the current state, allowing a timer to observe Completed or Rejected with no new input.
    /// </summary>
    public InputPatternCaptureState TryComplete(DateTimeOffset timestampUtc)
    {
        if (State != InputPatternCaptureState.Armed
            || !_completesAtUtc.HasValue
            || timestampUtc < _completesAtUtc.Value)
        {
            return State;
        }

        _completesAtUtc = null;
        if (_overflowed || _edges.Count < 2 || _downSources.Count != 0)
        {
            _downSources.Clear();
            State = InputPatternCaptureState.Rejected;
            return State;
        }

        CompletedSource = BuildCompletedSource();
        State = InputPatternCaptureState.Completed;
        return State;
    }

    public void Cancel()
    {
        if (State != InputPatternCaptureState.Armed)
        {
            return;
        }

        _downSources.Clear();
        _completesAtUtc = null;
        State = InputPatternCaptureState.Cancelled;
    }

    private void AddEdge(InputSource source, InputEventPhase phase, DateTimeOffset timestampUtc)
    {
        if (_edges.Count >= MaximumEdgeCount)
        {
            _overflowed = true;
            return;
        }

        _firstEdgeAtUtc ??= timestampUtc;
        var offset = timestampUtc - _firstEdgeAtUtc.Value;
        var offsetMilliseconds = (int)Math.Clamp(
            Math.Round(offset.TotalMilliseconds, MidpointRounding.AwayFromZero),
            0,
            CaptureDuration.TotalMilliseconds);
        _edges.Add(new CapturedEdge(source, phase, offsetMilliseconds));
    }

    private InputSource BuildCompletedSource()
    {
        var uniqueSources = new Dictionary<string, InputSource>(StringComparer.Ordinal);
        var down = new HashSet<string>(StringComparer.Ordinal);
        var maximumOverlap = 0;
        foreach (var edge in _edges)
        {
            var key = edge.Source.CanonicalKey;
            uniqueSources.TryAdd(key, edge.Source);
            if (edge.Phase == InputEventPhase.Pressed)
            {
                down.Add(key);
                maximumOverlap = Math.Max(maximumOverlap, down.Count);
            }
            else
            {
                down.Remove(key);
            }
        }

        // Collapsing to a chord must not discard repeated clicks that happened elsewhere in the
        // recording. Each distinct control therefore has exactly one down/up pair in addition to
        // sharing a common held interval with every other member.
        if (uniqueSources.Count >= 2
            && _edges.Count == uniqueSources.Count * 2
            && maximumOverlap == uniqueSources.Count)
        {
            return new InputSource
            {
                Device = CompositeDevice(),
                Control = new InputControlId
                {
                    Kind = InputControlKind.InputChord,
                    Code = 1
                },
                ChordMembers = uniqueSources.Values.ToList()
            };
        }

        return new InputSource
        {
            Device = CompositeDevice(),
            Control = new InputControlId
            {
                Kind = InputControlKind.InputSequence,
                Code = 1
            },
            PatternSteps = _edges
                .Select(static edge => new InputPatternStep
                {
                    Source = edge.Source,
                    Phase = edge.Phase,
                    OffsetMilliseconds = edge.OffsetMilliseconds
                })
                .ToList()
        };
    }

    private static InputDeviceSelector CompositeDevice() => new()
    {
        Kind = InputDeviceKind.Composite,
        MatchMode = DeviceMatchMode.AnyOfKind
    };

    private sealed record CapturedEdge(
        InputSource Source,
        InputEventPhase Phase,
        int OffsetMilliseconds);
}
