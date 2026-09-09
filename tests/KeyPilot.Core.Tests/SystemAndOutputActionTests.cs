using KeyPilot.Core.Actions;
using KeyPilot.Core.Input;

internal static class SystemAndOutputActionTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Logical output codec strips physical identity", CodecStripsPhysicalIdentity),
        ("Logical output codec is versioned and strict", CodecIsVersionedAndStrict),
        ("Copied keyboard chords plan safe reverse release", CopiedKeyboardChordPlansInOrder),
        ("Copied F13 through F24 remain injectable virtual keys", FunctionKeysF13ThroughF24RemainInjectable),
        ("Copied gamepad output plans injection; HID still explains missing backend", UnsupportedOutputFamiliesAreExplicit),
        ("Media and volume actions produce semantic operations", MediaAndVolumeActionsPlanSemantically),
        ("Exact volume planning enforces zero through one hundred", ExactVolumeIsBounded),
        ("Legacy special-key action emits captured keyboard chords", LegacySpecialKeyEmitsKeyboardChord)
    ];

    private static void CodecStripsPhysicalIdentity()
    {
        var source = new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Keyboard,
                MatchMode = DeviceMatchMode.ExactDevice,
                DeviceId = @"\\?\HID#VID_1234&PID_5678#SERIAL",
                VendorId = 0x1234,
                ProductId = 0x5678
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.KeyboardScanCode,
                Code = 0x6D,
                IsExtended = true,
                RawQualifier = "PREFIX=0002;DEVICEBYTE=99",
                ActivationQualifier = "ACTIVE=01",
                ReportId = 3,
                DataIndex = 7,
                LinkCollection = 2
            }
        };

        var target = LogicalOutputTargetCodec.FromInputSource(source);
        var encoded = LogicalOutputTargetCodec.Encode(target);
        var copy = LogicalOutputTargetCodec.Decode(encoded);
        var control = copy.Controls.Single();

        Assert(encoded.StartsWith(LogicalOutputTargetCodec.Prefix, StringComparison.Ordinal),
            "The output token must carry an explicit codec version.");
        Assert(copy.Version == LogicalOutputTarget.CurrentVersion,
            "The decoded target version must remain explicit.");
        Assert(copy.Family == LogicalOutputFamily.Keyboard,
            "A keyboard scan code must remain a logical keyboard output.");
        Assert(control.Code == 0x6D && control.Kind == InputControlKind.KeyboardScanCode,
            "The copied F22 scan code must remain intact.");
        Assert(control.RawQualifier == "PREFIX=0002",
            "Only the logical E0 prefix may survive a physical keyboard qualifier.");
        Assert(control.ActivationQualifier is null
            && control.ReportId is null
            && control.DataIndex is null
            && control.LinkCollection is null,
            "Report position and activation identity must be stripped from output targets.");
        Assert(!encoded.Contains("VID_1234", StringComparison.OrdinalIgnoreCase)
            && !encoded.Contains("SERIAL", StringComparison.OrdinalIgnoreCase),
            "The output token must not contain a physical DeviceSelector.");
    }

    private static void CodecIsVersionedAndStrict()
    {
        Assert(!LogicalOutputTargetCodec.TryDecode(
                "KPOT2.invalid",
                out _,
                out var versionError)
            && versionError.Contains("version", StringComparison.OrdinalIgnoreCase),
            "An unknown codec prefix must return a version capability error.");
        Assert(!LogicalOutputTargetCodec.TryDecode(
                LogicalOutputTargetCodec.Prefix + "not+base64",
                out _,
                out var payloadError)
            && payloadError.Contains("base64url", StringComparison.OrdinalIgnoreCase),
            "Unsafe base64 characters must be rejected without throwing from TryDecode.");

        var unsafeTarget = new LogicalOutputTarget
        {
            Family = LogicalOutputFamily.Keyboard,
            Controls =
            {
                new InputControlId
                {
                    Kind = InputControlKind.VirtualKey,
                    Code = 0x41,
                    ActivationQualifier = "physical-only"
                }
            }
        };
        AssertThrows<LogicalOutputTargetCodecException>(
            () => LogicalOutputTargetCodec.Encode(unsafeTarget),
            "The encoder must reject physical input fields even on a caller-created model.");
    }

    private static void CopiedKeyboardChordPlansInOrder()
    {
        var chord = Chord(
            KeyboardSource(InputControlKind.VirtualKey, 0xA2),
            KeyboardSource(InputControlKind.KeyboardScanCode, 0x02),
            KeyboardSource(InputControlKind.VirtualKey, 0x10));
        Assert(LogicalOutputTargetCodec.TryFromInputSource(chord, out var target, out var error),
            $"A keyboard-only chord must be copyable: {error}");
        var plan = new MappingActionPlanner().Plan(new CopyInputToOutputAction
        {
            EncodedTarget = LogicalOutputTargetCodec.Encode(target!),
            HoldMilliseconds = 42
        });

        Assert(plan.Operations.Count == 7, "Three chord members require 3 down, delay, and 3 up operations.");
        AssertInjection(plan.Operations[0], InputInjectionPhase.Down, 0xA2);
        AssertInjection(plan.Operations[1], InputInjectionPhase.Down, 0x02);
        AssertInjection(plan.Operations[2], InputInjectionPhase.Down, 0x10);
        Assert(plan.Operations[3] is DelayOperation { Milliseconds: 42 },
            "The copied chord hold must remain explicit.");
        AssertInjection(plan.Operations[4], InputInjectionPhase.Up, 0x10);
        AssertInjection(plan.Operations[5], InputInjectionPhase.Up, 0x02);
        AssertInjection(plan.Operations[6], InputInjectionPhase.Up, 0xA2);
        Assert(plan.Operations.OfType<InjectInputOperation>()
                .All(operation => operation.Target is ControlInjectionTarget),
            "Copied output must contain logical controls, never CapturedInputInjectionTarget identities.");
    }

    private static void FunctionKeysF13ThroughF24RemainInjectable()
    {
        for (var virtualKey = 0x7C; virtualKey <= 0x87; virtualKey++)
        {
            var source = KeyboardSource(InputControlKind.VirtualKey, virtualKey);
            var target = LogicalOutputTargetCodec.FromInputSource(source);
            var plan = new MappingActionPlanner().Plan(new CopyInputToOutputAction
            {
                EncodedTarget = LogicalOutputTargetCodec.Encode(target)
            });
            AssertInjection(plan.Operations[0], InputInjectionPhase.Down, virtualKey);
            AssertInjection(plan.Operations[2], InputInjectionPhase.Up, virtualKey);
        }
    }

    private static void UnsupportedOutputFamiliesAreExplicit()
    {
        var gamepad = new InputSource
        {
            Device = new InputDeviceSelector { Kind = InputDeviceKind.Gamepad },
            Control = new InputControlId
            {
                Kind = InputControlKind.GamepadButton,
                Code = 0x1000
            }
        };
        var hid = new InputSource
        {
            Device = new InputDeviceSelector { Kind = InputDeviceKind.Hid },
            Control = new InputControlId
            {
                Kind = InputControlKind.HidUsage,
                Code = 1,
                UsagePage = 0x0C,
                Usage = 0x00E9
            }
        };

        var gamepadPlan = new MappingActionPlanner().Plan(new CopyInputToOutputAction
        {
            EncodedTarget = LogicalOutputTargetCodec.Encode(
                LogicalOutputTargetCodec.FromInputSource(gamepad))
        });
        Assert(gamepadPlan.Operations.Count == 3,
            "Gamepad logical output must expand to down, hold, and up.");
        AssertInjection(gamepadPlan.Operations[0], InputInjectionPhase.Down, 0x1000);
        Assert(gamepadPlan.Operations[1] is DelayOperation, "Gamepad output must hold before release.");
        AssertInjection(gamepadPlan.Operations[2], InputInjectionPhase.Up, 0x1000);
        AssertPlanningError(
            LogicalOutputTargetCodec.FromInputSource(hid),
            "virtual HID backend");

        Assert(!LogicalOutputTargetCodec.TryFromInputSource(
                new InputSource
                {
                    Device = new InputDeviceSelector { Kind = InputDeviceKind.Hid },
                    Control = new InputControlId
                    {
                        Kind = InputControlKind.RawCode,
                        Code = 7,
                        RawQualifier = "OFFSET=2;MASK=01"
                    }
                },
                out _,
                out var rawError)
            && rawError.Contains("physical-device", StringComparison.OrdinalIgnoreCase),
            "Vendor raw reports must not be mislabeled as logical output controls.");
    }

    private static void MediaAndVolumeActionsPlanSemantically()
    {
        foreach (var command in Enum.GetValues<MediaControlCommand>())
        {
            var operation = new MappingActionPlanner()
                .Plan(new MediaControlAction { Command = command })
                .Operations
                .Single() as MediaControlOperation;
            Assert(operation?.Command == command,
                "Each media action must retain its semantic command in the plan.");
        }

        foreach (var command in new[]
                 {
                     VolumeControlCommand.Increase,
                     VolumeControlCommand.Decrease,
                     VolumeControlCommand.ToggleMute
                 })
        {
            var operation = new MappingActionPlanner()
                .Plan(new VolumeControlAction { Command = command })
                .Operations
                .Single() as VolumeControlOperation;
            Assert(operation?.Command == command && operation.LevelPercent is null,
                "Relative volume actions must not invent an exact level.");
        }
    }

    private static void ExactVolumeIsBounded()
    {
        foreach (var level in new[] { 0, 37, 100 })
        {
            var operation = new MappingActionPlanner().Plan(new VolumeControlAction
            {
                Command = VolumeControlCommand.SetLevelPercent,
                LevelPercent = level
            }).Operations.Single() as VolumeControlOperation;
            Assert(operation?.LevelPercent == level,
                "Exact volume planning must retain every accepted boundary value.");
        }

        foreach (var level in new int?[] { null, -1, 101 })
        {
            AssertThrows<ActionPlanningException>(
                () => new MappingActionPlanner().Plan(new VolumeControlAction
                {
                    Command = VolumeControlCommand.SetLevelPercent,
                    LevelPercent = level
                }),
                "Missing and out-of-range exact volume values must fail during planning.");
        }
    }

    private static void LegacySpecialKeyEmitsKeyboardChord()
    {
        var slots = SpecialKeySlots.CreateDefaults();
        slots[0] = slots[0] with
        {
            Source = Chord(
                KeyboardSource(InputControlKind.VirtualKey, 0xA2),
                KeyboardSource(InputControlKind.VirtualKey, 0x31))
        };
        var plan = new MappingActionPlanner().Plan(
            new EmitSpecialKeyAction { SlotNumber = 1 },
            slots);

        Assert(plan.Operations.Count == 5,
            "A legacy special-key action must expand a captured two-key chord.");
        AssertInjection(plan.Operations[0], InputInjectionPhase.Down, 0xA2);
        AssertInjection(plan.Operations[1], InputInjectionPhase.Down, 0x31);
        Assert(plan.Operations[2] is DelayOperation
            {
                Milliseconds: MappingActionPlanner.DefaultSpecialKeyHoldMilliseconds
            },
            "Legacy chord emission must use the established special-key hold.");
        AssertInjection(plan.Operations[3], InputInjectionPhase.Up, 0x31);
        AssertInjection(plan.Operations[4], InputInjectionPhase.Up, 0xA2);
    }

    private static void AssertPlanningError(LogicalOutputTarget target, string expectedText)
    {
        try
        {
            _ = new MappingActionPlanner().Plan(new CopyInputToOutputAction
            {
                EncodedTarget = LogicalOutputTargetCodec.Encode(target)
            });
            throw new InvalidOperationException("An unsupported output family was planned.");
        }
        catch (ActionPlanningException exception)
        {
            Assert(exception.Message.Contains(expectedText, StringComparison.OrdinalIgnoreCase),
                $"Planning error must explain the missing capability: {exception.Message}");
        }
    }

    private static InputSource KeyboardSource(InputControlKind kind, int code) => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Keyboard,
            MatchMode = DeviceMatchMode.ExactDevice,
            DeviceId = $"keyboard-{code:X}"
        },
        Control = new InputControlId { Kind = kind, Code = code }
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

    private static void AssertInjection(
        ActionPlanOperation operation,
        InputInjectionPhase phase,
        int code)
    {
        Assert(operation is InjectInputOperation
            {
                Phase: var actualPhase,
                Target: ControlInjectionTarget { Control.Code: var actualCode }
            }
            && actualPhase == phase
            && actualCode == code,
            $"Expected {phase} for 0x{code:X}.");
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
