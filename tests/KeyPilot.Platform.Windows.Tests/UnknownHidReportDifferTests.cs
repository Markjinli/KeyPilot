using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class UnknownHidReportDifferTests
{
    private const string FirstDevicePath =
        @"\\?\HID#VID_1234&PID_5678&MI_01#7&01234567&0&0000#{COLLECTION}";

    private const string SecondDevicePath =
        @"\\?\HID#VID_ABCD&PID_EF01&MI_02#8&76543210&0&0000#{COLLECTION}";

    public static Task IdentityIncludesEveryReportFieldAsync()
    {
        var identity = Identity(FirstDevicePath, usagePage: 0xFF01, usage: 0x0042, reportId: 7, length: 4);
        var sameIdentity = Identity(FirstDevicePath.ToLowerInvariant(), 0xFF01, 0x0042, 7, 4);

        Assert(identity.DevicePath == FirstDevicePath, "The full device path must be retained verbatim.");
        Assert(identity.Equals(sameIdentity), "Windows device-path casing must not split one physical report.");
        Assert(!identity.Equals(Identity(FirstDevicePath, 0xFF02, 0x0042, 7, 4)), "Usage page is part of identity.");
        Assert(!identity.Equals(Identity(FirstDevicePath, 0xFF01, 0x0043, 7, 4)), "Usage is part of identity.");
        Assert(!identity.Equals(Identity(FirstDevicePath, 0xFF01, 0x0042, 8, 4)), "Report ID is part of identity.");
        Assert(!identity.Equals(Identity(FirstDevicePath, 0xFF01, 0x0042, 7, 5)), "Report length is part of identity.");
        AssertThrows<ArgumentException>(
            () => Identity("   ", 0xFF01, 0x0042, 7, 4),
            "A missing physical device path must be rejected.");
        return Task.CompletedTask;
    }

    public static Task BaselineAndDeltaAreDeterministicAsync()
    {
        var differ = new UnknownHidReportDiffer(maximumReportLength: 8, maximumTrackedReportCount: 4);
        var identity = Identity(FirstDevicePath, 0xFF01, 0x0042, reportId: 3, length: 4);
        var first = new byte[] { 3, 0x10, 0x20, 0x30 };
        var timestamp = new DateTimeOffset(2026, 8, 1, 8, 30, 0, TimeSpan.FromHours(8));

        Assert(differ.Observe(identity, first, timestamp) is null, "The first report must only establish a baseline.");
        first[1] = 0x99;
        Assert(
            differ.Observe(identity, new byte[] { 3, 0x10, 0x20, 0x30 }, timestamp) is null,
            "The tracker must own a copy of the caller's report buffer.");

        var change = differ.Observe(
            identity,
            new byte[] { 3, 0x11, 0x20, 0x31 },
            timestamp) ?? throw new InvalidOperationException("A changed report must produce an event.");

        Assert(change.SequenceNumber == 1, "The first emitted change must have sequence one.");
        Assert(change.TimestampUtc.Offset == TimeSpan.Zero, "Change timestamps must be normalized to UTC.");
        Assert(change.ByteChanges.Count == 2, "Exactly two changed bytes must be reported.");
        Assert(
            change.ByteChanges[0] == new UnknownHidByteChange(1, 0x10, 0x11),
            "The first byte delta is incorrect.");
        Assert(
            change.ByteChanges[1] == new UnknownHidByteChange(3, 0x30, 0x31),
            "The second byte delta is incorrect.");
        Assert(change.PreviousReport.SequenceEqual(new byte[] { 3, 0x10, 0x20, 0x30 }), "Previous snapshot is incorrect.");
        Assert(change.CurrentReport.SequenceEqual(new byte[] { 3, 0x11, 0x20, 0x31 }), "Current snapshot is incorrect.");
        return Task.CompletedTask;
    }

    public static Task ReportIdentitiesKeepIndependentBaselinesAsync()
    {
        var differ = new UnknownHidReportDiffer(maximumReportLength: 8, maximumTrackedReportCount: 4);
        var firstReport = Identity(FirstDevicePath, 0xFF01, 1, reportId: 1, length: 2);
        var secondReport = Identity(FirstDevicePath, 0xFF01, 1, reportId: 2, length: 2);

        Assert(differ.Observe(firstReport, new byte[] { 1, 0 }, DateTimeOffset.UnixEpoch) is null, "First baseline failed.");
        Assert(differ.Observe(secondReport, new byte[] { 2, 9 }, DateTimeOffset.UnixEpoch) is null, "Second baseline failed.");
        Assert(differ.TrackedReportCount == 2, "Distinct report IDs must have distinct retained reports.");

        var change = differ.Observe(
            secondReport,
            new byte[] { 2, 10 },
            DateTimeOffset.UnixEpoch) ?? throw new InvalidOperationException("Second report change was lost.");
        Assert(change.Identity.Equals(secondReport), "The change must carry the exact report identity.");
        Assert(change.ByteChanges.Single().Offset == 1, "The report delta offset is incorrect.");

        Assert(
            differ.Observe(firstReport, new byte[] { 1, 0 }, DateTimeOffset.UnixEpoch) is null,
            "Changing one report ID must not contaminate another baseline.");
        return Task.CompletedTask;
    }

    public static Task LengthAndCountLimitsAreStrictAsync()
    {
        var differ = new UnknownHidReportDiffer(maximumReportLength: 2, maximumTrackedReportCount: 1);
        var accepted = Identity(FirstDevicePath, 0xFF01, 1, reportId: 0, length: 2);

        AssertThrows<InvalidDataException>(
            () => differ.Observe(
                Identity(FirstDevicePath, 0xFF01, 1, reportId: 0, length: 3),
                new byte[3],
                DateTimeOffset.UnixEpoch),
            "A report above the configured length limit must be rejected.");
        AssertThrows<InvalidDataException>(
            () => differ.Observe(accepted, new byte[1], DateTimeOffset.UnixEpoch),
            "A truncated report must be rejected before state changes.");

        differ.Observe(accepted, new byte[2], DateTimeOffset.UnixEpoch);
        AssertThrows<InvalidOperationException>(
            () => differ.Observe(
                Identity(SecondDevicePath, 0xFF01, 1, reportId: 0, length: 2),
                new byte[2],
                DateTimeOffset.UnixEpoch),
            "Capacity overflow must fail instead of evicting an arbitrary device.");
        Assert(differ.TrackedReportCount == 1, "Rejected reports must not alter retained state.");
        return Task.CompletedTask;
    }

    public static Task DisconnectClearsEveryDeviceReportAsync()
    {
        var differ = new UnknownHidReportDiffer(maximumReportLength: 8, maximumTrackedReportCount: 4);
        var first = Identity(FirstDevicePath, 0xFF01, 1, reportId: 1, length: 2);
        var second = Identity(FirstDevicePath, 0xFF01, 1, reportId: 2, length: 2);
        var unrelated = Identity(SecondDevicePath, 0xFF01, 1, reportId: 1, length: 2);
        differ.Observe(first, new byte[] { 1, 1 }, DateTimeOffset.UnixEpoch);
        differ.Observe(second, new byte[] { 2, 2 }, DateTimeOffset.UnixEpoch);
        differ.Observe(unrelated, new byte[] { 1, 3 }, DateTimeOffset.UnixEpoch);

        var removed = differ.RemoveDevice(FirstDevicePath.ToLowerInvariant());
        Assert(removed == 2, "Disconnect must remove every collection/report ID for that device.");
        Assert(differ.TrackedReportCount == 1, "Disconnect must preserve unrelated device state.");
        Assert(!differ.TryGetLastReport(first, out _), "Disconnected report state must not survive.");
        Assert(differ.TryGetLastReport(unrelated, out var report) && report.SequenceEqual(new byte[] { 1, 3 }), "Unrelated baseline was removed.");

        Assert(
            differ.Observe(first, new byte[] { 1, 9 }, DateTimeOffset.UnixEpoch) is null,
            "A reconnected device must establish a fresh baseline rather than emit a stale delta.");
        return Task.CompletedTask;
    }

    public static Task OpaqueBytesNeverAcquireButtonSemanticsAsync()
    {
        var differ = new UnknownHidReportDiffer(maximumReportLength: 8, maximumTrackedReportCount: 2);
        var identity = Identity(FirstDevicePath, 0x0001, 0x0005, reportId: 0, length: 3);
        differ.Observe(identity, new byte[] { 0x00, 0x01, 0x80 }, DateTimeOffset.UnixEpoch);

        var change = differ.Observe(
            identity,
            new byte[] { 0x01, 0x00, 0x7F },
            DateTimeOffset.UnixEpoch) ?? throw new InvalidOperationException("Opaque changes were not reported.");
        Assert(change.ByteChanges.Select(item => item.Offset).SequenceEqual(new[] { 0, 1, 2 }), "All bytes must remain opaque offsets.");
        Assert(identity.ReportId == 0, "The first payload byte must not be guessed to be a report ID.");
        return Task.CompletedTask;
    }

    private static UnknownHidReportIdentity Identity(
        string path,
        ushort usagePage,
        ushort usage,
        byte reportId,
        int length) =>
        new(path, usagePage, usage, reportId, length);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
