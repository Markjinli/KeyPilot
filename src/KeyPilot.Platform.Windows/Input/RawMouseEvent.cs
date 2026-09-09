namespace KeyPilot.Platform.Windows.Input;

/// <summary>A mouse-button packet received from WM_INPUT without suppressing the original input.</summary>
public sealed record RawMouseEvent(
    nint DeviceHandle,
    string DevicePath,
    ushort ButtonFlags,
    ushort VirtualKey,
    bool IsPressed,
    uint ExtraInformation,
    DateTimeOffset Timestamp)
{
    public bool HasPhysicalDevice => DeviceHandle != 0;

    public bool IsInjectedBy(InputInjectionMarker marker) => marker.Matches(ExtraInformation);
}
