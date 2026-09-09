using KeyPilot.Core.Input;

namespace KeyPilot.Core.Actions;

/// <summary>The only two states accepted by an input injection backend.</summary>
public enum InputInjectionPhase
{
    Down,
    Up
}

/// <summary>Base type for an immutable, already-expanded action-plan operation.</summary>
public abstract record ActionPlanOperation;

/// <summary>Identifies what an input operation will emit without choosing a platform API.</summary>
public abstract record InputInjectionTarget
{
    internal abstract string CanonicalKey { get; }
}

public sealed record ControlInjectionTarget(InputControlId Control) : InputInjectionTarget
{
    internal override string CanonicalKey => $"control/{Control.CanonicalKey}";
}

/// <summary>
/// A captured physical identity, used for special-key slots. Whether a concrete backend can
/// reproduce that identity is deliberately decided outside the Core assembly.
/// </summary>
public sealed record CapturedInputInjectionTarget(InputSource Source) : InputInjectionTarget
{
    internal override string CanonicalKey => $"captured/{Source.CanonicalKey}";
}

public sealed record InjectInputOperation(
    InputInjectionTarget Target,
    InputInjectionPhase Phase) : ActionPlanOperation;

public sealed record DelayOperation(int Milliseconds) : ActionPlanOperation;

public sealed record LaunchProgramOperation(
    string FilePath,
    string? Arguments,
    string? WorkingDirectory) : ActionPlanOperation;

public sealed record OpenUriOperation(string Uri) : ActionPlanOperation;

public sealed record RunScriptOperation(
    string ScriptPath,
    string? Arguments) : ActionPlanOperation;

/// <summary>A defensive, read-only snapshot of operations ready for sequential execution.</summary>
public sealed class ActionPlan
{
    private readonly IReadOnlyList<ActionPlanOperation> _operations;

    public ActionPlan(IEnumerable<ActionPlanOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var snapshot = operations.Select(
                operation => operation ?? throw new ArgumentException(
                    "An action plan cannot contain null operations.",
                    nameof(operations)))
            .ToArray();
        _operations = Array.AsReadOnly(snapshot);
    }

    public IReadOnlyList<ActionPlanOperation> Operations => _operations;
}

public sealed class ActionPlanningException : Exception
{
    public ActionPlanningException(string actionPath, string message)
        : base($"Unable to plan {actionPath}: {message}")
    {
        ActionPath = actionPath;
    }

    public string ActionPath { get; }
}
