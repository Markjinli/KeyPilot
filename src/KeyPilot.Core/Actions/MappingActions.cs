using System.Text.Json.Serialization;
using KeyPilot.Core.Input;

namespace KeyPilot.Core.Actions;

public enum KeyTransition
{
    Press,
    Down,
    Up
}

/// <summary>Base type for every executable mapping action.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$action")]
[JsonDerivedType(typeof(SendKeyAction), "sendKey")]
[JsonDerivedType(typeof(ShortcutAction), "shortcut")]
[JsonDerivedType(typeof(LaunchProgramAction), "launchProgram")]
[JsonDerivedType(typeof(OpenUriAction), "openUri")]
[JsonDerivedType(typeof(RunScriptAction), "runScript")]
[JsonDerivedType(typeof(EmitSpecialKeyAction), "emitSpecialKey")]
[JsonDerivedType(typeof(MediaControlAction), "mediaControl")]
[JsonDerivedType(typeof(VolumeControlAction), "volumeControl")]
[JsonDerivedType(typeof(CopyInputToOutputAction), "copyInputToOutput")]
[JsonDerivedType(typeof(DelayAction), "delay")]
[JsonDerivedType(typeof(MacroAction), "macro")]
public abstract record MappingAction;

/// <summary>Sends one control as a press, key-down, or key-up operation.</summary>
public sealed record SendKeyAction : MappingAction
{
    public InputControlId Target { get; init; } = new();

    public KeyTransition Transition { get; init; } = KeyTransition.Press;

    /// <summary>Duration between down and up when Transition is Press.</summary>
    public int HoldMilliseconds { get; init; } = 30;
}

/// <summary>Presses a set of controls together, such as Ctrl+Alt+=.</summary>
public sealed record ShortcutAction : MappingAction
{
    public List<InputControlId> Keys { get; init; } = new();

    public int HoldMilliseconds { get; init; } = 30;
}

public sealed record LaunchProgramAction : MappingAction
{
    public string FilePath { get; init; } = string.Empty;

    public string? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }
}

public sealed record OpenUriAction : MappingAction
{
    public string Uri { get; init; } = string.Empty;
}

public sealed record RunScriptAction : MappingAction
{
    public string ScriptPath { get; init; } = string.Empty;

    public string? Arguments { get; init; }
}

/// <summary>Re-emits the captured input bound to one of the ten special-key slots.</summary>
public sealed record EmitSpecialKeyAction : MappingAction
{
    public int SlotNumber { get; init; }
}

public sealed record DelayAction : MappingAction
{
    public int Milliseconds { get; init; }
}

public sealed record MacroAction : MappingAction
{
    public List<MappingAction> Steps { get; init; } = new();
}
