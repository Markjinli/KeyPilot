using KeyPilot.App.Presentation;
using KeyPilot.Core.Input;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class ShortcutCaptureSessionTests
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);

    public static Task NormalizesModifiersAndCompletesAtTwoSecondsAsync()
    {
        var session = new ShortcutCaptureSession();
        session.Arm(Epoch);

        session.Observe(RawKey(0x2A, isDown: true, 10)); // Shift
        session.Observe(RawKey(0x38, isDown: true, 20)); // Alt
        session.Observe(RawKey(0x1D, isDown: true, 30)); // Ctrl
        session.Observe(RawKey(0x25, isDown: true, 40)); // K
        session.Observe(RawKey(0x25, isDown: true, 41)); // repeat down
        session.Observe(RawKey(0x25, isDown: false, 50));

        Require(session.DeadlineUtc == Epoch + TimeSpan.FromSeconds(2), "capture deadline is not fixed at two seconds");
        Require(!session.TryComplete(Epoch.AddMilliseconds(1_999)), "capture completed before its deadline");
        Require(session.TryComplete(Epoch.AddSeconds(2)), "capture did not complete at its deadline");
        Require(session.State == ShortcutCaptureState.Completed, "valid shortcut was not completed");
        Require(session.CompletedText == "Ctrl + Alt + Shift + K", "modifier normalization/order is unstable");
        Require(session.AcceptedEdgeCount == 5, "duplicate down edge was not ignored");
        Require(session.Error is null, "successful capture retained an error");
        return Task.CompletedTask;
    }

    public static Task RejectsInjectedPureModifierAndUnboundedInputAsync()
    {
        var pureModifier = new ShortcutCaptureSession();
        pureModifier.Arm(Epoch);
        pureModifier.Observe(RawKey(0x1D, isDown: true, 10));
        pureModifier.Observe(RawKey(0x1D, isDown: false, 20));
        pureModifier.TryComplete(Epoch.AddSeconds(2));
        Require(pureModifier.State == ShortcutCaptureState.Rejected, "pure modifier capture was accepted");
        Require(pureModifier.Failure == ShortcutCaptureFailure.PureModifier, "pure modifier rejection reason was lost");

        var injected = new ShortcutCaptureSession();
        injected.Arm(Epoch);
        injected.Observe(InputKey(0x1E, InputEventPhase.Pressed, 10, isInjected: true));
        injected.TryComplete(Epoch.AddSeconds(2));
        Require(injected.Failure == ShortcutCaptureFailure.NoKeys, "injected InputEvent entered the shortcut");

        var edgeFlood = new ShortcutCaptureSession();
        edgeFlood.Arm(Epoch);
        for (var index = 0; index <= ShortcutCaptureSession.MaximumAcceptedEdges; index++)
        {
            edgeFlood.Observe(RawKey(0x1E, isDown: index % 2 == 0, index + 1));
        }
        Require(edgeFlood.Failure == ShortcutCaptureFailure.TooManyEdges, "edge cap did not reject an unbounded recording");

        var distinctFlood = new ShortcutCaptureSession();
        distinctFlood.Arm(Epoch);
        foreach (var makeCode in new ushort[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18 })
        {
            distinctFlood.Observe(RawKey(makeCode, isDown: true, makeCode));
        }
        Require(
            distinctFlood.Failure == ShortcutCaptureFailure.TooManyDistinctKeys,
            "distinct-key cap did not reject the ninth key");
        return Task.CompletedTask;
    }

    private static RawKeyboardEvent RawKey(ushort makeCode, bool isDown, int milliseconds) => new(
        DeviceHandle: 1,
        DevicePath: @"\\?\HID#TEST",
        MakeCode: makeCode,
        VirtualKey: 0,
        Flags: isDown ? (ushort)0 : (ushort)1,
        Message: isDown ? 0x0100u : 0x0101u,
        ExtraInformation: 0,
        Timestamp: Epoch.AddMilliseconds(milliseconds));

    private static InputEvent InputKey(
        int scanCode,
        InputEventPhase phase,
        int milliseconds,
        bool isInjected) => new()
    {
        Source = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Keyboard,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.KeyboardScanCode,
                Code = scanCode
            }
        },
        Phase = phase,
        TimestampUtc = Epoch.AddMilliseconds(milliseconds),
        IsInjected = isInjected
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
