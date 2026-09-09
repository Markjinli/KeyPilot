using KeyPilot.App.Presentation;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class InputDiagnosticSessionTests
{
    public static Task TraceIsExplicitBoundedAndRedactedAsync(string testRoot)
    {
        var path = Path.Combine(testRoot, "input-diagnostic.jsonl");
        var session = new InputDiagnosticSession(path);
        var started = DateTimeOffset.UtcNow;
        session.Start(started, "test-build");

        const string privatePath = @"\\?\HID#VID_0C45&PID_FEFE#PRIVATE-SERIAL";
        session.ObserveKeyboard(new RawKeyboardEvent(
            DeviceHandle: 1,
            DevicePath: privatePath,
            MakeCode: 0x1D,
            VirtualKey: 0xA2,
            Flags: 0,
            Message: 0x0100,
            ExtraInformation: 0,
            Timestamp: started.AddMilliseconds(10)));
        // Auto-repeat is folded until the matching up edge.
        session.ObserveKeyboard(new RawKeyboardEvent(
            DeviceHandle: 1,
            DevicePath: privatePath,
            MakeCode: 0x1D,
            VirtualKey: 0xA2,
            Flags: 0,
            Message: 0x0100,
            ExtraInformation: 0,
            Timestamp: started.AddMilliseconds(11)));
        session.ObserveKeyboard(new RawKeyboardEvent(
            DeviceHandle: 1,
            DevicePath: privatePath,
            MakeCode: 0x1D,
            VirtualKey: 0xA2,
            Flags: 1,
            Message: 0x0101,
            ExtraInformation: 0,
            Timestamp: started.AddMilliseconds(20)));

        session.ObserveXInputState(
            0,
            true,
            7,
            buttons: 0x0400,
            leftTrigger: XInputVirtualControlThresholds.TriggerPress,
            rightTrigger: 0,
            thumbLX: XInputVirtualControlThresholds.LeftStickPress,
            thumbLY: 0,
            thumbRX: 0,
            thumbRY: 0,
            started.AddMilliseconds(30));

        var result = session.StopAndSave("test-complete", started.AddSeconds(1));
        Assert(result.Saved, result.Error ?? "Diagnostic trace was not saved.");
        Assert(new FileInfo(path).Length <= InputDiagnosticSession.MaximumUtf8Bytes, "Trace exceeded its byte cap.");

        var text = File.ReadAllText(path);
        Assert(!text.Contains("PRIVATE-SERIAL", StringComparison.Ordinal), "Full private device paths must be redacted.");
        Assert(text.Contains("\"pathHash\"", StringComparison.Ordinal), "A stable redacted path hash is required.");
        Assert(text.Contains("\"control\":\"BIT_0400\"", StringComparison.Ordinal), "Reserved XInput bits must remain diagnosable.");
        Assert(text.Contains("\"control\":\"LT\"", StringComparison.Ordinal), "Trigger threshold edges must be diagnosable.");
        Assert(text.Contains("\"control\":\"L_RIGHT\"", StringComparison.Ordinal), "Stick direction edges must be diagnosable.");
        Assert(Count(text, "\"src\":\"rawkbd\"") == 2, "Keyboard auto-repeat must be folded to down/up edges.");
        return Task.CompletedTask;
    }

    public static Task TraceStopsAtHardLimitsAsync(string testRoot)
    {
        var path = Path.Combine(testRoot, "input-diagnostic-limit.jsonl");
        var session = new InputDiagnosticSession(path);
        var started = DateTimeOffset.UtcNow;
        session.Start(started, "limit-test");
        for (var index = 1; index <= 1_000; index++)
        {
            session.ObserveKeyboard(new RawKeyboardEvent(
                DeviceHandle: 1,
                DevicePath: @"\\?\HID#VID_1234&PID_5678#LIMIT",
                MakeCode: (ushort)index,
                VirtualKey: (ushort)(index % 255),
                Flags: 0,
                Message: 0x0100,
                ExtraInformation: 0,
                Timestamp: started.AddMilliseconds(index)));
        }

        var result = session.StopAndSave("limit-complete", started.AddSeconds(2));
        Assert(result.Saved, result.Error ?? "Bounded diagnostic trace was not saved.");
        Assert(result.Truncated, "An oversized diagnostic trace must report truncation.");
        Assert(result.EventCount < 1_000, "An oversized trace must stop accepting events.");
        Assert(new FileInfo(path).Length <= InputDiagnosticSession.MaximumUtf8Bytes,
            "The final summary must remain inside the hard byte cap.");
        return Task.CompletedTask;
    }

    private static int Count(string value, string pattern)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(pattern, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += pattern.Length;
        }

        return count;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
