using System.Globalization;
using System.Text.Json.Serialization;

namespace KeyPilot.Core.Input;

/// <summary>Broad physical device families understood by KeyPilot.</summary>
public enum InputDeviceKind
{
    Unknown,
    Keyboard,
    Gamepad,
    ConsumerControl,
    Hid,
    /// <summary>A physical mouse. Button identity uses VirtualKey (VK_MBUTTON / XBUTTON1 / XBUTTON2).</summary>
    Mouse,
    /// <summary>A logical source assembled from controls that may span device families.</summary>
    Composite
}

/// <summary>Controls how narrowly an input source matches attached devices.</summary>
public enum DeviceMatchMode
{
    AnyOfKind,
    ExactDevice
}

/// <summary>The namespace used to interpret a control code.</summary>
public enum InputControlKind
{
    KeyboardScanCode,
    VirtualKey,
    HidUsage,
    GamepadButton,
    GamepadAxisDirection,
    RawCode,
    /// <summary>A clockwise or counter-clockwise full rotation of an analog stick.</summary>
    GamepadRotation,
    /// <summary>An unordered set of two or more physical controls pressed together.</summary>
    InputChord,
    /// <summary>An ordered series of physical press/release edges captured over a short window.</summary>
    InputSequence
}

/// <summary>Stable codes for one completed 360-degree analog-stick rotation.</summary>
public enum GamepadRotationControl
{
    LeftClockwise = 1,
    LeftCounterClockwise = 2,
    RightClockwise = 3,
    RightCounterClockwise = 4
}

/// <summary>The edge represented by a normalized runtime input event.</summary>
public enum InputEventPhase
{
    Pressed,
    Released,
    Repeated
}

/// <summary>Selects either every device in a family or one captured physical device.</summary>
public sealed record InputDeviceSelector
{
    public InputDeviceKind Kind { get; init; } = InputDeviceKind.Unknown;

    public DeviceMatchMode MatchMode { get; init; } = DeviceMatchMode.AnyOfKind;

    /// <summary>A stable device path or application-assigned identifier.</summary>
    public string? DeviceId { get; init; }

    public ushort? VendorId { get; init; }

    public ushort? ProductId { get; init; }

    [JsonIgnore]
    public string CanonicalKey => MatchMode == DeviceMatchMode.AnyOfKind
        ? $"{Kind}:{MatchMode}:*:*:*"
        : string.Join(
            ":",
            Kind,
            MatchMode,
            Normalize(DeviceId),
            VendorId?.ToString("X4", CultureInfo.InvariantCulture) ?? "*",
            ProductId?.ToString("X4", CultureInfo.InvariantCulture) ?? "*");

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "*" : value.Trim().ToUpperInvariant();
}

/// <summary>A device-independent identity for a key, button, HID usage, or raw code.</summary>
public sealed record InputControlId
{
    public InputControlKind Kind { get; init; } = InputControlKind.RawCode;

    /// <summary>Scan code, virtual-key value, gamepad button/axis code, or vendor raw code.</summary>
    public int Code { get; init; }

    public ushort? UsagePage { get; init; }

    public ushort? Usage { get; init; }

    /// <summary>HID report identifier when the descriptor uses numbered reports.</summary>
    public byte? ReportId { get; init; }

    /// <summary>HID parser data index, unique within one top-level collection and report type.</summary>
    public ushort? DataIndex { get; init; }

    public ushort? LinkCollection { get; init; }

    /// <summary>Lossless vendor/raw qualifier such as byte range, mask and baseline pattern.</summary>
    public string? RawQualifier { get; init; }

    /// <summary>
    /// Optional direction-specific activation data used to distinguish press from release. It is
    /// deliberately excluded from <see cref="CanonicalKey"/> so both edges retain one identity.
    /// </summary>
    public string? ActivationQualifier { get; init; }

    public bool IsExtended { get; init; }

    [JsonIgnore]
    public string CanonicalKey => string.Join(
        ":",
        Kind,
        Code.ToString("X8", CultureInfo.InvariantCulture),
        UsagePage?.ToString("X4", CultureInfo.InvariantCulture) ?? "----",
        Usage?.ToString("X4", CultureInfo.InvariantCulture) ?? "----",
        ReportId?.ToString("X2", CultureInfo.InvariantCulture) ?? "--",
        DataIndex?.ToString("X4", CultureInfo.InvariantCulture) ?? "----",
        LinkCollection?.ToString("X4", CultureInfo.InvariantCulture) ?? "----",
        EncodeQualifier(RawQualifier),
        IsExtended ? "E" : "N");

    private static string EncodeQualifier(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "-"
            : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value.Trim()));
}

/// <summary>A complete mapping source: physical-device selector plus control identity.</summary>
public sealed record InputSource
{
    public InputDeviceSelector Device { get; init; } = new();

    public InputControlId Control { get; init; } = new();

    /// <summary>
    /// Physical members of an <see cref="InputControlKind.InputChord"/>. Member order is ignored;
    /// each member keeps its own exact/any-device selector so an OEM macro can remain isolated
    /// from the user's normal keyboard.
    /// </summary>
    public List<InputSource>? ChordMembers { get; init; }

    /// <summary>
    /// Ordered physical edges of an <see cref="InputControlKind.InputSequence"/>. Offsets are
    /// relative to the first edge, allowing runtime matching to tolerate human timing variation.
    /// </summary>
    public List<InputPatternStep>? PatternSteps { get; init; }

    [JsonIgnore]
    public string CanonicalKey => Control.Kind == InputControlKind.InputChord && ChordMembers is { Count: > 0 }
        ? "CHORD-V1/" + string.Join(
            "|",
            ChordMembers
                .Where(static member => member is not null)
                .Select(member => member.CanonicalKey)
                .OrderBy(key => key, StringComparer.Ordinal))
        : Control.Kind == InputControlKind.InputSequence && PatternSteps is { Count: > 0 }
            ? "SEQUENCE-V1/" + string.Join(
                "|",
                PatternSteps
                    .Select(step => step is null || step.Source is null
                        ? "<INVALID>"
                        : string.Join(
                            ":",
                            step.OffsetMilliseconds.ToString("D4", CultureInfo.InvariantCulture),
                            step.Phase,
                            step.Source.CanonicalKey)))
            : $"{Device.CanonicalKey}/{Control.CanonicalKey}";
}

/// <summary>One recorded physical edge in an ordered input pattern.</summary>
public sealed record InputPatternStep
{
    public InputSource Source { get; init; } = new();

    public InputEventPhase Phase { get; init; }

    /// <summary>Milliseconds since the pattern's first accepted edge.</summary>
    public int OffsetMilliseconds { get; init; }
}

/// <summary>A normalized event emitted by any keyboard, gamepad, consumer, or vendor HID source.</summary>
public sealed record InputEvent
{
    public InputSource Source { get; init; } = new();

    public InputEventPhase Phase { get; init; }

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Monotonic sequence assigned by the capture service.</summary>
    public long SequenceNumber { get; init; }

    /// <summary>True for KeyPilot-generated input, allowing recursion guards to ignore it.</summary>
    public bool IsInjected { get; init; }
}
