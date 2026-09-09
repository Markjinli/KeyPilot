using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.Core.Serialization;
using KeyPilot.Core.Validation;

var tests = new (string Name, Action Run)[]
{
    ("默认配置包含十个槽位", DefaultConfigurationHasTenSlots),
    ("输入标识区分扩展扫描码", CanonicalInputIdentityDistinguishesExtendedKeys),
    ("HID 标识保留报告与集合限定", CanonicalHidIdentityPreservesReportQualifiers),
    ("JSON 往返保留多态动作", JsonRoundTripPreservesPolymorphicActions),
    ("JSON 拒绝缺失的配置版本", JsonRejectsMissingSchemaVersion),
    ("JSON 拒绝未知字段", JsonRejectsUnknownFields),
    ("验证器拒绝未来配置版本", ValidatorRejectsFutureSchemaVersion),
    ("验证器拒绝重复输入源", ValidatorRejectsDuplicateInputSources),
    ("验证器拒绝未绑定特殊键", ValidatorRejectsUnboundSpecialKey),
    ("验证器拒绝无效长按阈值", ValidatorRejectsInvalidLongPressThreshold),
    ("验证器接受 XInput 高位按钮掩码", ValidatorAcceptsXInputButtonMasks),
    ("验证器拒绝超范围手柄按钮编码", ValidatorRejectsOutOfRangeGamepadButton),
    ("模拟手柄输入可以 JSON 往返", GamepadAxisDirectionRoundTrips),
    ("验证器拒绝超范围模拟手柄编码", ValidatorRejectsOutOfRangeGamepadAxisDirection),
    ("验证器接受自定义 URI 协议", ValidatorAcceptsCustomUriScheme),
    ("动作规划器展开按键并反序释放快捷键", PlannerExpandsKeyOperationsInOrder),
    ("动作规划器解析已采集特殊键", PlannerResolvesCapturedSpecialKey),
    ("动作规划器拒绝未绑定特殊键", PlannerRejectsUnboundSpecialKey)
}.Concat(AppearanceThemeTests.All)
    .Concat(StickMouseSettingsTests.All)
    .Concat(ApplicationProfileTests.All)
    .Concat(MappingConditionTests.All)
    .Concat(TriggerStateMachineTests.All)
    .Concat(MouseInputIdentityTests.All)
    .Concat(ChordFeatureTests.All)
    .Concat(SystemAndOutputActionTests.All)
    .Concat(InputPatternTests.All)
    .Concat(InputProcessLogTests.All)
    .ToArray();

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void DefaultConfigurationHasTenSlots()
{
    var configuration = KeyPilotConfiguration.CreateDefault();
    AssertEqual(10, configuration.SpecialKeySlots.Count, "slot count");
    AssertSequenceEqual(Enumerable.Range(1, 10), configuration.SpecialKeySlots.Select(slot => slot.Number));
    AssertNoErrors(ConfigurationValidator.Validate(configuration));
}

static void CanonicalInputIdentityDistinguishesExtendedKeys()
{
    var normal = KeyboardScanCode(0x1C);
    var extended = normal with { IsExtended = true };
    Assert(normal.CanonicalKey != extended.CanonicalKey, "扩展扫描码必须具有不同的规范标识。");
}

static void CanonicalHidIdentityPreservesReportQualifiers()
{
    var control = new InputControlId
    {
        Kind = InputControlKind.HidUsage,
        Code = 7,
        UsagePage = 0xFF59,
        Usage = 0x0061,
        ReportId = 2,
        DataIndex = 11,
        LinkCollection = 3,
        RawQualifier = "OFFSET=4;MASK=08;BASE=00;ACTIVE=08"
    };

    Assert(control.CanonicalKey != (control with { ReportId = 3 }).CanonicalKey, "Report ID 必须参与标识。");
    Assert(control.CanonicalKey != (control with { DataIndex = 12 }).CanonicalKey, "DataIndex 必须参与标识。");
    Assert(control.CanonicalKey != (control with { LinkCollection = 4 }).CanonicalKey, "LinkCollection 必须参与标识。");
    Assert(control.CanonicalKey != (control with { RawQualifier = "OFFSET=5" }).CanonicalKey, "原始限定信息必须参与标识。");
}

