namespace KeyPilot.Core.Input;

/// <summary>A user-named slot that binds a captured special control for reuse as an output.</summary>
public sealed record SpecialKeySlot
{
    public int Number { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public InputSource? Source { get; init; }
}

public static class SpecialKeySlots
{
    public const int Minimum = 1;
    public const int Maximum = 10;
    public const int Count = Maximum - Minimum + 1;

    public static List<SpecialKeySlot> CreateDefaults() =>
        Enumerable.Range(Minimum, Count)
            .Select(number => new SpecialKeySlot
            {
                Number = number,
                DisplayName = $"特殊键 {number}"
            })
            .ToList();
}
