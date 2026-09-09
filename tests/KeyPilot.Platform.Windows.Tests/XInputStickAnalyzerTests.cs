using KeyPilot.Platform.Windows.Input;

namespace KeyPilot.Platform.Windows.Tests;

internal static class XInputStickAnalyzerTests
{
    private const double AngleToleranceDegrees = 0.1;

    public static Task CardinalAnglesAndDeadzoneAreReportedAsync()
    {
        var harness = new StickHarness();

        var right = harness.Sample(XInputStick.Left, 30_000, 0);
        AssertNear(0, right.AngleDegrees, AngleToleranceDegrees, "Positive X must be zero degrees.");

        harness.Analyzer.Reset(0, XInputStick.Left);
        var up = harness.Sample(XInputStick.Left, 0, 30_000);
        AssertNear(90, up.AngleDegrees, AngleToleranceDegrees, "Positive Y must be 90 degrees.");

        harness.Analyzer.Reset(0, XInputStick.Left);
        var centered = harness.Sample(XInputStick.Left, 2_000, -1_000);
        Assert(centered.AngleDegrees is null, "A sample inside the effective deadzone must have no angle.");
        Assert(centered.IsWithinDeadzone, "A centered sample must be marked inside the deadzone.");
        Assert(!centered.IsGestureTracking, "A centered sample must not start rotation tracking.");
        return Task.CompletedTask;
    }

    public static Task ArbitraryStartRotationsCrossZeroInBothDirectionsAsync()
    {
        var clockwiseHarness = new StickHarness();
        var clockwise = clockwiseHarness.Arc(
            XInputStick.Left,
            startDegrees: 137,
            travelDegrees: -360,
            stepDegrees: 10);
        AssertSingleEdge(
            clockwise,
            userIndex: 0,
            XInputStick.Left,
            XInputStickRotationDirection.Clockwise);

        var counterClockwiseHarness = new StickHarness();
        var counterClockwise = counterClockwiseHarness.Arc(
            XInputStick.Left,
            startDegrees: 287,
            travelDegrees: 360,
            stepDegrees: 10);
        AssertSingleEdge(
            counterClockwise,
            userIndex: 0,
            XInputStick.Left,
            XInputStickRotationDirection.CounterClockwise);
        return Task.CompletedTask;
    }

    public static Task TwoTurnsEmitTwoIndependentEdgesAsync()
    {
        var harness = new StickHarness();
        var samples = harness.Arc(
            XInputStick.Right,
            startDegrees: 53,
            travelDegrees: 720,
            stepDegrees: 10);
        var edges = Edges(samples);

        Assert(edges.Count == 2, "Two complete turns must emit exactly two edges.");
        Assert(edges.All(edge => edge.Direction == XInputStickRotationDirection.CounterClockwise),
            "Both edges must preserve the rotation direction.");
        Assert(edges.Select(edge => edge.SequenceNumber).SequenceEqual([1L, 2L]),
            "Each completed turn must receive its own increasing sequence number.");
        return Task.CompletedTask;
    }

    public static Task ReturningToCenterPausesAndResumesProgressAsync()
    {
        var harness = new StickHarness();
        var firstHalf = harness.Arc(
            XInputStick.Left,
            startDegrees: 23,
            travelDegrees: 180,
            stepDegrees: 10);
        Assert(Edges(firstHalf).Count == 0, "A half turn must not emit an edge.");

        var centered = harness.Sample(XInputStick.Left, 0, 0);
        Assert(!centered.IsGestureTracking, "Returning to center must pause gesture tracking.");
        Assert(centered.GestureProgressDegrees > 170, "Returning to center must retain completed progress.");

        var secondHalf = harness.Arc(
            XInputStick.Left,
            startDegrees: 241,
            travelDegrees: 180,
            stepDegrees: 10);
        AssertSingleEdge(
            secondHalf,
            userIndex: 0,
            XInputStick.Left,
            XInputStickRotationDirection.CounterClockwise);
        return Task.CompletedTask;
    }

