namespace KeyPilot.Core.Configuration;

/// <summary>The analog stick used for relative mouse movement.</summary>
public enum StickMouseSource
{
    Left,
    Right
}

/// <summary>Per-profile settings for converting an analog stick into relative mouse movement.</summary>
public sealed record StickMouseSettings
{
    public const int MinimumSpeedPixelsPerSecond = 100;
    public const int MaximumSpeedPixelsPerSecond = 2_400;
    public const int DefaultSpeedPixelsPerSecond = 900;

    public const double MinimumDeadzone = 0.05;
    public const double MaximumDeadzone = 0.45;
    public const double DefaultDeadzone = 0.24;

    /// <summary>
    /// Safe opt-in switch. Older profiles receive this default and never begin moving the mouse
    /// merely because KeyPilot was updated.
    /// </summary>
    public bool IsEnabled { get; init; }

    public StickMouseSource Source { get; init; } = StickMouseSource.Left;

    public int SpeedPixelsPerSecond { get; init; } = DefaultSpeedPixelsPerSecond;

    /// <summary>Normalized radial deadzone, from zero to one.</summary>
    public double Deadzone { get; init; } = DefaultDeadzone;
}