static void JsonRoundTripPreservesPolymorphicActions()
{
    var configuration = CreateSampleConfiguration();
    var json = KeyPilotJson.Serialize(configuration);
    var copy = KeyPilotJson.Deserialize(json);

    Assert(json.Contains("\"$action\": \"macro\"", StringComparison.Ordinal), "JSON 缺少宏动作判别字段。");
    AssertEqual(configuration.ActiveProfileId, copy.ActiveProfileId, "active profile");

    var macro = copy.Profiles.Single().Mappings.Single().Action as MacroAction
        ?? throw new InvalidOperationException("动作类型未恢复为 MacroAction。");
    AssertEqual(3, macro.Steps.Count, "macro step count");
    Assert(macro.Steps[0] is ShortcutAction, "第一个宏步骤类型错误。");
    Assert(macro.Steps[1] is DelayAction, "第二个宏步骤类型错误。");
    Assert(macro.Steps[2] is EmitSpecialKeyAction, "第三个宏步骤类型错误。");
}

static void JsonRejectsMissingSchemaVersion()
{
    const string json = """
        {
          "activeProfileId": null,
          "isMappingEnabled": false,
          "profiles": [],
          "specialKeySlots": []
        }
        """;
    AssertThrows<System.Text.Json.JsonException>(
        () => KeyPilotJson.Deserialize(json),
        "缺少 schemaVersion 的配置不应被当作当前版本。" );
}

static void JsonRejectsUnknownFields()
{
    var json = KeyPilotJson.Serialize(KeyPilotConfiguration.CreateDefault());
    json = json.Insert(json.LastIndexOf('}'), ",\n  \"futureField\": true\n");
    AssertThrows<System.Text.Json.JsonException>(
        () => KeyPilotJson.Deserialize(json),
        "未知字段不应在下一次保存时被静默擦除。" );
}

static void ValidatorRejectsFutureSchemaVersion()
{
    var configuration = KeyPilotConfiguration.CreateDefault() with
    {
        SchemaVersion = KeyPilotConfiguration.CurrentSchemaVersion + 1
    };
    var issues = ConfigurationValidator.Validate(configuration);
    Assert(
        issues.Any(issue => issue.Code == "schema.unsupported" && issue.Severity == ValidationSeverity.Error),
        "验证器未拒绝未来配置版本。" );
}

static void ValidatorRejectsDuplicateInputSources()
{
    var configuration = CreateSampleConfiguration();
    var original = configuration.Profiles.Single().Mappings.Single();
    configuration.Profiles.Single().Mappings.Add(new InputMapping
    {
        Name = "重复来源",
        Source = original.Source,
        Action = new SendKeyAction { Target = KeyboardScanCode(0x20) }
    });

    var issues = ConfigurationValidator.Validate(configuration);
    Assert(
        issues.Any(issue => issue.Code == "mapping.source.duplicate" && issue.Severity == ValidationSeverity.Error),
        "验证器未报告重复输入源。");
}

static void ValidatorRejectsUnboundSpecialKey()
{
    var configuration = KeyPilotConfiguration.CreateDefault();
    configuration.Profiles.Single().Mappings.Add(new InputMapping
    {
        Name = "未绑定槽位",
        Source = KeyboardSource(0x1E),
        Action = new EmitSpecialKeyAction { SlotNumber = 1 }
    });

    var issues = ConfigurationValidator.Validate(configuration);
    Assert(
        issues.Any(issue => issue.Code == "specialKey.slot.unbound" && issue.Severity == ValidationSeverity.Error),
        "验证器未报告未绑定特殊键槽位。");
}

static void ValidatorRejectsInvalidLongPressThreshold()
{
    var configuration = KeyPilotConfiguration.CreateDefault();
    configuration.Profiles.Single().Mappings.Add(new InputMapping
    {
        Name = "无效长按",
        Source = KeyboardSource(0x1E),
        Trigger = new MappingTrigger
        {
            Kind = MappingTriggerKind.LongPress,
            LongPressMilliseconds = 20
        },
        Action = new SendKeyAction { Target = KeyboardScanCode(0x20) }
    });

    var issues = ConfigurationValidator.Validate(configuration);
    Assert(
        issues.Any(issue => issue.Code == "trigger.longPress.range" && issue.Severity == ValidationSeverity.Error),
        "验证器未报告无效长按阈值。");
}

static void ValidatorAcceptsXInputButtonMasks()
{
    var configuration = KeyPilotConfiguration.CreateDefault();
    foreach (var code in new[] { 0x1000, 0x8000 })
    {
        configuration.Profiles.Single().Mappings.Add(new InputMapping
        {
            Name = $"手柄 0x{code:X4} → 网址",
            SuppressOriginal = false,
            Source = GamepadSource(code),
            Action = new OpenUriAction { Uri = "https://example.com/" }
        });
    }

    AssertNoErrors(ConfigurationValidator.Validate(configuration));
    _ = KeyPilotJson.Serialize(configuration);
}