    public static Task ReversalCannotCreateFalseCompletionAsync()
    {
        var harness = new StickHarness();
        var forward = harness.Arc(
            XInputStick.Left,
            startDegrees: 15,
            travelDegrees: 300,
            stepDegrees: 10);
        var reverse = harness.Arc(
            XInputStick.Left,
            startDegrees: 315,
            travelDegrees: -300,
            stepDegrees: 10);

        Assert(Edges(forward).Count == 0 && Edges(reverse).Count == 0,
            "Reversing before a full turn must not manufacture a completion edge.");
        var final = reverse[^1];
        Assert(final.GestureDirection == XInputStickRotationDirection.Clockwise,
            "Sustained reverse travel must relock to the new direction.");
        Assert(final.GestureProgressDegrees < 360,
            "Reverse travel below one full turn must remain incomplete.");
        return Task.CompletedTask;
    }

    public static Task SmallRadiusMovementNeverStartsGestureAsync()
    {
        var harness = new StickHarness();
        var samples = harness.Arc(
            XInputStick.Left,
            startDegrees: 40,
            travelDegrees: 720,
            stepDegrees: 10,
            radius: 14_000);

        Assert(samples.Any(sample => sample.AngleDegrees.HasValue),
            "The test radius must remain outside the reporting deadzone.");
        Assert(samples.All(sample => !sample.IsGestureTracking),
            "Movement below the gesture entry radius must never start tracking.");
        Assert(Edges(samples).Count == 0,
            "Small-radius circles must never emit rotation edges.");
        return Task.CompletedTask;
    }

    public static Task CenterDriftAndEffectiveResolutionAreEstimatedAsync()
    {
        const short centerX = 1_200;
        const short centerY = -800;
        var harness = new StickHarness();

        XInputStickAnalysis? centered = null;
        for (var index = 0; index < 40; index++)
        {
            centered = harness.Sample(XInputStick.Left, centerX, centerY);
        }

        var calibrated = centered ?? throw new InvalidOperationException(
            "Center calibration must produce an analysis sample.");
        AssertNear(centerX, calibrated.CenterXRaw, 0.01, "The learned X center must follow stable drift.");
        AssertNear(centerY, calibrated.CenterYRaw, 0.01, "The learned Y center must follow stable drift.");
        Assert(calibrated.CenterDriftRadius > 0.04 && calibrated.CenterDriftRadius < 0.05,
            "Center drift must be normalized and exposed to the UI.");

        var arc = harness.Arc(
            XInputStick.Left,
            startDegrees: 0,
            travelDegrees: 100,
            stepDegrees: 5,
            radius: 25_000,
            centerX,
            centerY);
        var resolution = arc[^1].EffectiveAngularResolutionDegrees;
        Assert(resolution.HasValue, "Enough reliable angular steps must produce a resolution estimate.");
        AssertNear(5, resolution, 0.1, "A stable five-degree sampling step must be estimated near five degrees.");
        return Task.CompletedTask;
    }

    public static Task UsersAndSticksKeepIndependentStateAsync()
    {
        var harness = new StickHarness();
        Assert(Edges(harness.Arc(XInputStick.Left, 0, 180, 10, userIndex: 0)).Count == 0,
            "User 0 left stick must remain incomplete after a half turn.");
        Assert(Edges(harness.Arc(XInputStick.Right, 30, 180, 10, userIndex: 0)).Count == 0,
            "User 0 right stick must keep separate half-turn progress.");
        Assert(Edges(harness.Arc(XInputStick.Left, 60, 180, 10, userIndex: 1)).Count == 0,
            "User 1 must keep separate half-turn progress.");

        AssertSingleEdge(
            harness.Arc(XInputStick.Left, 180, 180, 10, userIndex: 0),
            0,
            XInputStick.Left,
            XInputStickRotationDirection.CounterClockwise);
        AssertSingleEdge(
            harness.Arc(XInputStick.Right, 210, 180, 10, userIndex: 0),
            0,
            XInputStick.Right,
            XInputStickRotationDirection.CounterClockwise);
        AssertSingleEdge(
            harness.Arc(XInputStick.Left, 240, 180, 10, userIndex: 1),
            1,
            XInputStick.Left,
            XInputStickRotationDirection.CounterClockwise);
        return Task.CompletedTask;
    }

