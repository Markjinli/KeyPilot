namespace KeyPilot.Core.Configuration;

/// <summary>Associates a Windows process executable name with a mapping profile.</summary>
public sealed record ApplicationProfileBinding
{
    /// <summary>
    /// Executable process name. Matching is case-insensitive and accepts either a bare name,
    /// a name ending in .exe, or a full executable path.
    /// </summary>
    public string ProcessName { get; init; } = string.Empty;

    public Guid ProfileId { get; init; }
}
