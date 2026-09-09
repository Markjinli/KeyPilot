namespace KeyPilot.Platform.Windows.Input;

internal static class RawInputKeyboardGuards
{
    internal const string SyntheticKeyboardDevicePath =
        "RAWINPUT:SYNTHETIC-KEYBOARD:NO-PHYSICAL-DEVICE";

    private const ushort KeyboardOverrunMakeCode = 0x00FF;
    private const ushort FirstInvalidVirtualKey = 0x00FF;

    internal static bool ShouldPublishKeyboardPacket(ushort makeCode, ushort virtualKey) =>
        makeCode != 0 && makeCode != KeyboardOverrunMakeCode && virtualKey < FirstInvalidVirtualKey;

    internal static void ValidateRequestedSize(uint requestedSize, uint headerSize)
    {
        if (requestedSize < headerSize)
        {
            throw new InvalidDataException(
                $"GetRawInputData reported {requestedSize} bytes; at least {headerSize} bytes are required for RAWINPUTHEADER.");
        }

        if (requestedSize > RawInputHidGuards.MaximumRawInputPacketBytes)
        {
            throw new InvalidDataException(
                $"GetRawInputData reported {requestedSize} bytes; the safety limit is " +
                $"{RawInputHidGuards.MaximumRawInputPacketBytes} bytes.");
        }
    }

    internal static void ValidateCopiedPacket(
        uint allocatedSize,
        uint reportedSize,
        uint copiedSize,
        uint headerSize)
    {
        if (copiedSize < headerSize || copiedSize > allocatedSize)
        {
            throw new InvalidDataException(
                $"GetRawInputData copied {copiedSize} bytes into a {allocatedSize}-byte buffer.");
        }

        if (reportedSize != copiedSize || allocatedSize != copiedSize)
        {
            throw new InvalidDataException(
                $"Raw Input packet size changed while it was being read " +
                $"(allocated {allocatedSize}, reported {reportedSize}, copied {copiedSize}).");
        }
    }

    internal static void ValidatePacketLayout(
        uint copiedSize,
        uint declaredSize,
        uint headerSize,
        uint payloadSize)
    {
        var minimumSize = (ulong)headerSize + payloadSize;
        if (declaredSize != copiedSize || declaredSize < minimumSize)
        {
            throw new InvalidDataException(
                $"RAWINPUT declared {declaredSize} bytes, copied {copiedSize} bytes, " +
                $"and requires at least {minimumSize} bytes for this packet type.");
        }
    }

    internal static void RequirePhysicalDeviceHandle(nint device)
    {
        if (device == 0)
        {
            throw new InvalidDataException("The Raw Input packet does not contain a physical device handle.");
        }
    }

    /// <summary>
    /// Creates a keyboard event while preserving the documented hDevice == NULL identity used by
    /// synthetic keyboard input. HID packets never call this method and still require a physical
    /// device handle before descriptor or report parsing.
    /// </summary>
    internal static RawKeyboardEvent CreateKeyboardEvent(
        nint device,
        ushort makeCode,
        ushort virtualKey,
        ushort flags,
        uint message,
        uint extraInformation,
        DateTimeOffset timestamp,
        Func<nint, string> physicalDevicePathResolver)
    {
        ArgumentNullException.ThrowIfNull(physicalDevicePathResolver);
        var devicePath = device == 0
            ? SyntheticKeyboardDevicePath
            : RequireStableDevicePath(device, physicalDevicePathResolver(device));
        return new RawKeyboardEvent(
            device,
            devicePath,
            makeCode,
            virtualKey,
            flags,
            message,
            extraInformation,
            timestamp);
    }

    internal static string RequireStableDevicePath(nint device, string? devicePath)
    {
        RequirePhysicalDeviceHandle(device);

        if (string.IsNullOrWhiteSpace(devicePath))
        {
            throw new InvalidDataException(
                $"Raw Input device 0x{unchecked((nuint)device):X} did not provide a stable device interface path.");
        }

        return devicePath;
    }

    internal static void EnsureOwnerThread(uint ownerThreadId, uint currentThreadId)
    {
        if (ownerThreadId != currentThreadId)
        {
            throw new InvalidOperationException(
                "RawInputKeyboardSource must be disposed on the thread that owns its HWND.");
        }
    }

    internal static void InvokeCaptureFaultedSafely(
        object sender,
        EventHandler<Exception>? handlers,
        Exception exception)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<Exception>)subscriber)(sender, exception);
            }
            catch
            {
                // A diagnostic subscriber must never unwind through a native window callback.
            }
        }
    }
}
