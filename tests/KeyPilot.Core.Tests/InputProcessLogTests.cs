using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Diagnostics;
using KeyPilot.Core.Input;

internal static class InputProcessLogTests
{
    public static IReadOnlyList<(string Name, Action Run)> All { get; } =
        new (string Name, Action Run)[]
        {
            ("过程日志：映射关闭时记为仅采集", MappingOffIsCaptureOnly),
            ("过程日志：无匹配映射时记为放行", UnmappedIsPassThrough),
            ("过程日志：键盘拦截成功记为拦截并改写", KeyboardSuppressedIsIntercept),
            ("过程日志：键盘无法真正拦截时拒绝映射", KeyboardUnsuppressedRefuses),
            ("过程日志：键盘保留原键记为叠加", KeyboardKeepOriginalOverlays),
            ("过程日志：鼠标中键叠加无法拦截", MouseOverlayCannotIntercept),
            ("过程日志：鼠标中键若要求拦截则拒绝执行", MouseSuppressRequestIsRefused),
            ("过程日志：注入回声不入日志", InjectedEchoIsIgnored),
            ("过程日志：重复按键默认折叠", RepeatsCollapseOntoPress),
            ("过程日志：近距重复边沿去重", DuplicateEdgesAreDeduped)
        };

    private static void MappingOffIsCaptureOnly()
    {
        var configuration = Config(mappingEnabled: false, KeyboardMapping(suppressOriginal: true));
        var disposition = InputProcessLogClassifier.Classify(configuration, KeyboardSource(), originalWasSuppressed: false);
        AssertEqual(InputProcessDisposition.CaptureOnly, disposition, "disposition");
        AssertEqual("仅采集", InputProcessLogClassifier.DispositionLabel(disposition), "label");
    }

    private static void UnmappedIsPassThrough()
    {
        var configuration = Config(mappingEnabled: true);
        var disposition = InputProcessLogClassifier.Classify(configuration, KeyboardSource(), originalWasSuppressed: false);
        AssertEqual(InputProcessDisposition.PassThrough, disposition, "disposition");
    }

    private static void KeyboardSuppressedIsIntercept()
    {
        var configuration = Config(mappingEnabled: true, KeyboardMapping(suppressOriginal: true, name: "F1 → Ctrl+C"));
        var entry = InputProcessLogClassifier.CreateEntry(
            Event(KeyboardSource(), InputEventPhase.Pressed),
            "F1",
            configuration,
            originalWasSuppressed: true,
            new DateTimeOffset(2026, 9, 10, 14, 32, 1, TimeSpan.FromHours(8)));
        AssertEqual(InputProcessDisposition.InterceptAndRewrite, entry.Disposition, "disposition");
        AssertEqual("拦截并改写", entry.DispositionLabel, "label");
        AssertEqual("Ctrl+C", entry.ActionSummary, "action");
        AssertEqual("成功", entry.ResultLabel, "result");
        AssertEqual("键盘", entry.DeviceLabel, "device");
    }

    private static void KeyboardUnsuppressedRefuses()
    {
        var configuration = Config(mappingEnabled: true, KeyboardMapping(suppressOriginal: true));
        var disposition = InputProcessLogClassifier.Classify(
            configuration,
            KeyboardSource(),
            originalWasSuppressed: false);
        AssertEqual(InputProcessDisposition.CannotInterceptRefused, disposition, "disposition");
        AssertEqual("无法拦截，未改写", InputProcessLogClassifier.DispositionLabel(disposition), "label");
    }

    private static void KeyboardKeepOriginalOverlays()
    {
        var configuration = Config(mappingEnabled: true, KeyboardMapping(suppressOriginal: false, name: "A → B"));
        var disposition = InputProcessLogClassifier.Classify(
            configuration,
            KeyboardSource(),
            originalWasSuppressed: false);
        AssertEqual(InputProcessDisposition.KeepOriginalOverlay, disposition, "disposition");
        AssertEqual("原键放行并叠加", InputProcessLogClassifier.DispositionLabel(disposition), "label");
    }