static void ValidatorRejectsOutOfRangeGamepadButton()
{
    var configuration = KeyPilotConfiguration.CreateDefault();
    configuration.Profiles.Single().Mappings.Add(new InputMapping
    {
        Name = "无效手柄按钮",
        SuppressOriginal = false,
        Source = GamepadSource(0x10000),
        Action = new OpenUriAction { Uri = "https://example.com/" }
    });

    var issues = ConfigurationValidator.Validate(configuration);
    Assert(
        issues.Any(issue => issue.Code == "control.gamepadButton.range" &&
            issue.Severity == ValidationSeverity.Error),
        "验证器未拒绝超出 ushort 范围的手柄按钮编码。");
}

static void GamepadAxisDirectionRoundTrips()
{
    var configuration = KeyPilotConfiguration.CreateDefault();
    configuration.Profiles.Single().Mappings.Add(new InputMapping
    {
        Name = "LT → 网址",
        SuppressOriginal = false,
        Source = GamepadAxisSource(1),
        Action = new OpenUriAction { Uri = "https://example.com/" }
    });

    var json = KeyPilotJson.Serialize(configuration);
    var copy = KeyPilotJson.Deserialize(json);
    var source = copy.Profiles.Single().Mappings.Single().Source;
    AssertEqual(InputControlKind.GamepadAxisDirection, source.Control.Kind, "axis control kind");
    AssertEqual(1, source.Control.Code, "axis control code");
}

static void ValidatorRejectsOutOfRangeGamepadAxisDirection()
{
    var configuration = KeyPilotConfiguration.CreateDefault();
    configuration.Profiles.Single().Mappings.Add(new InputMapping
    {
        Name = "无效模拟手柄输入",
        SuppressOriginal = false,
        Source = GamepadAxisSource(11),
        Action = new OpenUriAction { Uri = "https://example.com/" }
    });

    var issues = ConfigurationValidator.Validate(configuration);
    Assert(
        issues.Any(issue => issue.Code == "control.gamepadAxisDirection.range" &&
            issue.Severity == ValidationSeverity.Error),
        "验证器未拒绝超出定义范围的模拟手柄输入编码。");
}

static void ValidatorAcceptsCustomUriScheme()
{
    var configuration = KeyPilotConfiguration.CreateDefault();
    configuration.Profiles.Single().Mappings.Add(new InputMapping
    {
        Name = "打开 Steam 游戏库",
        Source = KeyboardSource(0x1F),
        Action = new OpenUriAction { Uri = "steam://open/games" }
    });

    AssertNoErrors(ConfigurationValidator.Validate(configuration));
}

static void PlannerExpandsKeyOperationsInOrder()
{
    var first = KeyboardScanCode(0x1D);
    var second = KeyboardScanCode(0x38);
    var plan = new MappingActionPlanner().Plan(new MacroAction
    {
        Steps =
        {
            new SendKeyAction
            {
                Target = KeyboardScanCode(0x39),
                Transition = KeyTransition.Press,
                HoldMilliseconds = 25
            },
            new ShortcutAction
            {
                Keys = { first, second },
                HoldMilliseconds = 40
            }
        }
    });

    AssertEqual(8, plan.Operations.Count, "expanded operation count");
    AssertInjection(plan.Operations[0], InputInjectionPhase.Down, 0x39);
    AssertDelay(plan.Operations[1], 25);
    AssertInjection(plan.Operations[2], InputInjectionPhase.Up, 0x39);
    AssertInjection(plan.Operations[3], InputInjectionPhase.Down, first.Code);
    AssertInjection(plan.Operations[4], InputInjectionPhase.Down, second.Code);
    AssertDelay(plan.Operations[5], 40);
    AssertInjection(plan.Operations[6], InputInjectionPhase.Up, second.Code);
    AssertInjection(plan.Operations[7], InputInjectionPhase.Up, first.Code);
}

static void PlannerResolvesCapturedSpecialKey()
{
    var configuration = CreateSampleConfiguration();
    var plan = new MappingActionPlanner().Plan(
        new EmitSpecialKeyAction { SlotNumber = 1 },
        configuration.SpecialKeySlots);

    AssertEqual(3, plan.Operations.Count, "special-key operation count");
    var down = plan.Operations[0] as InjectInputOperation
        ?? throw new InvalidOperationException("特殊键第一步应为注入按下。");
    var target = down.Target as CapturedInputInjectionTarget
        ?? throw new InvalidOperationException("特殊键应保留完整采集来源。");
    AssertEqual(InputInjectionPhase.Down, down.Phase, "special-key down phase");
    AssertEqual(
        configuration.SpecialKeySlots[0].Source!.CanonicalKey,
        target.Source.CanonicalKey,
        "captured special-key identity");
    AssertDelay(plan.Operations[1], MappingActionPlanner.DefaultSpecialKeyHoldMilliseconds);
    Assert(
        plan.Operations[2] is InjectInputOperation { Phase: InputInjectionPhase.Up },
        "特殊键最后一步应为注入释放。");
}

