using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class RawInputHidGuardsTests
{
    internal static Task CaptureRegistrationSelectionIsNarrowAsync()
    {
        var selected = RawInputHidGuards.SelectCaptureRegistrations(
        [
            new RawInputUsageRegistration(0x01, 0x05),
            new RawInputUsageRegistration(0x0C, 0x02),
            new RawInputUsageRegistration(0xFF10, 0x02),
            new RawInputUsageRegistration(0xFF00, 0x03),
            new RawInputUsageRegistration(0xFF20, 0x00),
            new RawInputUsageRegistration(0x0000, 0x01),
            new RawInputUsageRegistration(0xFF10, 0x02)
        ]);

        AssertSequence(
        [
            new RawInputUsageRegistration(0x01, 0x06),
            new RawInputUsageRegistration(0x01, 0x05),
            new RawInputUsageRegistration(0x0C, 0x01),
            new RawInputUsageRegistration(0xFF00, 0x03),
            new RawInputUsageRegistration(0xFF10, 0x02)
        ], selected, "Only keyboard, Game Pad, Consumer Control, and exact discovered vendor TLCs may be registered.");
        return Task.CompletedTask;
    }

    internal static Task RegistrationRequestsEnforceWin32ContractAsync()
    {
        var keyboard = new RawInputUsageRegistration(0x01, 0x06);
        var vendor = new RawInputUsageRegistration(0xFF10, 0x02);
        RawInputHidGuards.ValidateRegistrationRequest([keyboard, vendor], 0x00002100, (nint)42);
        RawInputHidGuards.ValidateRegistrationRequest([keyboard], 0x00000001, 0);

        AssertArgumentThrows(() => RawInputHidGuards.ValidateRegistrationRequest(
            [new RawInputUsageRegistration(0xFF10, 0)],
            0x00002100,
            (nint)42));
        AssertArgumentThrows(() => RawInputHidGuards.ValidateRegistrationRequest(
            [keyboard],
            0x00002100,
            0));
        AssertArgumentThrows(() => RawInputHidGuards.ValidateRegistrationRequest(
            [keyboard],
            0x00000001,
            (nint)42));
        AssertArgumentThrows(() => RawInputHidGuards.ValidateRegistrationRequest(
            [vendor],
            0x00000020,
            (nint)42));

        Assert(RawInputHidGuards.CanSkipRejectedOptionalRegistration(vendor, 87),
            "ERROR_INVALID_PARAMETER from one optional vendor TLC must be isolated.");
        Assert(!RawInputHidGuards.CanSkipRejectedOptionalRegistration(keyboard, 87),
            "A mandatory keyboard registration failure must remain visible.");
        Assert(!RawInputHidGuards.CanSkipRejectedOptionalRegistration(vendor, 5),
            "Unexpected native failures must not be hidden.");
        return Task.CompletedTask;
    }

    internal static Task HidBatchLayoutSupportsMultipleReportsAsync()
    {
        var layout = RawInputHidGuards.ValidateHidBatchLayout(
            copiedSize: 47,
            declaredSize: 47,
            headerSize: 24,
            reportLength: 5,
            reportCount: 3);

        Assert(layout.DataOffset == 32, "RAWHID data must begin after its two DWORD fields.");
        Assert(layout.ReportLength == 5, "The report length must be retained.");
        Assert(layout.ReportCount == 3, "Every report in the batch must be retained.");
        Assert(layout.ReportBytes == 15, "The complete report byte count must be retained.");
        return Task.CompletedTask;
    }

    internal static Task HidBatchLayoutRejectsUnsafeLengthsAsync()
    {
        AssertThrows(() => RawInputHidGuards.ValidateHidBatchLayout(32, 32, 24, 0, 1));
        AssertThrows(() => RawInputHidGuards.ValidateHidBatchLayout(32, 32, 24, 1, 0));
        AssertThrows(() => RawInputHidGuards.ValidateHidBatchLayout(32, 32, 24, 65_536, 1));
        AssertThrows(() => RawInputHidGuards.ValidateHidBatchLayout(32, 32, 24, 1, 4_097));
        AssertThrows(() => RawInputHidGuards.ValidateHidBatchLayout(36, 36, 24, 5, 1));
        AssertThrows(() => RawInputHidGuards.ValidateHidBatchLayout(1_048_577, 1_048_577, 24, 1, 1));
        return Task.CompletedTask;
    }

    internal static Task RawInputPacketCapIsSharedAsync()
    {
        RawInputKeyboardGuards.ValidateRequestedSize(
            RawInputHidGuards.MaximumRawInputPacketBytes,
            headerSize: 24);
        AssertThrows(
            () => RawInputKeyboardGuards.ValidateRequestedSize(
                RawInputHidGuards.MaximumRawInputPacketBytes + 1,
                headerSize: 24));
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException("An unsafe RAWHID length must be rejected.");
    }

    private static void AssertArgumentThrows(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("An invalid Raw Input registration must be rejected before Win32.");
    }

    private static void AssertSequence<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }
}