    public static Task DisconnectClearsCalibrationProgressAndSequenceAsync()
    {
        var harness = new StickHarness();
        var partial = harness.Arc(
            XInputStick.Left,
            startDegrees: 10,
            travelDegrees: 270,
            stepDegrees: 10);
        Assert(Edges(partial).Count == 0, "A partial turn must not emit an edge before disconnect.");

        var disconnected = harness.DisconnectFrame(userIndex: 0);
        Assert(!disconnected.Left.IsConnected && !disconnected.Right.IsConnected,
            "A disconnect frame must mark both sticks disconnected.");
        Assert(disconnected.Left.GestureProgressDegrees == 0 && disconnected.Left.GestureDirection is null,
            "Disconnect must clear retained rotation progress and direction.");
        Assert(disconnected.Left.CenterDriftRadius == 0,
            "Disconnect must clear learned center calibration.");

        var remainingQuarter = harness.Arc(
            XInputStick.Left,
            startDegrees: 280,
            travelDegrees: 90,
            stepDegrees: 10);
        Assert(Edges(remainingQuarter).Count == 0,
            "Travel remaining from the pre-disconnect gesture must not complete after reconnect.");

        harness.DisconnectFrame(userIndex: 0);
        var freshTurn = harness.Arc(
            XInputStick.Left,
            startDegrees: 91,
            travelDegrees: 360,
            stepDegrees: 10);
        var edge = Edges(freshTurn).Single();
        Assert(edge.SequenceNumber == 1,
            "Disconnect must reset the per-stick completion sequence with the rest of its state.");
        return Task.CompletedTask;
    }

    private static IReadOnlyList<XInputStickRotationEdge> Edges(
        IEnumerable<XInputStickAnalysis> samples) =>
        samples
            .Where(sample => sample.RotationEdge.HasValue)
            .Select(sample => sample.RotationEdge!.Value)
            .ToArray();

    private static void AssertSingleEdge(
        IEnumerable<XInputStickAnalysis> samples,
        int userIndex,
        XInputStick stick,
        XInputStickRotationDirection direction)
    {
        var materialized = samples.ToArray();
        var edges = Edges(materialized);
        Assert(edges.Count == 1,
            $"Exactly one rotation edge was expected; observed {edges.Count}, " +
            $"final progress {materialized[^1].GestureProgressDegrees:F3} degrees.");
        var edge = edges[0];
        Assert(edge.UserIndex == userIndex, "The rotation edge must retain its XInput user slot.");
        Assert(edge.Stick == stick, "The rotation edge must retain its originating stick.");
        Assert(edge.Direction == direction, "The rotation edge direction is incorrect.");
    }

    private static void AssertNear(
        double expected,
        double? actual,
        double tolerance,
        string message)
    {
        if (!actual.HasValue || Math.Abs(expected - actual.Value) > tolerance)
        {
            throw new InvalidOperationException(
                $"{message} Expected {expected:F3}, actual " +
                (actual.HasValue ? $"{actual.Value:F3}." : "null."));
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StickHarness
    {
        private static readonly DateTimeOffset Epoch = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
        private long _sampleIndex;
        private uint _packetNumber;

        public XInputStickAnalyzer Analyzer { get; } = new();

        public XInputStickAnalysis Sample(
            XInputStick stick,
            short x,
            short y,
            int userIndex = 0,
            bool isConnected = true) =>
            Analyzer.Process(new XInputStickRawSample(
                userIndex,
                stick,
                isConnected,
                ++_packetNumber,
                x,
                y,
                Epoch.AddMilliseconds(_sampleIndex++)));

        public IReadOnlyList<XInputStickAnalysis> Arc(
            XInputStick stick,
            double startDegrees,
            double travelDegrees,
            double stepDegrees,
            int radius = 30_000,
            short centerX = 0,
            short centerY = 0,
            int userIndex = 0)
        {
            if (stepDegrees <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stepDegrees));
            }

            var stepCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(travelDegrees) / stepDegrees));
            var samples = new List<XInputStickAnalysis>(stepCount + 1);
            for (var index = 0; index <= stepCount; index++)
            {
                var angle = startDegrees + (travelDegrees * index / stepCount);
                var radians = angle * Math.PI / 180.0;
                var x = checked((short)Math.Round(centerX + (radius * Math.Cos(radians))));
                var y = checked((short)Math.Round(centerY + (radius * Math.Sin(radians))));
                samples.Add(Sample(stick, x, y, userIndex));
            }

            return samples;
        }

        public XInputStickAnalysisFrame DisconnectFrame(int userIndex) =>
            Analyzer.Process(new XInputRawStateChangedEventArgs(
                userIndex,
                isConnected: false,
                ++_packetNumber,
                buttons: 0,
                leftTrigger: 0,
                rightTrigger: 0,
                thumbLX: 0,
                thumbLY: 0,
                thumbRX: 0,
                thumbRY: 0,
                Epoch.AddMilliseconds(_sampleIndex++)));
    }
}
