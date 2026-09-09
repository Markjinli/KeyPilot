namespace KeyPilot.Platform.Windows.Input;

/// <summary>A press or release from one of the four XInput user slots.</summary>
public sealed class XInputButtonChangedEventArgs(
    int userIndex,
    XInputButton button,
    bool isPressed,
    uint packetNumber,
    DateTimeOffset timestampUtc) : EventArgs
{
    public int UserIndex { get; } = userIndex;

    public XInputButton Button { get; } = button;

    public bool IsPressed { get; } = isPressed;

    public uint PacketNumber { get; } = packetNumber;

    public DateTimeOffset TimestampUtc { get; } = timestampUtc;
}

/// <summary>A press or release from a threshold-derived trigger or thumb-stick direction.</summary>
public sealed class XInputVirtualControlChangedEventArgs(
    int userIndex,
    XInputVirtualControl control,
    bool isPressed,
    uint packetNumber,
    DateTimeOffset timestampUtc) : EventArgs
{
    public int UserIndex { get; } = userIndex;

    public XInputVirtualControl Control { get; } = control;

    public bool IsPressed { get; } = isPressed;

    public uint PacketNumber { get; } = packetNumber;

    public DateTimeOffset TimestampUtc { get; } = timestampUtc;
}

/// <summary>
/// One deduplicated raw XInput state sample. Unlike mapped button events, <see cref="Buttons"/>
/// deliberately preserves unknown and reserved bits for explicit diagnostics.
/// </summary>
public sealed class XInputRawStateChangedEventArgs(
    int userIndex,
    bool isConnected,
    uint packetNumber,
    ushort buttons,
    byte leftTrigger,
    byte rightTrigger,
    short thumbLX,
    short thumbLY,
    short thumbRX,
    short thumbRY,
    DateTimeOffset timestampUtc) : EventArgs
{
    public int UserIndex { get; } = userIndex;

    public bool IsConnected { get; } = isConnected;

    public uint PacketNumber { get; } = packetNumber;

    public ushort Buttons { get; } = buttons;

    public byte LeftTrigger { get; } = leftTrigger;

    public byte RightTrigger { get; } = rightTrigger;

    public short ThumbLX { get; } = thumbLX;

    public short ThumbLY { get; } = thumbLY;

    public short ThumbRX { get; } = thumbRX;

    public short ThumbRY { get; } = thumbRY;

    public DateTimeOffset TimestampUtc { get; } = timestampUtc;
}

/// <summary>A connection state edge from one of the four XInput user slots.</summary>
public sealed class XInputConnectionChangedEventArgs(
    int userIndex,
    bool isConnected,
    DateTimeOffset timestampUtc) : EventArgs
{
    public int UserIndex { get; } = userIndex;

    public bool IsConnected { get; } = isConnected;

    public DateTimeOffset TimestampUtc { get; } = timestampUtc;
}
