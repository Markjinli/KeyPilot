using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;

namespace KeyPilot.App.Presentation;

internal enum InputNodeKind
{
    Keyboard,
    Gamepad,
    Special,
    Remote,
    Mouse
}

internal sealed class InputNodeState
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required InputNodeKind Kind { get; init; }

    public string FriendlyName { get; set; } = string.Empty;

    public string RawCode { get; set; } = string.Empty;

    /// <summary>Full, non-display identity used to match down/up packets without merging devices.</summary>
    public string? CaptureIdentity { get; set; }

    public string? DevicePath { get; set; }

    public InputSource? CapturedSource { get; set; }

    public string Location { get; set; } = "Standard";

    public MappingDraft? Mapping { get; set; }

    public string StableId => $"{Kind.ToString().ToLowerInvariant()}:{Id}";
}

internal sealed class MappingDraft
{
    /// <summary>The durable identity is retained when an existing mapping is edited.</summary>
    public Guid? PersistedId { get; set; }

    /// <summary>
    /// Keeps Core-only details (for example macro steps or launch arguments) lossless while the
    /// six-action drawer is only a projection of the active profile.
    /// </summary>
    public InputMapping? OriginalMapping { get; set; }

    public bool IsReadOnlyPassthrough { get; set; }

    public string SourceStableId { get; set; } = string.Empty;

    public string? SourceCaptureIdentity { get; set; }

    public string? SourceDevicePath { get; set; }

    public InputSource? Source { get; set; }

    public string ActionType { get; set; } = "快捷键";

    public string ActionValue { get; set; } = string.Empty;

    public string? TargetStableId { get; set; }

    public string? TargetCaptureIdentity { get; set; }

    public InputSource? TargetSource { get; set; }

    public MappingAction? Action { get; set; }

    public string Trigger { get; set; } = "单击";

    public MappingCondition Condition { get; set; } = new();

    public bool BlockOriginal { get; set; } = true;
}
