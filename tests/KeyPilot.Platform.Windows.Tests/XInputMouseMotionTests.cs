using System.ComponentModel;
using KeyPilot.Core.Configuration;
using KeyPilot.Platform.Windows.Actions;
using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class XInputMouseMotionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

    public static Task RelativeSendInputIsMarkedAndExactAsync()
    {
        var native = new RecordingSendInputNative();
        var backend = new WindowsRelativeMouseBackend(
            native,
            new InputInjectionMarker(0x4B50544C));

        backend.MoveRelative(0, 0);
        Assert(native.Inputs.Count == 0, "A zero relative delta must not call SendInput.");

        backend.MoveRelative(17, -9);
        var input = native.Inputs.Single();
        Assert(input.Type == WindowsRelativeMouseBackend.InputMouse, "Relative movement must use INPUT_MOUSE.");
        Assert(input.Union.Mouse.X == 17 && input.Union.Mouse.Y == -9, "Relative deltas changed during encoding.");
        Assert(input.Union.Mouse.Flags == WindowsRelativeMouseBackend.MouseEventMove,
            "Relative movement must not accidentally select absolute, button, or wheel flags.");
        Assert(input.Union.Mouse.MouseData == 0 && input.Union.Mouse.Time == 0,
            "Unused mouse fields must remain zero.");
        Assert(input.Union.Mouse.ExtraInformation == (nuint)0x4B50544C,
            "Mouse input must retain KeyPilot's recursion marker.");

        native.Result = new SendInputNativeResult(0, 5);
        AssertThrows<Win32Exception>(
            () => backend.MoveRelative(1, 1),
            "A short native insertion must be reported.");
        return Task.CompletedTask;
    }

    public static Task MotionUsesRadialCurveFractionsAndScreenCoordinatesAsync()
    {
        var engine = new XInputMouseMotionEngine();
        var settings = Settings(speed: 1_000, deadzone: 0.20);
        engine.ApplySettings(settings, isMappingEnabled: true, runtimeRevision: 7, TimeSpan.Zero);
        engine.Observe(Frame(0, angle: null, radius: 0), 7, TimeSpan.Zero);

        engine.Observe(Frame(0, angle: 90, radius: 1), 7, TimeSpan.Zero);
        Assert(engine.Tick(TimeSpan.FromMilliseconds(10)) == new MouseRelativeDelta(0, -10),
            "Positive XInput Y must become upward screen movement at full configured speed.");

        engine.Observe(Frame(0, angle: 0, radius: 0.60), 7, TimeSpan.FromMilliseconds(10));
        Assert(engine.Tick(TimeSpan.FromMilliseconds(50)) == new MouseRelativeDelta(10, 0),
            "Half post-deadzone radius must use the quadratic quarter-speed response.");

        var fractional = new XInputMouseMotionEngine();
        fractional.ApplySettings(Settings(speed: 100, deadzone: 0.20), true, 8, TimeSpan.Zero);
        fractional.Observe(Frame(0, null, 0), 8, TimeSpan.Zero);
        fractional.Observe(Frame(0, 0, 1), 8, TimeSpan.Zero);
        Assert(fractional.Tick(TimeSpan.FromMilliseconds(8)) is null,
            "A subpixel tick must wait instead of rounding into jitter.");
        Assert(fractional.Tick(TimeSpan.FromMilliseconds(16)) == new MouseRelativeDelta(1, 0),
            "Subpixel travel must accumulate into later steady movement.");
        return Task.CompletedTask;
    }

    public static Task LowestActiveSlotAndSelectedStickWinAsync()
    {
        var engine = new XInputMouseMotionEngine();
        engine.ApplySettings(
            Settings(source: StickMouseSource.Right, speed: 1_000),
            true,
            11,
            TimeSpan.Zero);
        engine.Observe(Frame(0, null, 0, rightAngle: null, rightRadius: 0), 11, TimeSpan.Zero);
        engine.Observe(Frame(1, null, 0, rightAngle: null, rightRadius: 0), 11, TimeSpan.Zero);
        engine.Observe(Frame(1, 180, 1, rightAngle: 90, rightRadius: 1), 11, TimeSpan.Zero);
        engine.Observe(Frame(0, 180, 1, rightAngle: 0, rightRadius: 1), 11, TimeSpan.Zero);

        Assert(engine.Tick(TimeSpan.FromMilliseconds(10)) == new MouseRelativeDelta(10, 0),
            "The selected right stick from the lowest deflected user slot must win.");
        return Task.CompletedTask;
    }

    public static Task RevisionSwitchRequiresCenterAndRejectsLateFramesAsync()
    {
        var engine = new XInputMouseMotionEngine();
        var settings = Settings(speed: 1_000);
        engine.ApplySettings(settings, true, 20, TimeSpan.Zero);
        engine.Observe(Frame(0, 0, 1), 20, TimeSpan.Zero);
        Assert(engine.Tick(TimeSpan.FromMilliseconds(10)) is null,
            "A stick held when mouse mode starts must remain quarantined.");

        engine.Observe(Frame(0, null, 0), 20, TimeSpan.FromMilliseconds(10));
        engine.Observe(Frame(0, 0, 1), 20, TimeSpan.FromMilliseconds(10));
        Assert(engine.Tick(TimeSpan.FromMilliseconds(20)) == new MouseRelativeDelta(10, 0),
            "Returning to center must release the held-stick quarantine.");

        engine.ApplySettings(settings, true, 21, TimeSpan.FromMilliseconds(20));
        engine.Observe(Frame(0, null, 0), 20, TimeSpan.FromMilliseconds(20));
        engine.Observe(Frame(0, 0, 1), 21, TimeSpan.FromMilliseconds(20));
        Assert(engine.Tick(TimeSpan.FromMilliseconds(30)) is null,
            "A late centered frame from the old revision must not release quarantine.");
        engine.Observe(Frame(0, null, 0), 21, TimeSpan.FromMilliseconds(30));
        engine.Observe(Frame(0, 0, 1), 21, TimeSpan.FromMilliseconds(30));
        Assert(engine.Tick(TimeSpan.FromMilliseconds(40)) == new MouseRelativeDelta(10, 0),
            "The current revision must resume only after its own centered frame.");
        return Task.CompletedTask;
    }

    public static Task DisconnectDisableAndStaleCaptureStopMovementAsync()
    {
        var engine = new XInputMouseMotionEngine();
        var settings = Settings(speed: 1_000);
        engine.ApplySettings(settings, true, 30, TimeSpan.Zero);
        engine.Observe(Frame(0, null, 0), 30, TimeSpan.Zero);
        engine.Observe(Frame(0, 0, 1), 30, TimeSpan.Zero);
        Assert(engine.Tick(TimeSpan.FromMilliseconds(10)).HasValue, "The prepared live stick must move.");

        engine.Observe(Frame(0, null, 0, isConnected: false), 30, TimeSpan.FromMilliseconds(10));
        Assert(engine.Tick(TimeSpan.FromMilliseconds(20)) is null, "Disconnect must stop movement immediately.");

        engine.Observe(Frame(0, null, 0), 30, TimeSpan.FromMilliseconds(20));
        engine.Observe(Frame(0, 0, 1), 30, TimeSpan.FromMilliseconds(20));
        Assert(engine.Tick(TimeSpan.FromMilliseconds(280)) is null,
            "A missing XInput heartbeat must stop stale held movement.");

        engine.ApplySettings(settings, isMappingEnabled: false, 31, TimeSpan.FromMilliseconds(280));
        engine.Observe(Frame(0, null, 0), 31, TimeSpan.FromMilliseconds(280));
        engine.Observe(Frame(0, 0, 1), 31, TimeSpan.FromMilliseconds(280));
        Assert(engine.Tick(TimeSpan.FromMilliseconds(290)) is null,
            "The global mapping switch must also disable stick mouse movement.");
        return Task.CompletedTask;
    }

    public static async Task PollingHeartbeatRepeatsSteadyConnectedStateAsync()
    {
        var reader = new SteadyXInputReader();
        using var source = new XInputGamepadSource(reader, TimeSpan.FromMilliseconds(1));
        var sampled = 0;
        var changed = 0;
        var enoughSamples = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.StateSampled += (_, _) =>
        {
            if (Interlocked.Increment(ref sampled) >= 3)
            {
                enoughSamples.TrySetResult();
            }
        };
        source.RawStateChanged += (_, _) => Interlocked.Increment(ref changed);
        source.Start();

        await enoughSamples.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.Stop();
        Assert(sampled >= 3, "A steady held state needs repeated sampling heartbeats.");
        Assert(changed == 1, "The existing raw diagnostic event must remain deduplicated.");
    }

    public static async Task OutputFailureDisablesContinuousRetryAsync()
    {
        var backend = new FailingMouseBackend();
        var clock = new ManualMouseClock();
        using var controller = new XInputMouseMotionController(backend, clock, startWorker: true);
        controller.ApplySettings(Settings(speed: 1_000), true, 40);
        controller.Observe(Frame(0, null, 0), 40);
        controller.Observe(Frame(0, 0, 1), 40);
        clock.Set(TimeSpan.FromMilliseconds(10));

        await backend.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(20);
        clock.Set(TimeSpan.FromMilliseconds(100));
        await Task.Delay(40);
        Assert(backend.CallCount == 1,
            "A failed SendInput backend must disable motion instead of retrying every polling tick.");
    }

    private static StickMouseSettings Settings(
        StickMouseSource source = StickMouseSource.Left,
        int speed = 900,
        double deadzone = 0.20) => new()
    {
        IsEnabled = true,
        Source = source,
        SpeedPixelsPerSecond = speed,
        Deadzone = deadzone
    };

    private static XInputStickAnalysisFrame Frame(
        int userIndex,
        double? angle,
        double radius,
        bool isConnected = true,
        double? rightAngle = null,
        double rightRadius = 0)
    {
        var timestamp = Epoch.AddMilliseconds(userIndex);
        return new XInputStickAnalysisFrame
        {
            UserIndex = userIndex,
            IsConnected = isConnected,
            PacketNumber = 1,
            Left = Analysis(userIndex, XInputStick.Left, angle, radius, isConnected, timestamp),
            Right = Analysis(userIndex, XInputStick.Right, rightAngle, rightRadius, isConnected, timestamp),
            TimestampUtc = timestamp
        };
    }

    private static XInputStickAnalysis Analysis(
        int userIndex,
        XInputStick stick,
        double? angle,
        double radius,
        bool isConnected,
        DateTimeOffset timestamp) => new()
    {
        UserIndex = userIndex,
        Stick = stick,
        IsConnected = isConnected,
        PacketNumber = 1,
        RawX = 0,
        RawY = 0,
        AngleDegrees = angle,
        Radius = radius,
        EffectiveDeadzoneRadius = 0.10,
        IsWithinDeadzone = !angle.HasValue,
        TimestampUtc = timestamp
    };

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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class SteadyXInputReader : IXInputStateReader
    {
        public XInputSnapshot Read(int userIndex) => userIndex == 0
            ? new XInputSnapshot(true, 1, 0, ThumbLX: 30_000)
            : XInputSnapshot.Disconnected;
    }

    private sealed class ManualMouseClock : IMonotonicMouseClock
    {
        private long _ticks;

        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        public void Set(TimeSpan elapsed) => Interlocked.Exchange(ref _ticks, elapsed.Ticks);
    }

    private sealed class FailingMouseBackend : IRelativeMouseBackend
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public TaskCompletionSource FirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MoveRelative(int deltaX, int deltaY)
        {
            Interlocked.Increment(ref _callCount);
            FirstCall.TrySetResult();
            throw new Win32Exception(5, "Synthetic SendInput failure.");
        }
    }
}
