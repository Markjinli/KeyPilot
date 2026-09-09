using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Serialization;
using KeyPilot.Core.Triggers;
using KeyPilot.Core.Validation;

internal static class InputPatternTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
        new (string Name, Action Run)[]
        {
            ("输入模式：重叠按键自动归类为无序组合", CaptureClassifiesOverlapAsChord),
            ("输入模式：非重叠边沿记录为有序序列", CaptureRecordsOrderedSequence),
            ("输入模式：计时器可在没有新事件时完成", TimerCompletesWithoutNewEvent),
            ("输入模式：重复、注入和游离释放不计入录制", CaptureIgnoresNoise),
            ("输入模式：超过三十二个边沿会拒绝", CaptureRejectsOverflow),
            ("输入模式：JSON 往返保留边沿与时间", JsonRoundTripPreservesSequence),
            ("输入模式：schema 2 自动迁移到当前版本", SchemaTwoMigratesToCurrent),
            ("输入模式：验证器接受平衡序列", ValidatorAcceptsBalancedSequence),
            ("输入模式：验证器拒绝不平衡和重复边沿", ValidatorRejectsInvalidEdges),
            ("输入模式：最终边沿形成一次逻辑点击", SequenceFiresOnceOnFinalEdge),
            ("输入模式：合理时间偏差仍可匹配", SequenceAcceptsTimingTolerance),
            ("输入模式：超出时间偏差不会匹配", SequenceRejectsTimingOutsideTolerance),
            ("输入模式：无关边沿重置并允许立即重新开始", UnrelatedEdgeSafelyRestarts),
            ("输入模式：精确设备匹配保持隔离", ExactDeviceSequenceIsIsolated),
            ("输入模式：注入事件不推进也不打断", InjectedEventsAreIgnored),
            ("输入模式：Reset 清除部分匹配", ResetClearsPartialSequence),
            ("输入模式：两次完整序列支持 DoublePress", SequenceSupportsDoublePress),
            ("输入模式：旧 ChordMembers 仍按原语义工作", LegacyChordStillMatches)
        };

    private static void CaptureClassifiesOverlapAsChord()
    {
        var session = new InputPatternCaptureSession();
        session.Arm(Epoch);
        session.Observe(Event(Key(0x1D, "macro"), InputEventPhase.Pressed, 100));
        session.Observe(Event(Key(0x02, "macro"), InputEventPhase.Pressed, 200));
        session.Observe(Event(Key(0x02, "macro"), InputEventPhase.Released, 300));
        session.Observe(Event(Key(0x1D, "macro"), InputEventPhase.Released, 400));

        AssertEqual(InputPatternCaptureState.Completed, session.TryComplete(Epoch.AddSeconds(2)), "completion state");
        var source = RequireCompleted(session);
        AssertEqual(InputControlKind.InputChord, source.Control.Kind, "captured kind");
        AssertEqual(2, source.ChordMembers?.Count ?? 0, "chord member count");
        AssertEqual(4, session.RecordedEdgeCount, "recorded edge count");
    }

    private static void CaptureRecordsOrderedSequence()
    {
        var session = RecordSequentialPattern();
        var source = RequireCompleted(session);
        var steps = source.PatternSteps
            ?? throw new InvalidOperationException("Sequence steps are missing.");

        AssertEqual(InputControlKind.InputSequence, source.Control.Kind, "captured kind");
        AssertSequenceEqual(new[] { 0, 100, 400, 550 }, steps.Select(step => step.OffsetMilliseconds));
        AssertSequenceEqual(
            new[]
            {
                InputEventPhase.Pressed,
                InputEventPhase.Released,
                InputEventPhase.Pressed,
                InputEventPhase.Released
            },
            steps.Select(step => step.Phase));
    }

    private static void TimerCompletesWithoutNewEvent()
    {
        var session = new InputPatternCaptureSession();
        session.Arm(Epoch);
        AssertEqual(Epoch.AddSeconds(2), session.DeadlineUtc, "deadline");
        session.Observe(Event(Key(0x1E), InputEventPhase.Pressed, 100));
        session.Observe(Event(Key(0x1E), InputEventPhase.Released, 200));

        AssertEqual(InputPatternCaptureState.Armed, session.TryComplete(Epoch.AddMilliseconds(1_999)), "before deadline");
        AssertEqual(InputPatternCaptureState.Completed, session.TryComplete(Epoch.AddSeconds(2)), "timer completion");
        AssertEqual<DateTimeOffset?>(null, session.DeadlineUtc, "deadline after completion");
    }

    private static void CaptureIgnoresNoise()
    {
        var session = new InputPatternCaptureSession();
        session.Arm(Epoch);
        session.Observe(Event(Key(0x30), InputEventPhase.Released, 10));
        session.Observe(Event(Key(0x2E), InputEventPhase.Pressed, 20, injected: true));
        session.Observe(Event(Key(0x1E), InputEventPhase.Pressed, 100));
        session.Observe(Event(Key(0x1E), InputEventPhase.Repeated, 150));
        session.Observe(Event(Key(0x1E), InputEventPhase.Released, 200));

        AssertEqual(2, session.RecordedEdgeCount, "noise-free edge count");
        AssertEqual(InputPatternCaptureState.Completed, session.TryComplete(Epoch.AddSeconds(2)), "completion state");
    }

    private static void CaptureRejectsOverflow()
    {
        var session = new InputPatternCaptureSession();
        session.Arm(Epoch);
        var time = 10;
        for (var index = 0; index < 17; index++)
        {
            session.Observe(Event(Key(0x1E), InputEventPhase.Pressed, time));
            session.Observe(Event(Key(0x1E), InputEventPhase.Released, time + 10));
            time += 30;
        }

        AssertEqual(InputPatternCaptureSession.MaximumEdgeCount, session.RecordedEdgeCount, "edge cap");
        AssertEqual(InputPatternCaptureState.Rejected, session.TryComplete(Epoch.AddSeconds(2)), "overflow result");
        AssertEqual<InputSource?>(null, session.CompletedSource, "overflow source");
    }

    private static void JsonRoundTripPreservesSequence()
    {
        var source = Sequence(
            Step(Key(0x1E, "macro"), InputEventPhase.Pressed, 0),
            Step(Key(0x1E, "macro"), InputEventPhase.Released, 90),
            Step(Key(0x30, "macro"), InputEventPhase.Pressed, 320),
            Step(Key(0x30, "macro"), InputEventPhase.Released, 430));
        var configuration = Configuration(source);

        var json = KeyPilotJson.Serialize(configuration);
        var copy = KeyPilotJson.Deserialize(json);
        var restored = copy.Profiles.Single().Mappings.Single().Source;

        Assert(json.Contains("\"patternSteps\"", StringComparison.Ordinal), "serialized steps");
        AssertEqual(source.CanonicalKey, restored.CanonicalKey, "round-trip identity");
        AssertEqual(4, restored.PatternSteps?.Count ?? 0, "round-trip count");
        AssertEqual(430, restored.PatternSteps?[^1].OffsetMilliseconds, "round-trip final offset");
    }

    private static void SchemaTwoMigratesToCurrent()
    {
        var configuration = KeyPilotConfiguration.CreateDefault("schema 2");
        var current = KeyPilotJson.Serialize(configuration);
        var schemaTwo = current.Replace(
            $"\"schemaVersion\": {KeyPilotConfiguration.CurrentSchemaVersion}",
            "\"schemaVersion\": 2",
            StringComparison.Ordinal);

        var migrated = KeyPilotJson.Deserialize(schemaTwo);
        AssertEqual(KeyPilotConfiguration.CurrentSchemaVersion, migrated.SchemaVersion, "migrated schema");
    }

    private static void ValidatorAcceptsBalancedSequence()
    {
        AssertNoErrors(ConfigurationValidator.Validate(Configuration(DefaultSequence())));
    }

    private static void ValidatorRejectsInvalidEdges()
    {
        var invalid = Sequence(
            Step(Key(0x1E), InputEventPhase.Pressed, 0),
            Step(Key(0x1E), InputEventPhase.Pressed, 100),
            Step(Key(0x30), InputEventPhase.Released, 200));
        var issues = ConfigurationValidator.Validate(Configuration(invalid));

        AssertIssue(issues, "source.sequence.edge.duplicatePress");
        AssertIssue(issues, "source.sequence.edge.unmatchedRelease");
        AssertIssue(issues, "source.sequence.edge.unreleased");
    }

    private static void SequenceFiresOnceOnFinalEdge()
    {
        var machine = Machine(DefaultSequence());
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Pressed, 0)), "A down");
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Released, 100)), "A up");
        AssertEmpty(machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Pressed, 300)), "B down");

        var final = machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Released, 400));
        AssertEqual(1, final.Count, "logical click count");
        AssertEqual(InputEventPhase.Released, final[0].OriginatingEvent.Phase, "logical click phase");
        AssertEmpty(machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Released, 410)), "duplicate final edge");
    }

    private static void SequenceAcceptsTimingTolerance()
    {
        var machine = Machine(DefaultSequence());
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Pressed, 0)), "start");
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Released, 220)), "A up with tolerance");
        AssertEmpty(machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Pressed, 430)), "B down with tolerance");
        AssertEqual(1, machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Released, 540)).Count, "tolerated completion");
    }

    private static void SequenceRejectsTimingOutsideTolerance()
    {
        var machine = Machine(DefaultSequence());
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Pressed, 0)), "start");
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Released, 251)), "late A up");
        AssertEmpty(machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Pressed, 300)), "B down after reset");
        AssertEmpty(machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Released, 400)), "B up after reset");
    }

    private static void UnrelatedEdgeSafelyRestarts()
    {
        var machine = Machine(DefaultSequence());
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Pressed, 0)), "old start");
        AssertEmpty(machine.Process(Event(Key(0x2E, "pad"), InputEventPhase.Pressed, 50)), "unrelated reset");

        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Pressed, 100)), "new start");
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Released, 200)), "new A up");
        AssertEmpty(machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Pressed, 400)), "new B down");
        AssertEqual(1, machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Released, 500)).Count, "new completion");
    }

    private static void ExactDeviceSequenceIsIsolated()
    {
        var source = Sequence(
            Step(Key(0x1E, "macro"), InputEventPhase.Pressed, 0),
            Step(Key(0x1E, "macro"), InputEventPhase.Released, 100),
            Step(Key(0x30, "macro"), InputEventPhase.Pressed, 300),
            Step(Key(0x30, "macro"), InputEventPhase.Released, 400));
        var machine = Machine(source);

        Play(machine, "normal", 0, expectedActivations: 0);
        Play(machine, "macro", 1_000, expectedActivations: 1);
    }

    private static void InjectedEventsAreIgnored()
    {
        var machine = Machine(DefaultSequence());
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Pressed, 0)), "start");
        AssertEmpty(machine.Process(Event(Key(0x2E, "pad"), InputEventPhase.Pressed, 50, injected: true)), "injected noise");
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Released, 100)), "A up");
        AssertEmpty(machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Pressed, 300)), "B down");
        AssertEqual(1, machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Released, 400)).Count, "completion");
    }

    private static void ResetClearsPartialSequence()
    {
        var machine = Machine(DefaultSequence());
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Pressed, 0)), "partial start");
        machine.Reset();
        AssertEmpty(machine.Process(Event(Key(0x1E, "pad"), InputEventPhase.Released, 100)), "orphan A up");
        AssertEmpty(machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Pressed, 300)), "orphan B down");
        AssertEmpty(machine.Process(Event(Key(0x30, "pad"), InputEventPhase.Released, 400)), "orphan B up");
        Play(machine, "pad", 1_000, expectedActivations: 1);
    }

    private static void SequenceSupportsDoublePress()
    {
        var machine = Machine(DefaultSequence(), MappingTriggerKind.DoublePress);
        Play(machine, "pad", 0, expectedActivations: 0);
        Play(machine, "pad", 500, expectedActivations: 1);
    }

    private static void LegacyChordStillMatches()
    {
        var chord = new InputSource
        {
            Device = CompositeDevice(),
            Control = new InputControlId { Kind = InputControlKind.InputChord, Code = 1 },
            ChordMembers = new List<InputSource> { Key(0x1D), Key(0x02) }
        };
        var machine = Machine(chord);
        AssertEmpty(machine.Process(Event(Key(0x1D, "pad"), InputEventPhase.Pressed, 0)), "Ctrl down");
        AssertEmpty(machine.Process(Event(Key(0x02, "pad"), InputEventPhase.Pressed, 20)), "1 down");
        AssertEqual(1, machine.Process(Event(Key(0x02, "pad"), InputEventPhase.Released, 30)).Count, "legacy chord activation");
    }

    private static InputPatternCaptureSession RecordSequentialPattern()
    {
        var session = new InputPatternCaptureSession();
        session.Arm(Epoch);
        session.Observe(Event(Key(0x1E, "macro"), InputEventPhase.Pressed, 100));
        session.Observe(Event(Key(0x1E, "macro"), InputEventPhase.Released, 200));
        session.Observe(Event(Key(0x30, "macro"), InputEventPhase.Pressed, 500));
        session.Observe(Event(Key(0x30, "macro"), InputEventPhase.Released, 650));
        AssertEqual(InputPatternCaptureState.Completed, session.TryComplete(Epoch.AddSeconds(2)), "completion state");
        return session;
    }

    private static InputSource RequireCompleted(InputPatternCaptureSession session) =>
        session.CompletedSource ?? throw new InvalidOperationException("Completed pattern is missing.");

    private static InputSource DefaultSequence() => Sequence(
        Step(Key(0x1E), InputEventPhase.Pressed, 0),
        Step(Key(0x1E), InputEventPhase.Released, 100),
        Step(Key(0x30), InputEventPhase.Pressed, 300),
        Step(Key(0x30), InputEventPhase.Released, 400));

    private static InputSource Sequence(params InputPatternStep[] steps) => new()
    {
        Device = CompositeDevice(),
        Control = new InputControlId { Kind = InputControlKind.InputSequence, Code = 1 },
        PatternSteps = steps.ToList()
    };

    private static InputPatternStep Step(
        InputSource source,
        InputEventPhase phase,
        int offsetMilliseconds) => new()
        {
            Source = source,
            Phase = phase,
            OffsetMilliseconds = offsetMilliseconds
        };

    private static InputDeviceSelector CompositeDevice() => new()
    {
        Kind = InputDeviceKind.Composite,
        MatchMode = DeviceMatchMode.AnyOfKind
    };

    private static InputSource Key(int scanCode, string? exactDeviceId = null) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Keyboard,
            MatchMode = exactDeviceId is null ? DeviceMatchMode.AnyOfKind : DeviceMatchMode.ExactDevice,
            DeviceId = exactDeviceId,
            VendorId = exactDeviceId is null ? null : (ushort)0x1234,
            ProductId = exactDeviceId is null ? null : (ushort)0x5678
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.KeyboardScanCode,
            Code = scanCode
        }
    };

    private static InputEvent Event(
        InputSource source,
        InputEventPhase phase,
        int milliseconds,
        bool injected = false) => new()
        {
            Source = source,
            Phase = phase,
            TimestampUtc = Epoch.AddMilliseconds(milliseconds),
            SequenceNumber = milliseconds + 1L,
            IsInjected = injected
        };

    private static MappingTriggerStateMachine Machine(
        InputSource source,
        MappingTriggerKind triggerKind = MappingTriggerKind.SinglePress) =>
        new(new[]
        {
            new InputMapping
            {
                Name = "pattern",
                SuppressOriginal = false,
                Source = source,
                Trigger = new MappingTrigger
                {
                    Kind = triggerKind,
                    DoublePressWindowMilliseconds = 1_000
                },
                Action = new OpenUriAction { Uri = "https://example.com/" }
            }
        });

    private static KeyPilotConfiguration Configuration(InputSource source)
    {
        var configuration = KeyPilotConfiguration.CreateDefault("pattern");
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "pattern",
            SuppressOriginal = false,
            Source = source,
            Action = new OpenUriAction { Uri = "https://example.com/" }
        });
        return configuration;
    }

    private static void Play(
        MappingTriggerStateMachine machine,
        string deviceId,
        int startMilliseconds,
        int expectedActivations)
    {
        AssertEmpty(machine.Process(Event(Key(0x1E, deviceId), InputEventPhase.Pressed, startMilliseconds)), "play A down");
        AssertEmpty(machine.Process(Event(Key(0x1E, deviceId), InputEventPhase.Released, startMilliseconds + 100)), "play A up");
        AssertEmpty(machine.Process(Event(Key(0x30, deviceId), InputEventPhase.Pressed, startMilliseconds + 300)), "play B down");
        var final = machine.Process(Event(Key(0x30, deviceId), InputEventPhase.Released, startMilliseconds + 400));
        AssertEqual(expectedActivations, final.Count, "play activation count");
    }

    private static void AssertIssue(IReadOnlyList<ValidationIssue> issues, string code) =>
        Assert(
            issues.Any(issue => issue.Code == code && issue.Severity == ValidationSeverity.Error),
            $"Expected validation issue {code}.");

    private static void AssertNoErrors(IReadOnlyList<ValidationIssue> issues) =>
        Assert(
            !issues.Any(issue => issue.Severity == ValidationSeverity.Error),
            "Unexpected validation errors: " + string.Join(", ", issues.Select(issue => issue.Code)));

    private static void AssertEmpty<T>(IReadOnlyCollection<T> values, string description) =>
        AssertEqual(0, values.Count, description);

    private static void AssertSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{description}: expected {expected}, actual {actual}.");
        }
    }
}
