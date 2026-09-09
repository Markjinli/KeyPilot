namespace KeyPilot.Platform.Windows.Input;

/// <summary>Identifies one of the two XInput thumb sticks.</summary>
public enum XInputStick
{
    Left = 0,
    Right = 1
}

/// <summary>The completed direction of one full thumb-stick rotation.</summary>
public enum XInputStickRotationDirection
{
    Clockwise = -1,
    CounterClockwise = 1
}

/// <summary>Fixed bounds used by <see cref="XInputStickAnalyzer"/>.</summary>
public static class XInputStickAnalysisThresholds
{
    public const short LeftBaseDeadzone = 7_849;
    public const short RightBaseDeadzone = 8_689;
    public const short CenterLearningRadius = 6_000;
    public const double CenterTrackingAlpha = 0.02;
    public const int InitialCenterSamples = 32;
    public const int RestNoiseWindow = 64;
    public const double RestNoisePercentile = 0.95;
    public const double RestNoiseMultiplier = 2.0;
    public const short DeadzonePadding = 256;
    public const double GestureEnterRadius = 0.55;
    public const double GestureExitRadius = 0.35;
    public const double AngularNoiseFloorDegrees = 0.35;
    public const double MaximumAngularStepDegrees = 120.0;
    public const double DirectionLockDegrees = 15.0;
    public const double DirectionReversalDegrees = 45.0;
    public const int AngularResolutionWindow = 128;
    public const int MinimumAngularResolutionSamples = 12;
    public const double AngularResolutionPercentile = 0.20;
    public const double MaximumResolutionSampleDegrees = 45.0;
    public const double FullRotationDegrees = 360.0;
    public const double FullRotationEpsilonDegrees = 0.000001;
}

/// <summary>A raw sample for one stick. XInput Y values are positive toward the top.</summary>
public readonly record struct XInputStickRawSample(
    int UserIndex,
    XInputStick Stick,
    bool IsConnected,
    uint PacketNumber,
    short X,
    short Y,
    DateTimeOffset TimestampUtc);

/// <summary>One edge emitted whenever the same stick completes another full 360-degree turn.</summary>
public readonly record struct XInputStickRotationEdge(
    int UserIndex,
    XInputStick Stick,
    XInputStickRotationDirection Direction,
    long SequenceNumber,
    DateTimeOffset TimestampUtc);

/// <summary>The current calibrated and gesture state of one stick.</summary>
public sealed record XInputStickAnalysis
{
    public required int UserIndex { get; init; }

    public required XInputStick Stick { get; init; }

    public required bool IsConnected { get; init; }

    public required uint PacketNumber { get; init; }

    public required short RawX { get; init; }

    public required short RawY { get; init; }

    /// <summary>0 is right and 90 is up. Null inside the effective deadzone.</summary>
    public double? AngleDegrees { get; init; }

    /// <summary>Center-corrected radial magnitude normalized to the physical XInput range.</summary>
    public double Radius { get; init; }

    public double CenterXRaw { get; init; }

    public double CenterYRaw { get; init; }

    /// <summary>Estimated center offset from the nominal origin, normalized to 0..1.</summary>
    public double CenterDriftRadius { get; init; }

    /// <summary>Noise-aware radial deadzone normalized to 0..1.</summary>
    public double EffectiveDeadzoneRadius { get; init; }

    public bool IsWithinDeadzone { get; init; }

    public bool IsGestureTracking { get; init; }

    public XInputStickRotationDirection? GestureDirection { get; init; }

    /// <summary>Retained directional progress toward the next full rotation, from 0 to 360.</summary>
    public double GestureProgressDegrees { get; init; }

    /// <summary>
    /// Low-percentile observed angular step at a reliable radius. Null until enough samples exist.
    /// This is an effective observed resolution, not a claim about the controller ADC bit depth.
    /// </summary>
    public double? EffectiveAngularResolutionDegrees { get; init; }

