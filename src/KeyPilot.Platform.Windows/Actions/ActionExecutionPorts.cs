using KeyPilot.Core.Actions;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>One marked request for a backend such as SendInput or a future virtual HID path.</summary>
public sealed record InputInjectionRequest(
    InputInjectionTarget Target,
    InputInjectionPhase Phase,
    InputInjectionMarker OriginMarker);

/// <summary>
/// Platform injection port. No native injection implementation is supplied at this stage, so
/// tests and callers must explicitly provide the backend they intend to use.
/// </summary>
public interface IInputInjectionBackend
{
    ValueTask InjectAsync(InputInjectionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// User-session external-action port. Implementations own all allow-list, quoting, shell, and
/// user-consent policy; the executor never calls Process.Start or ShellExecute directly.
/// </summary>
public interface IExternalActionBackend
{
    /// <summary>
    /// Performs the complete, side-effect-free policy validation that would precede a launch.
    /// The executor calls every validation method for the whole plan before dispatching anything.
    /// </summary>
    void ValidateLaunchProgram(LaunchProgramOperation operation);

    /// <summary>Performs complete, side-effect-free URI validation.</summary>
    void ValidateUri(OpenUriOperation operation);

    /// <summary>Performs complete, side-effect-free script and argument validation.</summary>
    void ValidateScript(RunScriptOperation operation);

    ValueTask LaunchProgramAsync(
        LaunchProgramOperation operation,
        CancellationToken cancellationToken);

    ValueTask OpenUriAsync(
        OpenUriOperation operation,
        CancellationToken cancellationToken);

    ValueTask RunScriptAsync(
        RunScriptOperation operation,
        CancellationToken cancellationToken);
}

/// <summary>Injectable clock boundary used by holds, macros, and deterministic unit tests.</summary>
public interface IActionDelayScheduler
{
    ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken);
}
