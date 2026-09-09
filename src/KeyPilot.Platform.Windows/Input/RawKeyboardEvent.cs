namespace KeyPilot.Platform.Windows.Input;

/// <summary>A keyboard packet received from WM_INPUT without suppressing the original input.</summary>
public sealed record RawKeyboardEvent(
    nint DeviceHandle,
    string DevicePath,
    ushort MakeCode,
    ushort VirtualKey,
    ushort Flags,
    uint Message,
    uint ExtraInformation,
    DateTimeOffset Timestamp)
{
    private const ushort BreakFlag = 0x0001;
    private const ushort E0Flag = 0x0002;
    private const ushort E1Flag = 0x0004;

    public bool IsKeyDown => (Flags & BreakFlag) == 0;

    public bool IsExtendedE0 => (Flags & E0Flag) != 0;

    public bool IsExtendedE1 => (Flags & E1Flag) != 0;

    /// <summary>
    /// False for synthetic RAWKEYBOARD packets whose RAWINPUTHEADER contains hDevice == NULL.
    /// Their DevicePath is an explicit synthetic identity, never a fabricated interface path.
    /// </summary>
    public bool HasPhysicalDevice => DeviceHandle != 0;

    /// <summary>Checks whether this packet carries the marker shared with the injection backend.</summary>
    public bool IsInjectedBy(InputInjectionMarker marker) => marker.Matches(ExtraInformation);

    public string RawCode =>
        $"Scan 0x{MakeCode:X2} · VK 0x{VirtualKey:X2} · Flags 0x{Flags:X2}";
}
