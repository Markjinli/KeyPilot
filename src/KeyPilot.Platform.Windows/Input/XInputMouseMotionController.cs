using System.Diagnostics;
using KeyPilot.Core.Configuration;
using KeyPilot.Platform.Windows.Actions;

namespace KeyPilot.Platform.Windows.Input;

/// <summary>
/// Converts the latest analyzed XInput stick state into a steady stream of relative cursor deltas.
/// Configuration replacement is a safety boundary: a held stick must return to center before it
/// can move the cursor under the new runtime revision.
/// </summary>
public sealed class XInputMouseMotionController : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan FaultReportInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly IRelativeMouseBackend _backend;
    private readonly IMonotonicMouseClock _clock;
    private readonly XInputMouseMotionEngine _engine;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Thread _worker;
    private long _lastFaultReportTimestamp;
    private bool _stopped;
    private bool _disposed;

    public XInputMouseMotionController(InputInjectionMarker originMarker)
        : this(
            new WindowsRelativeMouseBackend(originMarker),
            StopwatchMouseClock.Instance,
            startWorker: true)
    {
    }

    internal XInputMouseMotionController(
        IRelativeMouseBackend backend,
        IMonotonicMouseClock clock,
        bool startWorker)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _engine = new XInputMouseMotionEngine();
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "KeyPilot XInput mouse motion"
        };

        if (startWorker)
        {
            _worker.Start();
        }
        else
        {
            _stopped = true;
        }
    }

    public event EventHandler<Exception>? Faulted;

    public void ApplySettings(
        StickMouseSettings settings,
        bool isMappingEnabled,
        long runtimeRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _engine.ApplySettings(settings, isMappingEnabled, runtimeRevision, _clock.Elapsed);
        }
    }

    /// <summary>Submits one analyzed heartbeat for the matching runtime revision.</summary>
    public void Observe(XInputStickAnalysisFrame frame, long runtimeRevision)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_disposed || _stopped)
            {
                return;
            }

            _engine.Observe(frame, runtimeRevision, _clock.Elapsed);
        }
    }

    public void ResetUser(int userIndex)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _engine.ResetUser(userIndex);
        }
    }

    public void Stop()
    {
        Thread? worker = null;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _engine.Disable();
            _cancellation.Cancel();
            worker = _worker;
        }

        if (worker.IsAlive && worker != Thread.CurrentThread && !worker.Join(StopTimeout))
        {
            ReportFault(new TimeoutException("The XInput mouse motion thread did not stop within two seconds."));
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Run()
    {
        try
        {
            while (!_cancellation.Token.WaitHandle.WaitOne(TickInterval))
            {
                MouseRelativeDelta? delta;
                lock (_gate)
                {
                    if (_stopped)
                    {
                        return;
                    }

                    delta = _engine.Tick(_clock.Elapsed);
                }

                if (delta is { } movement)
                {
                    try
                    {
                        _backend.MoveRelative(movement.X, movement.Y);
                    }
                    catch (Exception exception) when (!IsFatalProcessException(exception))
                    {
                        // A failing SendInput path is a safety boundary. Do not hammer user32 at
                        // polling frequency or keep an apparently active controller after output
                        // has become unreliable; a later configuration apply can explicitly retry.
                        lock (_gate)
                        {
                            _engine.Disable();
                        }

                        ReportFault(exception);
                    }
                }
            }
        }
        catch (Exception exception) when (!IsFatalProcessException(exception))
        {
            ReportFault(exception);
        }
    }

    private void ReportFault(Exception exception)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastFaultReportTimestamp);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < FaultReportInterval)
        {
            return;
        }

        Interlocked.Exchange(ref _lastFaultReportTimestamp, now);
        var handlers = Faulted;
        if (handlers is null)
        {
            return;
        }

        foreach (var subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<Exception>)subscriber)(this, exception);
            }
            catch (Exception callbackException) when (!IsFatalProcessException(callbackException))
            {
                // A diagnostic subscriber cannot stop cursor control or the shutdown path.
            }
        }
    }

    private static bool IsFatalProcessException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal readonly record struct MouseRelativeDelta(int X, int Y);

internal interface IMonotonicMouseClock
{
    TimeSpan Elapsed { get; }
}

internal sealed class StopwatchMouseClock : IMonotonicMouseClock
{
    private readonly long _startedAt = Stopwatch.GetTimestamp();

    public static StopwatchMouseClock Instance { get; } = new();

    private StopwatchMouseClock()
    {
    }

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startedAt);
}

/// <summary>Pure, lock-free motion state used behind the controller lock and by focused tests.</summary>
internal sealed class XInputMouseMotionEngine
{
    internal static readonly TimeSpan SampleFreshness = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaximumTickDuration = TimeSpan.FromMilliseconds(50);

    private readonly SlotState[] _slots = Enumerable.Range(0, XInputGamepadSource.UserSlotCount)
        .Select(_ => new SlotState())
        .ToArray();

    private StickMouseSettings _settings = new();
    private bool _enabled;
    private long _runtimeRevision = long.MinValue;
    private TimeSpan? _lastTick;
    private int? _activeUserIndex;
    private double _remainderX;
    private double _remainderY;

