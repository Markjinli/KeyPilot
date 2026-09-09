using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class OpaqueHidButtonCaptureSessionTests
{
    private static readonly UnknownHidReportIdentity Identity = new(
        @"\\?\HID#VID_1234&PID_5678#CAPTURE",
        usagePage: 0xFF01,
        usage: 0x0042,
        reportId: 0,
        reportLength: 3);

    public static Task RepeatableTransitionIsConfirmedAsync()
    {
        var start = DateTimeOffset.UtcNow;
        var session = new OpaqueHidButtonCaptureSession(timeout: TimeSpan.FromSeconds(10));
        session.Arm(start);

        var candidate = Change(start.AddSeconds(1), new byte[] { 0, 0, 9 }, new byte[] { 0, 1, 9 });
        var restored = Change(start.AddSeconds(2), new byte[] { 0, 1, 8 }, new byte[] { 0, 0, 8 });
        var confirmed = Change(start.AddSeconds(3), new byte[] { 0, 0, 7 }, new byte[] { 0, 1, 7 });

        Assert(
            session.Observe(candidate).Progress == OpaqueHidCaptureProgress.CandidateDetected,
            "The first transition must become a candidate.");
        Assert(
            session.Observe(restored).Progress == OpaqueHidCaptureProgress.BaselineRestored,
            "Only candidate bytes must be required to return to baseline.");
        var result = session.Observe(confirmed);
        Assert(result.Progress == OpaqueHidCaptureProgress.Confirmed, "The repeated transition must confirm capture.");
        Assert(ReferenceEquals(result.ConfirmedChange, candidate), "The confirmed identity must use the original candidate.");
        Assert(!session.IsArmed, "A confirmed session must disarm itself.");
        return Task.CompletedTask;
    }

    public static Task OtherReportsCannotConfirmCandidateAsync()
    {
        var start = DateTimeOffset.UtcNow;
        var session = new OpaqueHidButtonCaptureSession();
        session.Arm(start);
        session.Observe(Change(start.AddSeconds(1), new byte[] { 0, 0, 0 }, new byte[] { 0, 1, 0 }));

        var otherIdentity = new UnknownHidReportIdentity(
            @"\\?\HID#VID_9999&PID_0001#OTHER",
            0xFF01,
            0x0042,
            0,
            3);
        var unrelated = Change(
            start.AddSeconds(2),
            new byte[] { 0, 1, 0 },
            new byte[] { 0, 0, 0 },
            otherIdentity);

        Assert(
            session.Observe(unrelated).Progress == OpaqueHidCaptureProgress.Ignored,
            "A different physical report must not advance the candidate.");
        Assert(
            session.Stage == OpaqueHidCaptureStage.WaitingForBaselineReturn,
            "An unrelated report must leave the candidate intact.");
        return Task.CompletedTask;
    }

    public static Task UnstableOrWideChangesAreRejectedAsync()
    {
        var start = DateTimeOffset.UtcNow;
        var session = new OpaqueHidButtonCaptureSession(maximumChangedByteCount: 1);
        session.Arm(start);

        var wide = Change(start.AddSeconds(1), new byte[] { 0, 0, 0 }, new byte[] { 0, 1, 1 });
        Assert(
            session.Observe(wide).Progress == OpaqueHidCaptureProgress.CandidateRejected,
            "A transition above the byte limit must be rejected.");

        session.Observe(Change(start.AddSeconds(2), new byte[] { 0, 0, 0 }, new byte[] { 0, 1, 0 }));
        var unstable = Change(start.AddSeconds(3), new byte[] { 0, 1, 0 }, new byte[] { 0, 2, 0 });
        Assert(
            session.Observe(unstable).Progress == OpaqueHidCaptureProgress.CandidateRejected,
            "A candidate byte that reaches a third value must be rejected.");
        Assert(
            session.Stage == OpaqueHidCaptureStage.WaitingForCandidate,
            "Rejection must safely restart candidate discovery.");
        return Task.CompletedTask;
    }

    public static Task StaleReportsAndTimeoutAreSafeAsync()
    {
        var start = DateTimeOffset.UtcNow;
        var session = new OpaqueHidButtonCaptureSession(timeout: TimeSpan.FromSeconds(2));
        session.Arm(start);

        var stale = Change(start.AddMilliseconds(-1), new byte[] { 0, 0, 0 }, new byte[] { 0, 1, 0 });
        Assert(
            session.Observe(stale).Progress == OpaqueHidCaptureProgress.Ignored,
            "Queued reports older than arming must be ignored.");
        Assert(session.IsArmed, "Ignoring a stale report must not cancel a valid capture.");
        Assert(session.TryExpire(start.AddSeconds(3)), "The session must expire after its deadline.");
        Assert(!session.IsArmed, "An expired session must be fully disarmed.");
        return Task.CompletedTask;
    }

    public static Task CancellationClearsCandidateAsync()
    {
        var start = DateTimeOffset.UtcNow;
        var session = new OpaqueHidButtonCaptureSession();
        session.Arm(start);
        session.Observe(Change(start.AddSeconds(1), new byte[] { 0, 0, 0 }, new byte[] { 0, 1, 0 }));
        session.Cancel();

        Assert(session.Stage == OpaqueHidCaptureStage.Idle, "Cancellation must return the session to idle.");
        Assert(
            session.Observe(Change(start.AddSeconds(2), new byte[] { 0, 1, 0 }, new byte[] { 0, 0, 0 })).Progress ==
                OpaqueHidCaptureProgress.Ignored,
            "Reports after cancellation must not reuse the old candidate.");
        return Task.CompletedTask;
    }

    public static Task FactoryCreatesExactOpaqueIdentityAsync()
    {
        var change = Change(
            DateTimeOffset.UtcNow,
            new byte[] { 7, 0, 0 },
            new byte[] { 7, 0x80, 0 });
        var device = new RawHidDeviceDescriptor(
            DeviceHandle: 42,
            Identity.DevicePath,
            VendorId: 0x1234,
            ProductId: 0x5678,
            VersionNumber: 1,
            Identity.UsagePage,
            Identity.Usage);

        var source = OpaqueHidInputSourceFactory.Create(device, change);
        Assert(source.Device.MatchMode == KeyPilot.Core.Input.DeviceMatchMode.ExactDevice, "Opaque HID must match the exact path.");
        Assert(source.Device.DeviceId == Identity.DevicePath, "The complete device path must be retained.");
        Assert(source.Device.VendorId == 0x1234 && source.Device.ProductId == 0x5678, "VID/PID were lost.");
        Assert(source.Control.Code == 1, "The minimum changed offset must be the raw code.");
        Assert(source.Control.ReportId is null, "An unparsed first byte must never become a report ID.");
        Assert(source.Control.RawQualifier?.Contains("0001:80:00~80", StringComparison.Ordinal) == true, "The qualifier must retain mask and value pair.");
        Assert(source.Control.ActivationQualifier?.Contains("BASE=0001:00;ACTIVE=0001:80", StringComparison.Ordinal) == true, "The captured press direction must be retained.");
        return Task.CompletedTask;
    }

    public static Task FactoryIdentityIsDirectionIndependentAsync()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var forward = Change(timestamp, new byte[] { 3, 0, 9 }, new byte[] { 3, 1, 9 });
        var reverse = Change(timestamp.AddMilliseconds(1), new byte[] { 3, 1, 9 }, new byte[] { 3, 0, 9 });
        var device = new RawHidDeviceDescriptor(42, Identity.DevicePath, 0x1234, 0x5678, 1, Identity.UsagePage, Identity.Usage);

        var forwardSource = OpaqueHidInputSourceFactory.Create(device, forward);
        var reverseSource = OpaqueHidInputSourceFactory.Create(device, reverse);
        Assert(
            forwardSource.CanonicalKey == reverseSource.CanonicalKey,
            "Press and release directions of one opaque transition must share an identity.");

        var mismatchedDevice = device with { Usage = 0x0043 };
        AssertThrows<ArgumentException>(
            () => OpaqueHidInputSourceFactory.Create(mismatchedDevice, forward),
            "A descriptor from another collection must be rejected.");
        return Task.CompletedTask;
    }

    public static Task RuntimeMatcherReportsOnlyCapturedEdgesAsync()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var pressed = Change(timestamp, new byte[] { 3, 0, 9 }, new byte[] { 3, 1, 9 });
        var released = Change(timestamp.AddMilliseconds(1), new byte[] { 3, 1, 9 }, new byte[] { 3, 0, 9 });
        var unrelated = Change(timestamp.AddMilliseconds(2), new byte[] { 3, 0, 9 }, new byte[] { 4, 0, 9 });
        var device = new RawHidDeviceDescriptor(42, Identity.DevicePath, 0x1234, 0x5678, 1, Identity.UsagePage, Identity.Usage);
        var source = OpaqueHidInputSourceFactory.Create(device, pressed);

        Assert(
            OpaqueHidInputMatcher.TryMatch(source, device, pressed, out var pressPhase) &&
                pressPhase == KeyPilot.Core.Input.InputEventPhase.Pressed,
            "The captured active transition must emit Pressed.");
        Assert(
            OpaqueHidInputMatcher.TryMatch(source, device, released, out var releasePhase) &&
                releasePhase == KeyPilot.Core.Input.InputEventPhase.Released,
            "The reverse transition must emit Released.");
        Assert(
            !OpaqueHidInputMatcher.TryMatch(source, device, unrelated, out _),
            "Changes outside the captured offsets must not emit an edge.");
        Assert(
            !OpaqueHidInputMatcher.TryMatch(source, device with { DevicePath = device.DevicePath + "#OTHER" }, pressed, out _),
            "Another physical device must not match the captured source.");
        return Task.CompletedTask;
    }

    private static UnknownHidReportChange Change(
        DateTimeOffset timestamp,
        byte[] previous,
        byte[] current,
        UnknownHidReportIdentity? identity = null)
    {
        var changes = previous
            .Zip(current, (before, after) => (before, after))
            .Select((pair, offset) => new { pair.before, pair.after, offset })
            .Where(item => item.before != item.after)
            .Select(item => new UnknownHidByteChange(item.offset, item.before, item.after))
            .ToArray();
        return new UnknownHidReportChange(
            identity ?? Identity,
            sequenceNumber: 1,
            timestamp,
            previous,
            current,
            changes);
    }

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
