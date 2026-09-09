using KeyPilot.Core.Actions;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>
/// Sequentially dispatches a precomputed plan to injected ports. It contains no direct process,
/// shell, script, network, or native-input calls.
/// </summary>
public sealed class WindowsActionExecutor
{
    private const int MaximumDelayMilliseconds = 600_000;

    private readonly IInputInjectionBackend _inputBackend;
    private readonly IExternalActionBackend _externalBackend;
    private readonly IActionDelayScheduler _delayScheduler;
    private readonly ISystemControlBackend _systemControlBackend;
    private readonly IGamepadInjectionBackend? _gamepadBackend;
    private readonly InputInjectionMarker _originMarker;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public WindowsActionExecutor(
        IInputInjectionBackend inputBackend,
        IExternalActionBackend externalBackend,
        IActionDelayScheduler delayScheduler,
        InputInjectionMarker originMarker)
        : this(
            inputBackend,
            externalBackend,
            delayScheduler,
            UnsupportedSystemControlBackend.Instance,
            originMarker)
    {
    }

    public WindowsActionExecutor(
        IInputInjectionBackend inputBackend,
        IExternalActionBackend externalBackend,
        IActionDelayScheduler delayScheduler,
        ISystemControlBackend systemControlBackend,
        InputInjectionMarker originMarker,
        IGamepadInjectionBackend? gamepadBackend = null)
    {
        _inputBackend = inputBackend ?? throw new ArgumentNullException(nameof(inputBackend));
        _externalBackend = externalBackend ?? throw new ArgumentNullException(nameof(externalBackend));
        _delayScheduler = delayScheduler ?? throw new ArgumentNullException(nameof(delayScheduler));
        _systemControlBackend = systemControlBackend
            ?? throw new ArgumentNullException(nameof(systemControlBackend));
        _gamepadBackend = gamepadBackend;
        if (originMarker.Value == 0)
        {
            throw new ArgumentException("A non-zero process-local marker is required.", nameof(originMarker));
        }

        _originMarker = originMarker;
    }

