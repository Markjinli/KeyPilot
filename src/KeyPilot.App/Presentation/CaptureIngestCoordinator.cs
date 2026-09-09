using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;

namespace KeyPilot.App.Presentation;

/// <summary>
/// Owns the unsuppressed capture→event conversion used by XInput, DualShock HID, RC003, and
/// opaque HID. Keyboard suppression still uses <see cref="MappingExecutionCoordinator.SubmitTracked"/>.
/// </summary>
internal sealed class CaptureIngestCoordinator
{
    private readonly InputEventPhaseTracker _phaseTracker;
    private readonly MappingExecutionCoordinator _mapping;
    private readonly Func<KeyPilotConfiguration> _runtimeConfiguration;
    private readonly Func<InputEvent, bool> _observeChord;
    private readonly Action<InputSource, string> _warnSuppression;
    private long _sequence;

    public CaptureIngestCoordinator(
        InputEventPhaseTracker phaseTracker,
        MappingExecutionCoordinator mapping,
        Func<KeyPilotConfiguration> runtimeConfiguration,
        Func<InputEvent, bool> observeChord,
        Action<InputSource, string> warnSuppression)
    {
        _phaseTracker = phaseTracker ?? throw new ArgumentNullException(nameof(phaseTracker));
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        _runtimeConfiguration = runtimeConfiguration ?? throw new ArgumentNullException(nameof(runtimeConfiguration));
        _observeChord = observeChord ?? throw new ArgumentNullException(nameof(observeChord));
        _warnSuppression = warnSuppression ?? throw new ArgumentNullException(nameof(warnSuppression));
    }

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public InputEvent Observe(
        InputSource source,
        bool isPressed,
        DateTimeOffset timestampUtc,
        string suppressionFamily)
    {
        ArgumentNullException.ThrowIfNull(source);
        var phase = _phaseTracker.Observe(source, isPressed);
        var normalized = new InputEvent
        {
            Source = source,
            Phase = phase,
            TimestampUtc = timestampUtc,
            SequenceNumber = NextSequence()
        };

        if (phase == InputEventPhase.Pressed &&
            MappingDispatchPolicy.HasEnabledSuppressedMapping(_runtimeConfiguration(), source))
        {
            _warnSuppression(source, suppressionFamily);
        }

        if (!_observeChord(normalized))
        {
            _mapping.TrySubmitPassThrough(normalized);
        }

        return normalized;
    }

    public void ResetTopology()
    {
        _phaseTracker.Reset();
        _mapping.TryResetInputState();
    }
}
