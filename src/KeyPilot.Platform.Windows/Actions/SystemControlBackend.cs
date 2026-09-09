using KeyPilot.Core.Actions;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Actions;

/// <summary>Platform boundary for media transport and endpoint-volume operations.</summary>
public interface ISystemControlBackend
{
    void ValidateMediaControl(MediaControlOperation operation);

    void ValidateVolumeControl(VolumeControlOperation operation);

    ValueTask ExecuteMediaControlAsync(
        MediaControlOperation operation,
        InputInjectionMarker originMarker,
        CancellationToken cancellationToken);

    ValueTask ExecuteVolumeControlAsync(
        VolumeControlOperation operation,
        InputInjectionMarker originMarker,
        CancellationToken cancellationToken);
}

internal sealed class UnsupportedSystemControlBackend : ISystemControlBackend
{
    public static UnsupportedSystemControlBackend Instance { get; } = new();

    private UnsupportedSystemControlBackend()
    {
    }

    public void ValidateMediaControl(MediaControlOperation operation) =>
        throw new NotSupportedException("No system media-control backend was configured.");

    public void ValidateVolumeControl(VolumeControlOperation operation) =>
        throw new NotSupportedException("No system volume-control backend was configured.");

    public ValueTask ExecuteMediaControlAsync(
        MediaControlOperation operation,
        InputInjectionMarker originMarker,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("No system media-control backend was configured.");

    public ValueTask ExecuteVolumeControlAsync(
        VolumeControlOperation operation,
        InputInjectionMarker originMarker,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("No system volume-control backend was configured.");
}