    public async Task ExecuteAsync(ActionPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Preflight(plan);

        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var heldTargets = new List<InputInjectionTarget>();
            try
            {
                foreach (var operation in plan.Operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DispatchAsync(operation, heldTargets, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // A failed/cancelled shortcut must not strand modifiers. Cleanup deliberately
                // ignores the cancelled token and makes one best-effort release per held target.
                await ReleaseHeldTargetsAsync(heldTargets).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async ValueTask DispatchAsync(
        ActionPlanOperation operation,
        List<InputInjectionTarget> heldTargets,
        CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case InjectInputOperation injection:
                // Track Down before crossing the backend boundary. If a backend reports failure
                // after partially injecting, cleanup still has enough information to send Up.
                if (injection.Phase == InputInjectionPhase.Down)
                {
                    heldTargets.Add(injection.Target);
                }

                var request = new InputInjectionRequest(
                    injection.Target,
                    injection.Phase,
                    _originMarker);
                if (IsGamepadTarget(injection.Target))
                {
                    if (_gamepadBackend is null)
                    {
                        throw new InvalidOperationException(
                            "gamepad output is unavailable because no virtual gamepad backend is installed.");
                    }

                    await _gamepadBackend.InjectAsync(request, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _inputBackend.InjectAsync(request, cancellationToken).ConfigureAwait(false);
                }
                if (injection.Phase == InputInjectionPhase.Up)
                {
                    RemoveHeldTarget(heldTargets, injection.Target);
                }
                break;

            case DelayOperation delay:
                await _delayScheduler.DelayAsync(delay.Milliseconds, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case LaunchProgramOperation launch:
                await _externalBackend.LaunchProgramAsync(launch, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case OpenUriOperation uri:
                await _externalBackend.OpenUriAsync(uri, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case RunScriptOperation script:
                await _externalBackend.RunScriptAsync(script, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MediaControlOperation media:
                await _systemControlBackend.ExecuteMediaControlAsync(
                        media,
                        _originMarker,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            case VolumeControlOperation volume:
                await _systemControlBackend.ExecuteVolumeControlAsync(
                        volume,
                        _originMarker,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                // Preflight guarantees this branch is unreachable before any dispatch starts.
                throw new InvalidOperationException(
                    $"Unsupported action-plan operation {operation.GetType().Name}.");
        }
    }

    private static void RemoveHeldTarget(
        List<InputInjectionTarget> heldTargets,
        InputInjectionTarget target)
    {
        for (var index = heldTargets.Count - 1; index >= 0; index--)
        {
            if (Equals(heldTargets[index], target))
            {
                heldTargets.RemoveAt(index);
                return;
            }
        }
    }

    private async Task ReleaseHeldTargetsAsync(List<InputInjectionTarget> heldTargets)
    {
        for (var index = heldTargets.Count - 1; index >= 0; index--)
        {
            try
            {
                var request = new InputInjectionRequest(
                    heldTargets[index],
                    InputInjectionPhase.Up,
                    _originMarker);
                if (IsGamepadTarget(heldTargets[index]))
                {
                    if (_gamepadBackend is not null)
                    {
                        await _gamepadBackend.InjectAsync(request, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    await _inputBackend.InjectAsync(request, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Continue releasing the remaining targets. The original execution failure is
                // more useful to the caller than a secondary best-effort cleanup failure.
            }
        }
    }

    private void Preflight(ActionPlan plan)
    {
        for (var index = 0; index < plan.Operations.Count; index++)
        {
            var operation = plan.Operations[index];
            switch (operation)
            {
                case InjectInputOperation injection:
                    ValidateInjection(injection, index);
                    break;
                case DelayOperation delay when delay.Milliseconds is >= 0 and <= MaximumDelayMilliseconds:
                    break;
                case DelayOperation:
                    throw Invalid(index, "delay is outside the accepted range.");
                case LaunchProgramOperation launch when !string.IsNullOrWhiteSpace(launch.FilePath):
                    ValidateExternal(index, () => _externalBackend.ValidateLaunchProgram(launch));
                    break;
                case LaunchProgramOperation:
                    throw Invalid(index, "program path is empty.");
                case OpenUriOperation uri
                    when ExternalUriNormalizer.TryNormalize(uri.Uri, out _, out _):
                    ValidateExternal(index, () => _externalBackend.ValidateUri(uri));
                    break;
                case OpenUriOperation:
                    throw Invalid(index, "the URI is empty, unsafe, or not a valid domain/protocol address.");
                case RunScriptOperation script when !string.IsNullOrWhiteSpace(script.ScriptPath):
                    ValidateExternal(index, () => _externalBackend.ValidateScript(script));
                    break;
                case RunScriptOperation:
                    throw Invalid(index, "script path is empty.");
                case MediaControlOperation media:
                    ValidateSystem(index, () => _systemControlBackend.ValidateMediaControl(media));
                    break;
                case VolumeControlOperation volume:
                    ValidateSystem(index, () => _systemControlBackend.ValidateVolumeControl(volume));
                    break;
                default:
                    throw Invalid(index, $"operation type {operation.GetType().Name} is unsupported.");
            }
        }
    }

    private static void ValidateExternal(int index, Action validation)
    {
        try
        {
            validation();
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or InvalidOperationException)
        {
            throw Invalid(index, $"external action is invalid: {exception.Message}");
        }
    }

    private static void ValidateSystem(int index, Action validation)
    {
        try
        {
            validation();
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or InvalidOperationException)
        {
            throw Invalid(index, $"system control is invalid: {exception.Message}");
        }
    }

    private void ValidateInjection(InjectInputOperation operation, int index)
    {
        if (operation.Target is null)
        {
            throw Invalid(index, "input target is null.");
        }

        if (!Enum.IsDefined(operation.Phase))
        {
            throw Invalid(index, "input phase is unsupported.");
        }

        switch (operation.Target)
        {
            case ControlInjectionTarget { Control: not null } control when IsGamepadTarget(control):
                if (_gamepadBackend is null)
                {
                    throw Invalid(
                        index,
                        "gamepad output is unavailable because no virtual gamepad backend is installed.");
                }

                try
                {
                    _gamepadBackend.Validate(control.Control);
                    return;
                }
                catch (Exception exception) when (exception is ArgumentException
                                                      or NotSupportedException
                                                      or InvalidOperationException)
                {
                    throw Invalid(index, exception.Message);
                }
            case ControlInjectionTarget { Control: not null }:
            case CapturedInputInjectionTarget { Source: not null }:
                try
                {
                    KeyboardSendInputEncoder.ValidateTarget(operation.Target);
                    return;
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                {
                    throw Invalid(index, $"input target cannot be encoded by SendInput: {exception.Message}");
                }
            default:
                throw Invalid(index, "input target type or payload is unsupported.");
        }
    }

    private static bool IsGamepadTarget(InputInjectionTarget target) =>
        target is ControlInjectionTarget
        {
            Control.Kind: InputControlKind.GamepadButton
                or InputControlKind.GamepadAxisDirection
                or InputControlKind.GamepadRotation
        };

    private static InvalidOperationException Invalid(int index, string message) =>
        new($"Invalid action plan at operation {index}: {message}");
}
