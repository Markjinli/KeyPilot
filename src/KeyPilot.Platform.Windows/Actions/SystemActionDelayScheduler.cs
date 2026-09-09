namespace KeyPilot.Platform.Windows.Actions;

/// <summary>Cancellation-aware wall-clock delay used between expanded macro operations.</summary>
public sealed class SystemActionDelayScheduler : IActionDelayScheduler
{
    public const int MaximumDelayMilliseconds = 600_000;

    public ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds is < 0 or > MaximumDelayMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(milliseconds),
                $"Delay must be between 0 and {MaximumDelayMilliseconds} milliseconds.");
        }

        return new ValueTask(Task.Delay(milliseconds, cancellationToken));
    }
}
