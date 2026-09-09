namespace KeyPilot.Platform.Windows.Input;

internal readonly record struct RawInputUsageRegistration(ushort UsagePage, ushort Usage);

internal readonly record struct RawHidBatchLayout(
    int DataOffset,
    int ReportLength,
    int ReportCount,
    int ReportBytes);

/// <summary>Pure selection and bounds checks kept separate from the Win32 callback.</summary>
internal static class RawInputHidGuards
{
    private const uint RidevRemove = 0x00000001;
    private const uint RidevPageOnly = 0x00000020;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const int ErrorInvalidParameter = 87;
    internal const uint MaximumRawInputPacketBytes = 1024 * 1024;
    // HID report lengths are represented by the existing opaque-report model as 1..UInt16.MaxValue.
    internal const uint MaximumHidReportBytes = ushort.MaxValue;
    internal const uint MaximumHidReportCount = 4096;

    internal static RawInputUsageRegistration[] SelectCaptureRegistrations(
        IEnumerable<RawInputUsageRegistration> discoveredHidCollections)
    {
        ArgumentNullException.ThrowIfNull(discoveredHidCollections);

        var registrations = new List<RawInputUsageRegistration>
        {
            new(0x01, 0x06), // Generic Desktop / Keyboard
            new(0x01, 0x05), // Generic Desktop / Game Pad (DualShock / DualSense)
            new(0x0C, 0x01)  // Consumer / Consumer Control
        };

        registrations.AddRange(
            discoveredHidCollections
                // Exact-TLC registration requires both fields to be non-zero. Usage zero is
                // legal only with RIDEV_PAGEONLY, which is intentionally too broad for capture.
                .Where(collection =>
                    collection.UsagePage >= 0xFF00 && collection.Usage != 0)
                .Distinct()
                .OrderBy(collection => collection.UsagePage)
                .ThenBy(collection => collection.Usage));

        return registrations.Distinct().ToArray();
    }

    internal static bool IsOptionalVendorRegistration(RawInputUsageRegistration registration) =>
        registration.UsagePage >= 0xFF00 && registration.Usage != 0;

    internal static bool CanSkipRejectedOptionalRegistration(
        RawInputUsageRegistration registration,
        int nativeErrorCode) =>
        nativeErrorCode == ErrorInvalidParameter && IsOptionalVendorRegistration(registration);

    internal static void ValidateRegistrationRequest(
        IReadOnlyList<RawInputUsageRegistration> registrations,
        uint flags,
        nint target)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if (registrations.Count == 0)
        {
            return;
        }

        var removes = (flags & RidevRemove) != 0;
        var pageOnly = (flags & 0x00000030) == RidevPageOnly;
        if (removes && (flags != RidevRemove || target != 0))
        {
            throw new ArgumentException("RIDEV_REMOVE requires no other flags and a null target.");
        }

        if (!removes && (flags & (RidevInputSink | RidevDevNotify)) != 0 && target == 0)
        {
            throw new ArgumentException("RIDEV_INPUTSINK/RIDEV_DEVNOTIFY requires a target window.");
        }

        foreach (var registration in registrations)
        {
            if (registration.UsagePage == 0 ||
                (pageOnly ? registration.Usage != 0 : registration.Usage == 0))
            {
                throw new ArgumentException(
                    $"Invalid Raw Input usage {registration.UsagePage:X4}/{registration.Usage:X4} for flags 0x{flags:X8}.");
            }
        }
    }

    internal static RawHidBatchLayout ValidateHidBatchLayout(
        uint copiedSize,
        uint declaredSize,
        uint headerSize,
        uint reportLength,
        uint reportCount)
    {
        if (copiedSize > MaximumRawInputPacketBytes || declaredSize > MaximumRawInputPacketBytes)
        {
            throw new InvalidDataException(
                $"Raw Input HID packet exceeds the {MaximumRawInputPacketBytes}-byte limit.");
        }

        if (reportLength is 0 or > MaximumHidReportBytes)
        {
            throw new InvalidDataException(
                $"RAWHID report length {reportLength} is outside the supported 1..{MaximumHidReportBytes} range.");
        }

        if (reportCount is 0 or > MaximumHidReportCount)
        {
            throw new InvalidDataException(
                $"RAWHID report count {reportCount} is outside the supported 1..{MaximumHidReportCount} range.");
        }

        ulong reportBytes;
        ulong dataOffset;
        ulong requiredSize;
        try
        {
            reportBytes = checked((ulong)reportLength * reportCount);
            dataOffset = checked((ulong)headerSize + (sizeof(uint) * 2UL));
            requiredSize = checked(dataOffset + reportBytes);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("RAWHID length arithmetic overflowed.", exception);
        }

        if (requiredSize > declaredSize || requiredSize > copiedSize)
        {
            throw new InvalidDataException(
                $"RAWHID needs {requiredSize} bytes, but declared {declaredSize} and copied {copiedSize}.");
        }

        if (reportBytes > int.MaxValue || dataOffset > int.MaxValue)
        {
            throw new InvalidDataException("RAWHID data cannot be addressed safely in a managed buffer.");
        }

        return new RawHidBatchLayout(
            checked((int)dataOffset),
            checked((int)reportLength),
            checked((int)reportCount),
            checked((int)reportBytes));
    }
}