static void PlannerRejectsUnboundSpecialKey()
{
    var configuration = KeyPilotConfiguration.CreateDefault();
    try
    {
        _ = new MappingActionPlanner().Plan(
            new EmitSpecialKeyAction { SlotNumber = 1 },
            configuration.SpecialKeySlots);
        throw new InvalidOperationException("未绑定特殊键应导致规划失败。");
    }
    catch (ActionPlanningException exception)
    {
        AssertEqual("action.slotNumber", exception.ActionPath, "planning failure path");
    }
}

static KeyPilotConfiguration CreateSampleConfiguration()
{
    var specialSource = new InputSource
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Hid,
            MatchMode = DeviceMatchMode.ExactDevice,
            VendorId = 0x1234,
            ProductId = 0x5678
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.HidUsage,
            UsagePage = 0xFF00,
            Usage = 0x0001,
            Code = 7
        }
    };

    var configuration = KeyPilotConfiguration.CreateDefault("掌机");
    configuration.SpecialKeySlots[0] = configuration.SpecialKeySlots[0] with
    {
        DisplayName = "厂商快捷键",
        Source = specialSource
    };

    configuration.Profiles.Single().Mappings.Add(new InputMapping
    {
        Name = "A → 宏",
        Source = KeyboardSource(0x1E),
        Trigger = new MappingTrigger
        {
            Kind = MappingTriggerKind.LongPress,
            LongPressMilliseconds = 600
        },
        SuppressOriginal = true,
        Action = new MacroAction
        {
            Steps =
            {
                new ShortcutAction
                {
                    Keys =
                    {
                        KeyboardScanCode(0x1D),
                        KeyboardScanCode(0x38),
                        KeyboardScanCode(0x0D)
                    }
                },
                new DelayAction { Milliseconds = 80 },
                new EmitSpecialKeyAction { SlotNumber = 1 }
            }
        }
    });

    return configuration;
}

static InputSource KeyboardSource(int scanCode) => new()
{
    Device = new InputDeviceSelector
    {
        Kind = InputDeviceKind.Keyboard,
        MatchMode = DeviceMatchMode.AnyOfKind
    },
    Control = KeyboardScanCode(scanCode)
};

static InputSource GamepadSource(int code) => new()
{
    Device = new InputDeviceSelector
    {
        Kind = InputDeviceKind.Gamepad,
        MatchMode = DeviceMatchMode.AnyOfKind
    },
    Control = new InputControlId
    {
        Kind = InputControlKind.GamepadButton,
        Code = code
    }
};

static InputSource GamepadAxisSource(int code) => new()
{
    Device = new InputDeviceSelector
    {
        Kind = InputDeviceKind.Gamepad,
        MatchMode = DeviceMatchMode.AnyOfKind
    },
    Control = new InputControlId
    {
        Kind = InputControlKind.GamepadAxisDirection,
        Code = code
    }
};

static InputControlId KeyboardScanCode(int scanCode) => new()
{
    Kind = InputControlKind.KeyboardScanCode,
    Code = scanCode
};

static void AssertInjection(
    ActionPlanOperation operation,
    InputInjectionPhase expectedPhase,
    int expectedCode)
{
    var injection = operation as InjectInputOperation
        ?? throw new InvalidOperationException("Expected an input injection operation.");
    var target = injection.Target as ControlInjectionTarget
        ?? throw new InvalidOperationException("Expected a control injection target.");
    AssertEqual(expectedPhase, injection.Phase, "injection phase");
    AssertEqual(expectedCode, target.Control.Code, "injection control code");
}

static void AssertDelay(ActionPlanOperation operation, int expectedMilliseconds)
{
    var delay = operation as DelayOperation
        ?? throw new InvalidOperationException("Expected a delay operation.");
    AssertEqual(expectedMilliseconds, delay.Milliseconds, "delay milliseconds");
}

static void AssertNoErrors(IReadOnlyList<ValidationIssue> issues) =>
    Assert(!issues.Any(issue => issue.Severity == ValidationSeverity.Error), "配置包含意外的验证错误。");

static void AssertThrows<TException>(Action action, string message)
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

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string description)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{description}: expected {expected}, actual {actual}.");
    }
}

static void AssertSequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException("Sequences are not equal.");
    }
}
