namespace KeyPilot.Core.Actions;

public sealed record MediaControlOperation(MediaControlCommand Command) : ActionPlanOperation;

public sealed record VolumeControlOperation(
    VolumeControlCommand Command,
    int? LevelPercent) : ActionPlanOperation;