    private static void MouseOverlayCannotIntercept()
    {
        var configuration = Config(mappingEnabled: true, MouseMapping(suppressOriginal: false, name: "中键 → Win+V"));
        var entry = InputProcessLogClassifier.CreateEntry(
            Event(MouseSource(), InputEventPhase.Pressed),
            "中键",
            configuration,
            originalWasSuppressed: false,
            DateTimeOffset.Now);
        AssertEqual(InputProcessDisposition.CannotInterceptOverlay, entry.Disposition, "disposition");
        AssertEqual("无法拦截，已叠加", entry.DispositionLabel, "label");
        AssertEqual("Win+V", entry.ActionSummary, "action");
        AssertEqual("已叠加", entry.ResultLabel, "result");
        AssertEqual("鼠标", entry.DeviceLabel, "device");
        Assert(!InputProcessLogClassifier.CanSuppressOriginal(MouseSource()), "mouse cannot suppress");
    }

    private static void MouseSuppressRequestIsRefused()
    {
        var configuration = Config(mappingEnabled: true, MouseMapping(suppressOriginal: true));
        var disposition = InputProcessLogClassifier.Classify(
            configuration,
            MouseSource(),
            originalWasSuppressed: false);
        AssertEqual(InputProcessDisposition.CannotInterceptRefused, disposition, "disposition");
    }

    private static void InjectedEchoIsIgnored()
    {
        var log = new InputProcessLog();
        var injected = Event(KeyboardSource(), InputEventPhase.Pressed) with { IsInjected = true };
        var recorded = log.TryRecord(
            injected,
            "F1",
            Config(mappingEnabled: true, KeyboardMapping(true)),
            originalWasSuppressed: false,
            DateTimeOffset.Now);
        Assert(recorded is null, "injected events must not be logged");
        AssertEqual(0, log.Count, "count");
    }

    private static void RepeatsCollapseOntoPress()
    {
        var log = new InputProcessLog();
        var configuration = Config(mappingEnabled: false);
        var now = DateTimeOffset.Now;
        var source = KeyboardSource();
        Assert(log.TryRecord(Event(source, InputEventPhase.Pressed), "A", configuration, false, now) is not null, "press");
        var collapsed = log.TryRecord(
            Event(source, InputEventPhase.Repeated),
            "A",
            configuration,
            false,
            now.AddMilliseconds(80));
        Assert(collapsed is not null, "repeat recorded");
        AssertEqual(1, log.Count, "still one row");
        AssertEqual(2, collapsed!.RepeatCount, "repeat count");
        AssertEqual(" ×2", InputProcessLogClassifier.FormatRepeatSuffix(collapsed.RepeatCount), "suffix");
    }

    private static void DuplicateEdgesAreDeduped()
    {
        var log = new InputProcessLog();
        var configuration = Config(mappingEnabled: false);
        var now = DateTimeOffset.Now;
        var source = KeyboardSource();
        Assert(log.TryRecord(Event(source, InputEventPhase.Pressed), "A", configuration, false, now) is not null, "first");
        var dup = log.TryRecord(
            Event(source, InputEventPhase.Pressed),
            "A",
            configuration,
            false,
            now.AddMilliseconds(10));
        Assert(dup is null, "duplicate press within 40ms is ignored");
        AssertEqual(1, log.Count, "count");
    }

    private static KeyPilotConfiguration Config(bool mappingEnabled, params InputMapping[] mappings)
    {
        var profile = new MappingProfile
        {
            Name = "test",
            Mappings = mappings.ToList()
        };
        return new KeyPilotConfiguration
        {
            ActiveProfileId = profile.Id,
            IsMappingEnabled = mappingEnabled,
            Profiles = { profile }
        };
    }

    private static InputMapping KeyboardMapping(bool suppressOriginal, string name = "F1 → X") =>
        new()
        {
            Name = name,
            SuppressOriginal = suppressOriginal,
            Source = KeyboardSource(),
            Action = new SendKeyAction()
        };

    private static InputMapping MouseMapping(bool suppressOriginal, string name = "中键 → Win+V") =>
        new()
        {
            Name = name,
            SuppressOriginal = suppressOriginal,
            Source = MouseSource(),
            Action = new OpenUriAction { Uri = "https://example.com/" }
        };

    private static InputSource KeyboardSource() => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Keyboard,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.KeyboardScanCode,
            Code = 0x3B
        }
    };

    private static InputSource MouseSource() => new()
    {
        Device = new InputDeviceSelector
        {
            Kind = InputDeviceKind.Mouse,
            MatchMode = DeviceMatchMode.AnyOfKind
        },
        Control = new InputControlId
        {
            Kind = InputControlKind.VirtualKey,
            Code = 4
        }
    };

    private static InputEvent Event(InputSource source, InputEventPhase phase) => new()
    {
        Source = source,
        Phase = phase,
        TimestampUtc = DateTimeOffset.UtcNow,
        SequenceNumber = 1
    };

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