    public void ApplySettings(
        StickMouseSettings settings,
        bool isMappingEnabled,
        long runtimeRevision,
        TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings with
        {
            SpeedPixelsPerSecond = Math.Clamp(
                settings.SpeedPixelsPerSecond,
                StickMouseSettings.MinimumSpeedPixelsPerSecond,
                StickMouseSettings.MaximumSpeedPixelsPerSecond),
            Deadzone = Math.Clamp(
                settings.Deadzone,
                StickMouseSettings.MinimumDeadzone,
                StickMouseSettings.MaximumDeadzone)
        };
        _enabled = isMappingEnabled && _settings.IsEnabled;
        _runtimeRevision = runtimeRevision;
        _lastTick = now;
        ResetMotion();

        foreach (var slot in _slots)
        {
            // Even a newly connected controller must prove it has returned to center. This avoids
            // a cursor jump when KeyPilot starts or a foreground profile changes while held.
            slot.NeedsCenter = _enabled;
        }
    }

    public void Observe(XInputStickAnalysisFrame frame, long runtimeRevision, TimeSpan now)
    {
        if ((uint)frame.UserIndex >= XInputGamepadSource.UserSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (runtimeRevision != _runtimeRevision)
        {
            return;
        }

        var slot = _slots[frame.UserIndex];
        slot.Frame = frame;
        slot.ObservedAt = now;
        if (!frame.IsConnected)
        {
            slot.NeedsCenter = _enabled;
            if (_activeUserIndex == frame.UserIndex)
            {
                ResetMotion();
            }

            return;
        }

        if (slot.NeedsCenter && IsCentered(SelectedStick(frame)))
        {
            slot.NeedsCenter = false;
        }
    }

    public MouseRelativeDelta? Tick(TimeSpan now)
    {
        var elapsed = _lastTick.HasValue ? now - _lastTick.Value : TimeSpan.Zero;
        _lastTick = now;
        if (!_enabled || elapsed <= TimeSpan.Zero)
        {
            ResetMotion();
            return null;
        }

        if (elapsed > MaximumTickDuration)
        {
            elapsed = MaximumTickDuration;
        }

        var candidate = _slots
            .Select((slot, userIndex) => (slot, userIndex))
            .Where(item => IsEligible(item.slot, now))
            .OrderBy(item => item.userIndex)
            .FirstOrDefault();
        if (candidate.slot is null)
        {
            ResetMotion();
            return null;
        }

        if (_activeUserIndex != candidate.userIndex)
        {
            ResetMotion();
            _activeUserIndex = candidate.userIndex;
        }

        var analysis = SelectedStick(candidate.slot.Frame!);
        var deadzone = Math.Max(_settings.Deadzone, analysis.EffectiveDeadzoneRadius);
        var normalizedRadius = Math.Clamp(
            (analysis.Radius - deadzone) / Math.Max(double.Epsilon, 1.0 - deadzone),
            0,
            1);
        // A quadratic response keeps precise center movement while preserving the configured
        // maximum speed at the edge.
        var pixels = _settings.SpeedPixelsPerSecond * normalizedRadius * normalizedRadius * elapsed.TotalSeconds;
        var radians = analysis.AngleDegrees!.Value * (Math.PI / 180.0);
        _remainderX += Math.Cos(radians) * pixels;
        _remainderY += -Math.Sin(radians) * pixels; // XInput up is positive; screen up is negative.

        var deltaX = ExtractWholePixels(_remainderX);
        var deltaY = ExtractWholePixels(_remainderY);
        if (deltaX == 0 && deltaY == 0)
        {
            return null;
        }

        _remainderX -= deltaX;
        _remainderY -= deltaY;
        if (Math.Abs(_remainderX) < 1e-9)
        {
            _remainderX = 0;
        }

        if (Math.Abs(_remainderY) < 1e-9)
        {
            _remainderY = 0;
        }

        return new MouseRelativeDelta(deltaX, deltaY);
    }

    public void ResetUser(int userIndex)
    {
        if ((uint)userIndex >= XInputGamepadSource.UserSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(userIndex));
        }

        _slots[userIndex] = new SlotState { NeedsCenter = _enabled };
        if (_activeUserIndex == userIndex)
        {
            ResetMotion();
        }
    }

    public void Disable()
    {
        _enabled = false;
        ResetMotion();
    }

    private bool IsEligible(SlotState slot, TimeSpan now)
    {
        if (slot.Frame is null || !slot.Frame.IsConnected || slot.NeedsCenter ||
            now - slot.ObservedAt > SampleFreshness)
        {
            return false;
        }

        return !IsCentered(SelectedStick(slot.Frame));
    }

    private bool IsCentered(XInputStickAnalysis analysis)
    {
        var effectiveDeadzone = Math.Max(_settings.Deadzone, analysis.EffectiveDeadzoneRadius);
        return analysis.IsWithinDeadzone || !analysis.AngleDegrees.HasValue || analysis.Radius <= effectiveDeadzone;
    }

    private XInputStickAnalysis SelectedStick(XInputStickAnalysisFrame frame) =>
        _settings.Source == StickMouseSource.Left ? frame.Left : frame.Right;

    private static int ExtractWholePixels(double value)
    {
        const double floatingPointTolerance = 1e-9;
        var whole = value >= 0
            ? Math.Floor(value + floatingPointTolerance)
            : Math.Ceiling(value - floatingPointTolerance);
        return checked((int)whole);
    }

    private void ResetMotion()
    {
        _activeUserIndex = null;
        _remainderX = 0;
        _remainderY = 0;
    }

    private sealed class SlotState
    {
        public XInputStickAnalysisFrame? Frame { get; set; }

        public TimeSpan ObservedAt { get; set; }

        public bool NeedsCenter { get; set; }
    }
}
