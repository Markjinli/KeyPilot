namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Immutable metadata for one Raw Input HID top-level collection. The complete device path is
/// retained because VID/PID alone cannot distinguish two otherwise identical physical devices.
/// </summary>
public sealed record RawHidDeviceDescriptor(
    nint DeviceHandle,
    string DevicePath,
    uint VendorId,
    uint ProductId,
    uint VersionNumber,
    ushort UsagePage,
    ushort Usage);