    public XInputStickRotationEdge? RotationEdge { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>Analysis of both sticks from one raw XInput state event.</summary>
public sealed record XInputStickAnalysisFrame
{
    public required int UserIndex { get; init; }

    public required bool IsConnected { get; init; }

    public required uint PacketNumber { get; init; }

    public required XInputStickAnalysis Left { get; init; }

    public required XInputStickAnalysis Right { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>
/// Stateful, deterministic analysis of XInput thumb-stick samples. State is isolated by user slot
/// and stick. The analyzer performs no I/O and is safe to call from multiple threads.
/// </summary>
public sealed class XInputStickAnalyzer
{
    private const double XInputMagnitude = short.MaxValue;
    private const int StickCount = 2;

    private readonly object _gate = new();
    private readonly StickState[,] _states = new StickState[XInputGamepadSource.UserSlotCount, StickCount];

    public XInputStickAnalyzer()
    {
        for (var userIndex = 0; userIndex < XInputGamepadSource.UserSlotCount; userIndex++)
        {
            for (var stickIndex = 0; stickIndex < StickCount; stickIndex++)
            {
                _states[userIndex, stickIndex] = new StickState();
            }
        }
    }

    /// <summary>Processes both sticks from one deduplicated raw XInput event.</summary>
    public XInputStickAnalysisFrame Process(XInputRawStateChangedEventArgs sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ValidateUserIndex(sample.UserIndex);

        lock (_gate)
        {
            var left = ProcessCore(new XInputStickRawSample(
                sample.UserIndex,
                XInputStick.Left,
                sample.IsConnected,
                sample.PacketNumber,
                sample.ThumbLX,
                sample.ThumbLY,
                sample.TimestampUtc));
            var right = ProcessCore(new XInputStickRawSample(
                sample.UserIndex,
                XInputStick.Right,
                sample.IsConnected,
                sample.PacketNumber,
                sample.ThumbRX,
                sample.ThumbRY,
                sample.TimestampUtc));

            return new XInputStickAnalysisFrame
            {
                UserIndex = sample.UserIndex,
                IsConnected = sample.IsConnected,
                PacketNumber = sample.PacketNumber,
                Left = left,
                Right = right,
                TimestampUtc = sample.TimestampUtc
            };
        }
    }

    /// <summary>Processes one stick sample, primarily for independent capture pipelines and tests.</summary>
    public XInputStickAnalysis Process(XInputStickRawSample sample)
    {
        ValidateSample(sample);
        lock (_gate)
        {
            return ProcessCore(sample);
        }
    }

    public void Reset(int userIndex)
    {
        ValidateUserIndex(userIndex);
        lock (_gate)
        {
            _states[userIndex, (int)XInputStick.Left] = new StickState();
            _states[userIndex, (int)XInputStick.Right] = new StickState();
        }
    }

    public void Reset(int userIndex, XInputStick stick)
    {
        ValidateUserIndex(userIndex);
        ValidateStick(stick);
        lock (_gate)
        {
            _states[userIndex, (int)stick] = new StickState();
        }
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            for (var userIndex = 0; userIndex < XInputGamepadSource.UserSlotCount; userIndex++)
            {
                for (var stickIndex = 0; stickIndex < StickCount; stickIndex++)
                {
                    _states[userIndex, stickIndex] = new StickState();
                }
            }
        }
    }

    private XInputStickAnalysis ProcessCore(XInputStickRawSample sample)
    {
        var state = _states[sample.UserIndex, (int)sample.Stick];
        if (!sample.IsConnected)
        {
            state = new StickState();
            _states[sample.UserIndex, (int)sample.Stick] = state;
            return CreateAnalysis(sample, state, angle: null, radius: 0, deadzone: BaseDeadzone(sample.Stick), edge: null);
        }

        var offsetX = sample.X - state.CenterX;
        var offsetY = sample.Y - state.CenterY;
        var distanceFromCenter = Math.Sqrt((offsetX * offsetX) + (offsetY * offsetY));
        if (!state.IsGestureTracking && distanceFromCenter <= XInputStickAnalysisThresholds.CenterLearningRadius)
        {
            LearnCenter(state, sample.X, sample.Y);
            offsetX = sample.X - state.CenterX;
            offsetY = sample.Y - state.CenterY;
            distanceFromCenter = Math.Sqrt((offsetX * offsetX) + (offsetY * offsetY));
            state.RestNoise.Add(distanceFromCenter);
        }

        var deadzone = EffectiveDeadzone(state, sample.Stick);
        var isWithinDeadzone = distanceFromCenter <= deadzone;
        var radius = Math.Min(1.0, distanceFromCenter / XInputMagnitude);
        double? angle = isWithinDeadzone
            ? null
            : NormalizeAngle(Math.Atan2(offsetY, offsetX) * (180.0 / Math.PI));
        var edge = UpdateGesture(state, sample, radius, angle);

        return CreateAnalysis(sample, state, angle, radius, deadzone, edge);
    }

    private static XInputStickRotationEdge? UpdateGesture(
        StickState state,
        XInputStickRawSample sample,
        double radius,
        double? angle)
    {
        if (!state.IsGestureTracking)
        {
            if (angle.HasValue && radius >= XInputStickAnalysisThresholds.GestureEnterRadius)
            {
                state.IsGestureTracking = true;
                state.PreviousAngleDegrees = angle.Value;
                state.ReversalTravelDegrees = 0;
            }

            return null;
        }

        if (!angle.HasValue || radius <= XInputStickAnalysisThresholds.GestureExitRadius)
        {
            // Returning to center pauses the gesture. Progress is retained, while clearing the
            // angular anchor prevents a jump to the next exit angle from being counted.
            state.IsGestureTracking = false;
            state.PreviousAngleDegrees = null;
            state.ReversalTravelDegrees = 0;
            return null;
        }

        if (!state.PreviousAngleDegrees.HasValue)
        {
            state.PreviousAngleDegrees = angle.Value;
            return null;
        }

        var delta = ShortestSignedAngle(state.PreviousAngleDegrees.Value, angle.Value);
        state.PreviousAngleDegrees = angle.Value;
        var magnitude = Math.Abs(delta);
        if (magnitude < XInputStickAnalysisThresholds.AngularNoiseFloorDegrees)
        {
            return null;
        }

        if (magnitude > XInputStickAnalysisThresholds.MaximumAngularStepDegrees)
        {
            // At one sample the direction of a larger jump is ambiguous. Re-anchor without
            // allowing it to complete or reverse a gesture.
            state.ReversalTravelDegrees = 0;
            return null;
        }

        if (magnitude <= XInputStickAnalysisThresholds.MaximumResolutionSampleDegrees)
        {
            state.AngularSteps.Add(magnitude);
        }

        if (!state.Direction.HasValue)
        {
            state.UnlockedSignedTravelDegrees += delta;
            if (Math.Abs(state.UnlockedSignedTravelDegrees) < XInputStickAnalysisThresholds.DirectionLockDegrees)
            {
                return null;
            }

            state.Direction = DirectionOf(state.UnlockedSignedTravelDegrees);
            state.ProgressDegrees = Math.Abs(state.UnlockedSignedTravelDegrees);
            state.UnlockedSignedTravelDegrees = 0;
        }
        else
        {
            var directionSign = (int)state.Direction.Value;
            var travelInDirection = delta * directionSign;
            if (travelInDirection >= 0)
            {
                state.ProgressDegrees += travelInDirection;
                state.ReversalTravelDegrees = 0;
            }
            else
            {
                var reverseTravel = -travelInDirection;
                state.ProgressDegrees = Math.Max(0, state.ProgressDegrees - reverseTravel);
                state.ReversalTravelDegrees += reverseTravel;
                if (state.ReversalTravelDegrees >= XInputStickAnalysisThresholds.DirectionReversalDegrees)
                {
                    state.Direction = Opposite(state.Direction.Value);
                    state.ProgressDegrees = state.ReversalTravelDegrees;
                    state.ReversalTravelDegrees = 0;
                }
            }
        }

        if (state.ProgressDegrees <
                XInputStickAnalysisThresholds.FullRotationDegrees -
                XInputStickAnalysisThresholds.FullRotationEpsilonDegrees ||
            !state.Direction.HasValue)
        {
            return null;
        }

        state.ProgressDegrees = Math.Max(
            0,
            state.ProgressDegrees - XInputStickAnalysisThresholds.FullRotationDegrees);
        state.EdgeSequence++;
        return new XInputStickRotationEdge(
            sample.UserIndex,
            sample.Stick,
            state.Direction.Value,
            state.EdgeSequence,
            sample.TimestampUtc);
    }

    private static void LearnCenter(StickState state, short rawX, short rawY)
    {
        state.CenterSampleCount++;
        var alpha = state.CenterSampleCount <= XInputStickAnalysisThresholds.InitialCenterSamples
            ? 1.0 / state.CenterSampleCount
            : XInputStickAnalysisThresholds.CenterTrackingAlpha;
        state.CenterX += (rawX - state.CenterX) * alpha;
        state.CenterY += (rawY - state.CenterY) * alpha;
    }

    private static double EffectiveDeadzone(StickState state, XInputStick stick)
    {
        var baseDeadzone = BaseDeadzone(stick);
        if (state.RestNoise.Count == 0)
        {
            return baseDeadzone;
        }

        var observedNoise = state.RestNoise.Percentile(XInputStickAnalysisThresholds.RestNoisePercentile);
        return Math.Max(
            baseDeadzone,
            (observedNoise * XInputStickAnalysisThresholds.RestNoiseMultiplier) +
            XInputStickAnalysisThresholds.DeadzonePadding);
    }

    private static XInputStickAnalysis CreateAnalysis(
        XInputStickRawSample sample,
        StickState state,
        double? angle,
        double radius,
        double deadzone,
        XInputStickRotationEdge? edge)
    {
        var centerDrift = Math.Min(
            1.0,
            Math.Sqrt((state.CenterX * state.CenterX) + (state.CenterY * state.CenterY)) / XInputMagnitude);
        double? resolution = state.AngularSteps.Count < XInputStickAnalysisThresholds.MinimumAngularResolutionSamples
            ? null
            : state.AngularSteps.Percentile(XInputStickAnalysisThresholds.AngularResolutionPercentile);

        return new XInputStickAnalysis
        {
            UserIndex = sample.UserIndex,
            Stick = sample.Stick,
            IsConnected = sample.IsConnected,
            PacketNumber = sample.PacketNumber,
            RawX = sample.X,
            RawY = sample.Y,
            AngleDegrees = angle,
            Radius = radius,
            CenterXRaw = state.CenterX,
            CenterYRaw = state.CenterY,
            CenterDriftRadius = centerDrift,
            EffectiveDeadzoneRadius = Math.Min(1.0, deadzone / XInputMagnitude),
            IsWithinDeadzone = !angle.HasValue,
            IsGestureTracking = state.IsGestureTracking,
            GestureDirection = state.Direction,
            GestureProgressDegrees = state.ProgressDegrees,
            EffectiveAngularResolutionDegrees = resolution,
            RotationEdge = edge,
            TimestampUtc = sample.TimestampUtc
        };
    }

    private static double BaseDeadzone(XInputStick stick) => stick == XInputStick.Left
        ? XInputStickAnalysisThresholds.LeftBaseDeadzone
        : XInputStickAnalysisThresholds.RightBaseDeadzone;

    private static double NormalizeAngle(double angleDegrees)
    {
        var normalized = angleDegrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    private static double ShortestSignedAngle(double fromDegrees, double toDegrees)
    {
        var delta = (toDegrees - fromDegrees) % 360.0;
        if (delta > 180.0)
        {
            delta -= 360.0;
        }
        else if (delta <= -180.0)
        {
            delta += 360.0;
        }

        return delta;
    }

    private static XInputStickRotationDirection DirectionOf(double signedDegrees) => signedDegrees < 0
        ? XInputStickRotationDirection.Clockwise
        : XInputStickRotationDirection.CounterClockwise;

    private static XInputStickRotationDirection Opposite(XInputStickRotationDirection direction) =>
        direction == XInputStickRotationDirection.Clockwise
            ? XInputStickRotationDirection.CounterClockwise
            : XInputStickRotationDirection.Clockwise;

    private static void ValidateSample(XInputStickRawSample sample)
    {
        ValidateUserIndex(sample.UserIndex);
        ValidateStick(sample.Stick);
    }

    private static void ValidateUserIndex(int userIndex)
    {
        if ((uint)userIndex >= XInputGamepadSource.UserSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(userIndex));
        }
    }

    private static void ValidateStick(XInputStick stick)
    {
        if (stick is not XInputStick.Left and not XInputStick.Right)
        {
            throw new ArgumentOutOfRangeException(nameof(stick));
        }
    }

    private sealed class StickState
    {
        public double CenterX { get; set; }

        public double CenterY { get; set; }

        public int CenterSampleCount { get; set; }

        public RollingValues RestNoise { get; } = new(XInputStickAnalysisThresholds.RestNoiseWindow);

        public bool IsGestureTracking { get; set; }

        public double? PreviousAngleDegrees { get; set; }

        public XInputStickRotationDirection? Direction { get; set; }

        public double UnlockedSignedTravelDegrees { get; set; }

        public double ProgressDegrees { get; set; }

        public double ReversalTravelDegrees { get; set; }

        public long EdgeSequence { get; set; }

        public RollingValues AngularSteps { get; } = new(XInputStickAnalysisThresholds.AngularResolutionWindow);
    }

    private sealed class RollingValues(int capacity)
    {
        private readonly double[] _values = new double[capacity];
        private int _next;

        public int Count { get; private set; }

        public void Add(double value)
        {
            _values[_next] = value;
            _next = (_next + 1) % _values.Length;
            Count = Math.Min(Count + 1, _values.Length);
        }

        public double Percentile(double percentile)
        {
            if (Count == 0)
            {
                throw new InvalidOperationException("A percentile requires at least one value.");
            }

            var ordered = new double[Count];
            Array.Copy(_values, ordered, Count);
            Array.Sort(ordered);
            var index = (int)Math.Floor((ordered.Length - 1) * percentile);
            return ordered[index];
        }
    }
}
