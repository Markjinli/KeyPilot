namespace KeyPilot.Core.Configuration;

public enum MappingTriggerKind
{
    SinglePress,
    DoublePress,
    LongPress,
    KeyDown,
    KeyUp
}

/// <summary>Defines when an input mapping fires without mixing timing policy into its action.</summary>
public sealed record MappingTrigger
{
    public MappingTriggerKind Kind { get; init; } = MappingTriggerKind.SinglePress;

    public int LongPressMilliseconds { get; init; } = 600;

    public int DoublePressWindowMilliseconds { get; init; } = 350;
}
