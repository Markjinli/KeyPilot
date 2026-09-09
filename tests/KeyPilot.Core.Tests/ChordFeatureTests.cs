using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Serialization;
using KeyPilot.Core.Triggers;
using KeyPilot.Core.Validation;

internal static class ChordFeatureTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const int LeftControlScanCode = 0x1D;
    private const int OneScanCode = 0x02;

    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
        new (string Name, Action Run)[]
        {
            ("组合键：规范标识与成员顺序无关", CanonicalKeyIgnoresMemberOrder),
            ("组合键：JSON 往返保留映射和槽位", JsonRoundTripPreservesChord),
            ("组合键：schema 1 配置自动迁移到当前版本", SchemaOneConfigurationMigrates),
            ("组合键：验证器接受两个不同成员", ValidatorAcceptsTwoMembers),
            ("组合键：验证器拒绝重复成员", ValidatorRejectsDuplicateMembers),
            ("组合键：验证器拒绝嵌套组合", ValidatorRejectsNestedChord),
            ("组合键：验证器拒绝屏蔽逻辑组合输入", ValidatorRejectsSuppressOriginal),
            ("组合键录制：采集 Ctrl+1 并在全释放后完成", CaptureRecordsControlOne),
            ("组合键录制：忽略重复和注入事件", CaptureIgnoresRepeatAndInjectedEvents),
            ("组合键录制：十秒后超时", CaptureExpiresAtDeadline),
            ("组合键触发：正反按键顺序均可识别", TriggerAcceptsEitherPressOrder),
            ("组合键触发：250ms 边界成功", TriggerAcceptsTwoHundredFiftyMillisecondBoundary),
            ("组合键触发：251ms 超时失败", TriggerRejectsTwoHundredFiftyOneMilliseconds),
            ("组合键触发：任一成员释放仅单击一次", SinglePressFiresOnceOnFirstMemberRelease),
            ("组合键触发：全部释放后可以再次触发", ChordRearmsOnlyAfterAllMembersRelease),
            ("组合键触发：精确设备与普通键盘隔离", ExactDeviceMembersAreIsolated),
            ("组合键触发：Reset 清除未完成状态", ResetClearsPartialChord),
            ("组合键触发：KeyDown 与 KeyUp 保持边沿语义", KeyDownAndKeyUpUseLogicalChordEdges),
            ("组合键触发：LongPress 从组合完成时计时", LongPressUsesChordCompletionTime),
            ("组合键触发：DoublePress 识别两次完整组合", DoublePressRecognizesTwoChordClicks)
        };

    private static void CanonicalKeyIgnoresMemberOrder()
    {
        var control = Keyboard(LeftControlScanCode, "macro-pad");
        var one = Keyboard(OneScanCode, "macro-pad");

        var forward = Chord(control, one);
        var reverse = Chord(one, control);

        AssertEqual(forward.CanonicalKey, reverse.CanonicalKey, "canonical chord identity");
    }

    private static void JsonRoundTripPreservesChord()
    {
        var chord = Chord(
            Keyboard(LeftControlScanCode, "macro-pad"),
            Keyboard(OneScanCode, "macro-pad"));
        var configuration = ConfigurationWithChord(chord);
        configuration.SpecialKeySlots[0] = configuration.SpecialKeySlots[0] with
        {
            DisplayName = "Ctrl+1",
            Source = chord
        };

        var json = KeyPilotJson.Serialize(configuration);
        var copy = KeyPilotJson.Deserialize(json);
        var mappingSource = copy.Profiles.Single().Mappings.Single().Source;
        var slotSource = copy.SpecialKeySlots[0].Source
            ?? throw new InvalidOperationException("Chord slot source was not restored.");

        Assert(json.Contains("\"chordMembers\"", StringComparison.Ordinal), "serialized chord members");
        AssertEqual(InputDeviceKind.Composite, mappingSource.Device.Kind, "mapping device kind");
        AssertEqual(InputControlKind.InputChord, mappingSource.Control.Kind, "mapping control kind");
        AssertEqual(2, mappingSource.ChordMembers?.Count ?? 0, "mapping member count");
        AssertEqual(chord.CanonicalKey, mappingSource.CanonicalKey, "mapping chord identity");
        AssertEqual(chord.CanonicalKey, slotSource.CanonicalKey, "slot chord identity");
    }

    private static void SchemaOneConfigurationMigrates()
    {
        var configuration = KeyPilotConfiguration.CreateDefault("Legacy");
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "Legacy A",
            SuppressOriginal = false,
            Source = Keyboard(0x1E),
            Action = new OpenUriAction { Uri = "https://example.com/" }
        });
        var currentJson = KeyPilotJson.Serialize(configuration);
        var legacyJson = currentJson.Replace(
            $"\"schemaVersion\": {KeyPilotConfiguration.CurrentSchemaVersion}",
            "\"schemaVersion\": 1",
            StringComparison.Ordinal);

        var migrated = KeyPilotJson.Deserialize(legacyJson);
        var savedAgain = KeyPilotJson.Serialize(migrated);

        AssertEqual(KeyPilotConfiguration.CurrentSchemaVersion, migrated.SchemaVersion, "migrated schema");
        AssertEqual(1, migrated.Profiles.Single().Mappings.Count, "legacy mapping count");
        Assert(savedAgain.Contains(
            $"\"schemaVersion\": {KeyPilotConfiguration.CurrentSchemaVersion}",
            StringComparison.Ordinal), "resaved current schema");
    }

    private static void ValidatorAcceptsTwoMembers()
    {
        var configuration = ConfigurationWithChord(Chord(
            Keyboard(LeftControlScanCode),
            Keyboard(OneScanCode)));

        AssertNoErrors(ConfigurationValidator.Validate(configuration));
    }

    private static void ValidatorRejectsDuplicateMembers()
    {
        var control = Keyboard(LeftControlScanCode);
        var configuration = ConfigurationWithChord(Chord(control, control));

        AssertIssue(
            ConfigurationValidator.Validate(configuration),
            "source.chord.member.duplicate");
    }

    private static void ValidatorRejectsNestedChord()
    {
        var nested = Chord(Keyboard(LeftControlScanCode), Keyboard(OneScanCode));
        var outer = Chord(Keyboard(0x2A), nested);
        var configuration = ConfigurationWithChord(outer);

        AssertIssue(ConfigurationValidator.Validate(configuration), "source.chord.nested");
    }

    private static void ValidatorRejectsSuppressOriginal()
    {
        var configuration = ConfigurationWithChord(
            Chord(Keyboard(LeftControlScanCode), Keyboard(OneScanCode)),
            suppressOriginal: true);

        AssertIssue(
            ConfigurationValidator.Validate(configuration),
            "mapping.suppression.unsupportedLogicalSource");
    }

    private static void CaptureRecordsControlOne()
    {
        var session = new InputChordCaptureSession();
        var control = Keyboard(LeftControlScanCode, "macro-pad");
        var one = Keyboard(OneScanCode, "macro-pad");
        session.Arm(Epoch);

        AssertEqual(InputChordCaptureState.Armed, session.Observe(Event(control, InputEventPhase.Pressed, 0)), "Ctrl down");
        AssertEqual(InputChordCaptureState.Armed, session.Observe(Event(one, InputEventPhase.Pressed, 40)), "1 down");
        AssertEqual(InputChordCaptureState.Armed, session.Observe(Event(one, InputEventPhase.Released, 60)), "1 up while Ctrl held");
        AssertEqual(InputChordCaptureState.Completed, session.Observe(Event(control, InputEventPhase.Released, 80)), "Ctrl up");

        var source = session.CompletedSource
            ?? throw new InvalidOperationException("Recorded chord is missing.");
        AssertEqual(InputControlKind.InputChord, source.Control.Kind, "captured control kind");
        AssertEqual(2, source.ChordMembers?.Count ?? 0, "captured member count");
        AssertEqual(Chord(control, one).CanonicalKey, source.CanonicalKey, "captured identity");
    }

    private static void CaptureIgnoresRepeatAndInjectedEvents()
    {
        var session = new InputChordCaptureSession();
        var control = Keyboard(LeftControlScanCode, "macro-pad");
        var one = Keyboard(OneScanCode, "macro-pad");
        var alt = Keyboard(0x38, "macro-pad");
        session.Arm(Epoch);

        session.Observe(Event(alt, InputEventPhase.Pressed, 0, injected: true));
        session.Observe(Event(control, InputEventPhase.Pressed, 10));
        session.Observe(Event(control, InputEventPhase.Repeated, 20));
        session.Observe(Event(alt, InputEventPhase.Repeated, 30));
        session.Observe(Event(one, InputEventPhase.Pressed, 40));
        session.Observe(Event(alt, InputEventPhase.Released, 50, injected: true));
        session.Observe(Event(control, InputEventPhase.Released, 60));
        session.Observe(Event(one, InputEventPhase.Released, 70));

        var source = session.CompletedSource
            ?? throw new InvalidOperationException("Recorded chord is missing.");
        AssertEqual(2, source.ChordMembers?.Count ?? 0, "member count after ignored events");
        AssertEqual(Chord(control, one).CanonicalKey, source.CanonicalKey, "ignored-event identity");
    }

    private static void CaptureExpiresAtDeadline()
    {
        var session = new InputChordCaptureSession();
        session.Arm(Epoch);
        session.Observe(Event(Keyboard(LeftControlScanCode), InputEventPhase.Pressed, 0));

        Assert(session.TryExpire(Epoch + InputChordCaptureSession.CaptureTimeout), "deadline should expire");
        AssertEqual(InputChordCaptureState.Expired, session.State, "expired state");
        AssertEqual<InputSource?>(null, session.CompletedSource, "expired result");
        AssertEqual(
            InputChordCaptureState.Expired,
            session.Observe(Event(Keyboard(OneScanCode), InputEventPhase.Pressed, 10_001)),
            "events after expiry");
    }

    private static void TriggerAcceptsEitherPressOrder()
    {
        var chord = Chord(Keyboard(LeftControlScanCode), Keyboard(OneScanCode));

        var forward = new MappingTriggerStateMachine(new[] { Mapping(chord) });
        AssertEmpty(forward.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Pressed, 0)), "forward first down");
        AssertEmpty(forward.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Pressed, 20)), "forward second down");
        AssertEqual(1, forward.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 30)).Count, "forward activation");

        var reverse = new MappingTriggerStateMachine(new[] { Mapping(chord) });
        AssertEmpty(reverse.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Pressed, 0)), "reverse first down");
        AssertEmpty(reverse.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Pressed, 20)), "reverse second down");
        AssertEqual(1, reverse.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 30)).Count, "reverse activation");
    }

    private static void TriggerAcceptsTwoHundredFiftyMillisecondBoundary()
    {
        var machine = Machine(MappingTriggerKind.SinglePress);
        AssertEmpty(machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Pressed, 0)), "first down");
        AssertEmpty(machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Pressed, 250)), "second down at boundary");
        AssertEqual(1, machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 251)).Count, "boundary activation");
    }

    private static void TriggerRejectsTwoHundredFiftyOneMilliseconds()
    {
        var machine = Machine(MappingTriggerKind.SinglePress);
        AssertEmpty(machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Pressed, 0)), "first down");
        AssertEmpty(machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Pressed, 251)), "late second down");
        AssertEmpty(machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 260)), "late second up");
        AssertEmpty(machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 270)), "first up");
    }

    private static void SinglePressFiresOnceOnFirstMemberRelease()
    {
        var machine = Machine(MappingTriggerKind.SinglePress);
        PressChord(machine, 0, 20);

        var firstRelease = machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 30));
        var secondRelease = machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 40));

        AssertEqual(1, firstRelease.Count, "first release activation count");
        AssertEmpty(secondRelease, "second release");
        AssertEqual(InputEventPhase.Released, firstRelease[0].OriginatingEvent.Phase, "logical release phase");
    }

    private static void ChordRearmsOnlyAfterAllMembersRelease()
    {
        var machine = Machine(MappingTriggerKind.SinglePress);
        PressChord(machine, 0, 10);
        AssertEqual(1, machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 20)).Count, "first activation");

        AssertEmpty(machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Pressed, 30)), "repress before all up");
        AssertEmpty(machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 40)), "blocked repress up");
        AssertEmpty(machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 50)), "last original member up");

        PressChord(machine, 100, 110);
        AssertEqual(1, machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 120)).Count, "second activation");
    }

    private static void ExactDeviceMembersAreIsolated()
    {
        var exactChord = Chord(
            Keyboard(LeftControlScanCode, "macro-pad"),
            Keyboard(OneScanCode, "macro-pad"));
        var machine = new MappingTriggerStateMachine(new[] { Mapping(exactChord) });

        PressChord(machine, 0, 10, "normal-keyboard");
        AssertEmpty(machine.Process(Event(Keyboard(LeftControlScanCode, "normal-keyboard"), InputEventPhase.Released, 20)), "normal Ctrl up");
        AssertEmpty(machine.Process(Event(Keyboard(OneScanCode, "normal-keyboard"), InputEventPhase.Released, 30)), "normal 1 up");

        PressChord(machine, 100, 110, "macro-pad");
        AssertEqual(1, machine.Process(Event(Keyboard(OneScanCode, "macro-pad"), InputEventPhase.Released, 120)).Count, "exact device activation");
    }

    private static void ResetClearsPartialChord()
    {
        var machine = Machine(MappingTriggerKind.SinglePress);
        AssertEmpty(machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Pressed, 0)), "partial down");
        machine.Reset();

        AssertEmpty(machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Pressed, 10)), "old partial cannot complete");
        AssertEmpty(machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 20)), "orphan release");
        PressChord(machine, 30, 40);
        AssertEqual(1, machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 50)).Count, "fresh activation");
    }

    private static void KeyDownAndKeyUpUseLogicalChordEdges()
    {
        var keyDown = Machine(MappingTriggerKind.KeyDown);
        AssertEmpty(keyDown.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Pressed, 0)), "KeyDown first member");
        AssertEqual(1, keyDown.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Pressed, 10)).Count, "KeyDown chord completion");
        AssertEmpty(keyDown.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 20)), "KeyDown release");

        var keyUp = Machine(MappingTriggerKind.KeyUp);
        PressChord(keyUp, 0, 10);
        AssertEqual(1, keyUp.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 20)).Count, "KeyUp first release");
        AssertEmpty(keyUp.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 30)), "KeyUp second release");
    }

    private static void LongPressUsesChordCompletionTime()
    {
        var machine = Machine(MappingTriggerKind.LongPress, longPressMilliseconds: 600);
        PressChord(machine, 0, 20);

        AssertEmpty(machine.AdvanceTo(Epoch.AddMilliseconds(619)), "before long threshold");
        var activations = machine.AdvanceTo(Epoch.AddMilliseconds(620));
        AssertEqual(1, activations.Count, "long activation count");
        AssertEqual(Epoch.AddMilliseconds(620), activations[0].TriggeredAtUtc, "long activation time");
        AssertEmpty(machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 700)), "release after long press");
    }

    private static void DoublePressRecognizesTwoChordClicks()
    {
        var machine = Machine(MappingTriggerKind.DoublePress, doublePressWindowMilliseconds: 350);

        PressChord(machine, 0, 10);
        AssertEmpty(machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 20)), "first click up");
        AssertEmpty(machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 30)), "first click final up");

        PressChord(machine, 100, 110);
        var activations = machine.Process(Event(Keyboard(OneScanCode, "pad"), InputEventPhase.Released, 120));
        AssertEqual(1, activations.Count, "double activation count");
        AssertEmpty(machine.Process(Event(Keyboard(LeftControlScanCode, "pad"), InputEventPhase.Released, 130)), "second click final up");
    }

    private static KeyPilotConfiguration ConfigurationWithChord(
        InputSource chord,
        bool suppressOriginal = false)
    {
        var configuration = KeyPilotConfiguration.CreateDefault("Chord tests");
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = "Ctrl+1",
            SuppressOriginal = suppressOriginal,
            Source = chord,
            Action = new OpenUriAction { Uri = "https://example.com/" }
        });
        return configuration;
    }

    private static MappingTriggerStateMachine Machine(
        MappingTriggerKind kind,
        int longPressMilliseconds = 600,
        int doublePressWindowMilliseconds = 350) =>
        new(new[]
        {
            Mapping(
                Chord(Keyboard(LeftControlScanCode), Keyboard(OneScanCode)),
                kind,
                longPressMilliseconds,
                doublePressWindowMilliseconds)
        });

    private static InputMapping Mapping(
        InputSource chord,
        MappingTriggerKind kind = MappingTriggerKind.SinglePress,
        int longPressMilliseconds = 600,
        int doublePressWindowMilliseconds = 350) =>
        new()
        {
            Name = kind.ToString(),
            SuppressOriginal = false,
            Source = chord,
            Trigger = new MappingTrigger
            {
                Kind = kind,
                LongPressMilliseconds = longPressMilliseconds,
                DoublePressWindowMilliseconds = doublePressWindowMilliseconds
            },
            Action = new OpenUriAction { Uri = "https://example.com/" }
        };

    private static InputSource Chord(params InputSource[] members) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Composite,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.InputChord,
            Code = 1
        },
        ChordMembers = members.ToList()
    };

    private static InputSource Keyboard(int scanCode, string? exactDeviceId = null) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Keyboard,
            MatchMode = exactDeviceId is null
                ? DeviceMatchMode.AnyOfKind
                : DeviceMatchMode.ExactDevice,
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
        bool injected = false) =>
        new()
        {
            Source = source,
            Phase = phase,
            TimestampUtc = Epoch.AddMilliseconds(milliseconds),
            SequenceNumber = milliseconds + 1L,
            IsInjected = injected
        };

    private static void PressChord(
        MappingTriggerStateMachine machine,
        int firstAtMilliseconds,
        int secondAtMilliseconds,
        string deviceId = "pad")
    {
        AssertEmpty(
            machine.Process(Event(
                Keyboard(LeftControlScanCode, deviceId),
                InputEventPhase.Pressed,
                firstAtMilliseconds)),
            "Ctrl down");
        AssertEmpty(
            machine.Process(Event(
                Keyboard(OneScanCode, deviceId),
                InputEventPhase.Pressed,
                secondAtMilliseconds)),
            "1 down");
    }

    private static void AssertIssue(IReadOnlyList<ValidationIssue> issues, string expectedCode) =>
        Assert(
            issues.Any(issue => issue.Code == expectedCode && issue.Severity == ValidationSeverity.Error),
            $"Expected validation issue {expectedCode}.");

    private static void AssertNoErrors(IReadOnlyList<ValidationIssue> issues) =>
        Assert(
            !issues.Any(issue => issue.Severity == ValidationSeverity.Error),
            "Configuration contains unexpected validation errors: " +
            string.Join(", ", issues.Where(issue => issue.Severity == ValidationSeverity.Error).Select(issue => issue.Code)));

    private static void AssertEmpty<T>(IReadOnlyCollection<T> items, string description) =>
        AssertEqual(0, items.Count, description);

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
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}.");
        }
    }
}
