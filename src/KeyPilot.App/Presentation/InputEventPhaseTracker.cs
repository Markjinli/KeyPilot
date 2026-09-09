using KeyPilot.Core.Input;

namespace KeyPilot.App.Presentation;

/// <summary>
/// Converts repeated physical down packets into explicit Repeated events. A release always clears
/// the held identity, including a release observed after the app started mid-press.
/// </summary>
internal sealed class InputEventPhaseTracker
{
    private readonly HashSet<string> _heldSources = new(StringComparer.Ordinal);

    public InputEventPhase Observe(InputSource source, bool isPressed)
    {
        ArgumentNullException.ThrowIfNull(source);
        var key = source.CanonicalKey;
        if (!isPressed)
        {
            _heldSources.Remove(key);
            return InputEventPhase.Released;
        }

        return _heldSources.Add(key)
            ? InputEventPhase.Pressed
            : InputEventPhase.Repeated;
    }

    public void Reset() => _heldSources.Clear();
}
