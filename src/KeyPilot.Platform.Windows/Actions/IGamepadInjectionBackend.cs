using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>Optional virtual Xbox 360 output. Missing ViGEmBus keeps capture-only fail-closed.</summary>
public interface IGamepadInjectionBackend
{
    bool IsAvailable { get; }

    string UnavailableReason { get; }

    bool IsConnected { get; }

    void Validate(InputControlId control);

    ValueTask InjectAsync(InputInjectionRequest request, CancellationToken cancellationToken);

    void Disconnect();
}
