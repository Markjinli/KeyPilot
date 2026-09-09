namespace KeyPilot.Core.Actions;

public enum MediaControlCommand
{
    PlayPause,
    Stop,
    PreviousTrack,
    NextTrack
}

public sealed record MediaControlAction : MappingAction
{
    public MediaControlCommand Command { get; init; }
}

public enum VolumeControlCommand
{
    Increase,
    Decrease,
    ToggleMute,
    SetLevelPercent
}

public sealed record VolumeControlAction : MappingAction
{
    public VolumeControlCommand Command { get; init; }

    /// <summary>Required only for <see cref="VolumeControlCommand.SetLevelPercent"/>.</summary>
    public int? LevelPercent { get; init; }
}

/// <summary>Emits a codec-validated logical copy of a captured input, never its device identity.</summary>
public sealed record CopyInputToOutputAction : MappingAction
{
    public string EncodedTarget { get; init; } = string.Empty;

    public int HoldMilliseconds { get; init; } = 30;
}
