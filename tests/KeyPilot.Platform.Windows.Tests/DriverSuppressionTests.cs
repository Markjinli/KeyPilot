using System.Collections.Concurrent;
using System.ComponentModel;
using KeyPilot.App.Presentation;
using KeyPilot.Core.Actions;
using KeyPilot.Core.Configuration;
using KeyPilot.Core.Input;
using KeyPilot.DriverClient;

namespace KeyPilot.Platform.Windows.Tests;

internal static class DriverSuppressionTests
{
    public static Task RuleBuilderHonorsProtocolBoundariesAsync()
    {
        var normal = Mapping("normal", KeyboardSource(0x1E, 0x0000, isExtended: false));
        var duplicate = Mapping("duplicate", KeyboardSource(0x1E, 0x0000, isExtended: false));
        var e0 = Mapping("e0", KeyboardSource(0x1D, 0x0002, isExtended: true));
        var e1 = Mapping("e1", KeyboardSource(0x45, 0x0004, isExtended: true));
        var exact = Mapping(
            "exact",
            KeyboardSource(0x30, 0x0000, isExtended: false, DeviceMatchMode.ExactDevice));
        var emergency = Mapping("emergency", KeyboardSource(0x1D, 0x0000, isExtended: false));
        var ambiguous = Mapping("ambiguous", KeyboardSource(0x20, null, isExtended: false));
        var unsupported = Mapping("gamepad", new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Gamepad,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId { Kind = InputControlKind.GamepadButton, Code = 1 }
        });
        var unsupportedMouse = Mapping("mouse", new InputSource
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Mouse,
                MatchMode = DeviceMatchMode.AnyOfKind
            },
            Control = new InputControlId { Kind = InputControlKind.VirtualKey, Code = 4 }
        });
        var passthrough = Mapping(
            "passthrough",
            KeyboardSource(0x21, 0x0000, isExtended: false)) with
        {
            SuppressOriginal = false
        };
        var disabled = Mapping(
            "disabled",
            KeyboardSource(0x22, 0x0000, isExtended: false)) with
        {
            IsEnabled = false
        };
        var active = new MappingProfile
        {
            Name = "active",
            Mappings =
            {
                normal, duplicate, e0, e1, exact, emergency, ambiguous,
                unsupported, unsupportedMouse, passthrough, disabled
            }
        };
        var inactive = new MappingProfile
        {
            Name = "inactive",
            Mappings = { Mapping("inactive", KeyboardSource(0x23, 0, false)) }
        };
        var configuration = new KeyPilotConfiguration
        {
            IsMappingEnabled = true,
            ActiveProfileId = active.Id,
            Profiles = { inactive, active }
        };

        var exactHash = Enumerable.Range(1, KeyPilotDriverClient.DeviceHashLength)
            .Select(value => (byte)value)
            .ToArray();
        var result = DriverSuppressionRuleBuilder.Build(configuration, _ => exactHash);

        Require(result.Rules.Count == 4, "The three wildcard rules and one exact-device rule should remain.");
        KeyPilotDriverClient.ValidateRules(result.Rules);
        var normalRule = result.Rules.Single(rule => rule.MakeCode == 0x1E);
        var e0Rule = result.Rules.Single(rule => rule.MakeCode == 0x1D);
        var e1Rule = result.Rules.Single(rule => rule.MakeCode == 0x45);
        var exactRule = result.Rules.Single(rule => rule.MakeCode == 0x30);
        Require(
            normalRule.RequiredFlags == 0 && normalRule.IgnoredFlags == 0x0006,
            "Normal scan codes must exclude both E0 and E1.");
        Require(
            e0Rule.RequiredFlags == 0x0002 && e0Rule.IgnoredFlags == 0x0004,
            "E0 scan codes must require E0 and exclude E1.");
        Require(
            e1Rule.RequiredFlags == 0x0004 && e1Rule.IgnoredFlags == 0x0002,
            "E1 scan codes must require E1 and exclude E0.");
        Require(
            result.Bindings[normalRule.RuleId].Mappings.Count == 2,
            "Duplicate predicates should share one driver rule while retaining both mappings.");
        Require(exactRule.DeviceHash.Span.SequenceEqual(exactHash), "ExactDevice must retain its resolved protocol hash.");
        Require(DriverSuppressionRuleBuilder.RuleMatchesSource(exactRule, exact.Source), "Exact source lookup must include the device hash.");
        Require(
            DriverSuppressionRuleBuilder.EventMatchesRule(
                DriverEvent(1, exactRule, 1, DriverInputPhase.Down) with { DeviceHash = exactHash },
                exactRule),
            "Driver events with the exact hash must match.");
        Require(
            !DriverSuppressionRuleBuilder.EventMatchesRule(
                DriverEvent(1, exactRule, 1, DriverInputPhase.Down),
                exactRule),
            "An event from another device hash must not match an exact rule.");
        Require(
            result.Issues.Any(issue =>
                issue.Kind == DriverRuleIssueKind.ProtocolRejected && issue.MappingId == emergency.Id),
            "The emergency left-Ctrl rule must be skipped and reported.");
        Require(
            result.Issues.Any(issue => issue.Kind == DriverRuleIssueKind.AmbiguousScanPrefix),
            "An ambiguous prefix must be skipped and reported.");
        Require(
            result.Issues.Any(issue => issue.Kind == DriverRuleIssueKind.UnsupportedSource),
            "Non-keyboard sources must be skipped and reported.");
        Require(
            result.Issues.Any(issue =>
                issue.Kind == DriverRuleIssueKind.UnsupportedSource && issue.MappingId == unsupportedMouse.Id),
            "Mouse VirtualKey sources must be reported as unsupported for driver suppression.");
        Require(
            result.Rules.Where(rule => rule != exactRule)
                .All(rule => rule.DeviceHash.Span.ToArray().All(value => value == 0)),
            "AnyOfKind rules must use the explicit all-keyboards zero hash.");

        var failedExact = DriverSuppressionRuleBuilder.Build(
            Configuration(exact),
            _ => throw new InvalidOperationException("synthetic resolution failure"));
        Require(failedExact.Rules.Count == 0, "A failed exact-device lookup must never create a wildcard rule.");
        Require(
            failedExact.Issues.Any(issue => issue.Kind == DriverRuleIssueKind.ExactDeviceResolutionFailed),
            "A failed exact-device lookup must be reported explicitly.");
        return Task.CompletedTask;
    }

    public static async Task CoordinatorMapsPhasesAndGenerationsAsync()
    {
        var fake = new FakeDriverClient();
        var received = new ConcurrentQueue<DriverSuppressionInputEventArgs>();
        var statuses = new ConcurrentQueue<DriverSuppressionStatus>();
        using var coordinator = CreateCoordinator(() => fake);
        coordinator.InputReceived += (_, args) =>
        {
            received.Enqueue(args);
            coordinator.ReportInputCompletion(
                args.ConnectionEpoch,
                args.DriverSequence,
                new MappingInputCompletion(true, checked((long)args.DriverSequence), null),
                args.PolicyRevision);
        };
        coordinator.StatusChanged += (_, status) => statuses.Enqueue(status);
        var firstSource = KeyboardSource(0x1E, 0, false);
        coordinator.ApplyConfiguration(Configuration(Mapping("first", firstSource)), runtimeRevision: 101);
        coordinator.Start();
        await WaitUntilAsync(() => fake.RuleGenerations.Count == 1, "Initial rules were not installed.");
        var firstGeneration = fake.RuleGenerations.Single();
        var firstRule = fake.RuleSets.Single().Single();
        Require(firstGeneration > 0, "The first generation must be non-zero.");
        Require(coordinator.IsSuppressing(firstSource), "Accepted rules must own their Raw Input path.");

        fake.Events.Enqueue(DriverEvent(firstGeneration, firstRule, 1, DriverInputPhase.Down));
        fake.Events.Enqueue(DriverEvent(firstGeneration, firstRule, 2, DriverInputPhase.Repeat));
        fake.Events.Enqueue(DriverEvent(firstGeneration, firstRule, 3, DriverInputPhase.Up));
        await WaitUntilAsync(() => received.Count == 3, "Driver phases were not dispatched.");
        Require(
            received.Select(item => item.InputEvent.Phase).SequenceEqual(new[]
            {
                InputEventPhase.Pressed,
                InputEventPhase.Repeated,
                InputEventPhase.Released
            }),
            "Driver Down/Repeat/Up must map exactly to Core phases.");
        Require(received.All(item => item.InputEvent.Source == firstSource), "Rule binding must restore its original source.");
        Require(received.All(item => item.PolicyRevision == 1), "Initial input must carry its policy revision.");
        Require(received.All(item => item.RuntimeRevision == 101), "Initial input must carry its runtime revision.");
        await WaitUntilAsync(
            () => fake.HeartbeatAcks.Any(value => value == 3),
            "Heartbeat ACK did not advance after successful dispatch.");

        var secondSource = KeyboardSource(0x30, 0, false);
        coordinator.ApplyConfiguration(Configuration(Mapping("second", secondSource)), runtimeRevision: 202);
        await WaitUntilAsync(() => fake.RuleGenerations.Count == 2, "Replacement rules were not installed.");
        var secondGeneration = fake.RuleGenerations.Last();
        var secondRule = fake.RuleSets.Last().Single();
        Require(secondGeneration > firstGeneration, "Ruleset generations must increase monotonically.");
        Require(!coordinator.IsSuppressing(firstSource), "Old predicates must be replaced atomically.");
        Require(coordinator.IsSuppressing(secondSource), "The replacement predicate must become active.");

        fake.Events.Enqueue(DriverEvent(secondGeneration, secondRule, 4, DriverInputPhase.Down));
        await WaitUntilAsync(() => received.Count == 4, "Current-generation input was not dispatched.");
        Require(received.Last().InputEvent.Source == secondSource, "The current ruleId must resolve to the replacement source.");
        Require(received.Last().PolicyRevision == 2, "Replacement input must carry the new policy revision.");
        Require(received.Last().RuntimeRevision == 202, "Replacement input must carry the new runtime revision.");
        await WaitUntilAsync(
            () => fake.HeartbeatAcks.Any(value => value == 4),
            "Current-generation input was not acknowledged after successful completion.");

        fake.Events.Enqueue(DriverEvent(firstGeneration, firstRule, 5, DriverInputPhase.Down));
        await WaitUntilAsync(
            () => statuses.Any(status => status.State == DriverSuppressionState.FailOpen),
            "A stale generation did not force fail-open.");
        Require(received.Count == 4, "A stale generation must not reach the replacement profile.");
        Require(!fake.HeartbeatAcks.Any(value => value >= 5), "A stale generation must never be acknowledged.");
        Require(fake.Disposed, "A stale generation must release the suppression lease.");
        Require(statuses.Any(status => status.State == DriverSuppressionState.Active), "Active status must be visible.");
        coordinator.Stop();
        Require(fake.Disposed, "Stopping must close the lease-owning transport.");
    }

    public static async Task SupersededPolicyCompletionCannotAcknowledgeAsync()
    {
        var fake = new FakeDriverClient();
        var delivered = new TaskCompletionSource<DriverSuppressionInputEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failOpen = new TaskCompletionSource<DriverSuppressionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = CreateCoordinator(() => fake, reconnect: TimeSpan.FromSeconds(5));
        coordinator.InputReceived += (_, args) => delivered.TrySetResult(args);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State == DriverSuppressionState.FailOpen)
            {
                failOpen.TrySetResult(status);
            }
        };

        coordinator.ApplyConfiguration(Configuration(
            Mapping("first", KeyboardSource(0x1E, 0, false))), runtimeRevision: 11);
        coordinator.Start();
        await WaitUntilAsync(() => fake.RuleGenerations.Count == 1, "Initial policy was not installed.");
        var rule = fake.RuleSets.Single().Single();
        fake.Events.Enqueue(DriverEvent(fake.RuleGenerations.Single(), rule, 1, DriverInputPhase.Down));
        var input = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(input.PolicyRevision == 1 && input.RuntimeRevision == 11,
            "Suppressed input did not retain its policy/runtime revisions.");

        coordinator.ApplyConfiguration(Configuration(
            Mapping("second", KeyboardSource(0x30, 0, false))), runtimeRevision: 12);
        Require(coordinator.ReportInputCompletion(
            input.ConnectionEpoch,
            input.DriverSequence,
            new MappingInputCompletion(true, 1, null),
            input.PolicyRevision), "The stale report should wake the lease worker so it can fail open.");

        await failOpen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(!fake.HeartbeatAcks.Any(value => value >= 1),
            "A completion from a superseded policy must never advance the kernel ACK.");
        Require(fake.Disposed, "A superseded completion must release the driver lease.");
    }

    public static async Task HeartbeatFailureImmediatelyFailsOpenAsync()
    {
        var fake = new FakeDriverClient { ThrowOnHeartbeat = true };
        var failOpen = new TaskCompletionSource<DriverSuppressionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = CreateCoordinator(() => fake, reconnect: TimeSpan.FromSeconds(5));
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State == DriverSuppressionState.FailOpen)
            {
                failOpen.TrySetResult(status);
            }
        };
        var source = KeyboardSource(0x1E, 0, false);
        coordinator.ApplyConfiguration(Configuration(Mapping("heartbeat", source)));
        coordinator.Start();

        await failOpen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(fake.Disposed, "A heartbeat failure must immediately dispose the driver client.");
        Require(!coordinator.IsActive, "A failed lease must clear the active suppression snapshot.");
        Require(!coordinator.IsSuppressing(source), "Raw Input must resume after heartbeat failure.");
    }

    public static async Task ReadOrDispatchFailureImmediatelyFailsOpenAsync()
    {
        foreach (var failDuringDispatch in new[] { false, true })
        {
            var fake = new FakeDriverClient { ThrowOnRead = !failDuringDispatch };
            var failOpen = new TaskCompletionSource<DriverSuppressionStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var coordinator = CreateCoordinator(() => fake, reconnect: TimeSpan.FromSeconds(5));
            if (failDuringDispatch)
            {
                coordinator.InputReceived += (_, _) => throw new SyntheticDriverDispatchException();
            }
            coordinator.StatusChanged += (_, status) =>
            {
                if (status.State == DriverSuppressionState.FailOpen)
                {
                    failOpen.TrySetResult(status);
                }
            };
            var source = KeyboardSource(0x1E, 0, false);
            coordinator.ApplyConfiguration(Configuration(Mapping("read", source)));
            coordinator.Start();
            await WaitUntilAsync(() => fake.RuleGenerations.Count >= 1, "Rules were not installed before failure.");
            if (failDuringDispatch)
            {
                var rule = fake.RuleSets.Single().Single();
                fake.Events.Enqueue(DriverEvent(fake.RuleGenerations.Single(), rule, 1, DriverInputPhase.Down));
            }

            await failOpen.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(fake.Disposed, "Read/dispatch failure must dispose the driver transport.");
            Require(!coordinator.IsActive, "Read/dispatch failure must clear suppression ownership.");
        }
    }

    public static async Task FailedActionCompletionImmediatelyFailsOpenAsync()
    {
        var fake = new FakeDriverClient();
        var failOpen = new TaskCompletionSource<DriverSuppressionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = CreateCoordinator(() => fake, reconnect: TimeSpan.FromSeconds(5));
        coordinator.InputReceived += (_, args) => coordinator.ReportInputCompletion(
            args.ConnectionEpoch,
            args.DriverSequence,
            new MappingInputCompletion(
                false,
                1,
                new InvalidOperationException("synthetic mapped action failure")));
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State == DriverSuppressionState.FailOpen)
            {
                failOpen.TrySetResult(status);
            }
        };
        coordinator.ApplyConfiguration(Configuration(
            Mapping("failure", KeyboardSource(0x1E, 0, false))));
        coordinator.Start();
        await WaitUntilAsync(() => fake.RuleGenerations.Count == 1, "Rules were not installed.");
        var rule = fake.RuleSets.Single().Single();
        fake.Events.Enqueue(DriverEvent(fake.RuleGenerations.Single(), rule, 1, DriverInputPhase.Down));

        await failOpen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(fake.Disposed, "A failed mapped action must release the suppression lease.");
        Require(!fake.HeartbeatAcks.Any(value => value >= 1), "A failed action must never be acknowledged.");
    }

    public static async Task AcknowledgementCannotCrossCompletionGapAsync()
    {
        var fake = new FakeDriverClient();
        DriverSuppressionInputEventArgs? first = null;
        using var coordinator = CreateCoordinator(() => fake, reconnect: TimeSpan.FromSeconds(5));
        coordinator.InputReceived += (_, args) =>
        {
            if (args.DriverSequence == 1)
            {
                first = args;
                return;
            }

            coordinator.ReportInputCompletion(
                args.ConnectionEpoch,
                args.DriverSequence,
                new MappingInputCompletion(true, 1, null));
        };
        coordinator.ApplyConfiguration(Configuration(
            Mapping("gap", KeyboardSource(0x1E, 0, false))));
        coordinator.Start();
        await WaitUntilAsync(() => fake.RuleGenerations.Count == 1, "Rules were not installed.");
        var rule = fake.RuleSets.Single().Single();
        var generation = fake.RuleGenerations.Single();
        fake.Events.Enqueue(DriverEvent(generation, rule, 1, DriverInputPhase.Down));
        fake.Events.Enqueue(DriverEvent(generation, rule, 2, DriverInputPhase.Repeat));
        await WaitUntilAsync(() => first is not null, "First completion was not held.");
        await Task.Delay(80);
        Require(
            !fake.HeartbeatAcks.Any(value => value >= 2),
            "A completed later event must not ACK across the first sequence gap.");

        coordinator.ReportInputCompletion(
            first!.ConnectionEpoch,
            first.DriverSequence,
            new MappingInputCompletion(true, 2, null));
        await WaitUntilAsync(
            () => fake.HeartbeatAcks.Any(value => value == 2),
            "Contiguous ACK did not advance after the gap completed.");
    }

    public static async Task ReconnectKeepsGenerationMonotonicAsync()
    {
        var first = new FakeDriverClient { ThrowOnRead = true };
        var second = new FakeDriverClient();
        var clients = new ConcurrentQueue<FakeDriverClient>(new[] { first, second });
        using var coordinator = CreateCoordinator(
            () => clients.TryDequeue(out var client)
                ? client
                : throw new Win32Exception(2),
            reconnect: TimeSpan.FromMilliseconds(10));
        coordinator.ApplyConfiguration(Configuration(
            Mapping("reconnect", KeyboardSource(0x1E, 0, false))));
        coordinator.Start();

        await WaitUntilAsync(() => second.RuleGenerations.Count == 1, "The second lease did not reconnect.");
        Require(first.Disposed, "The failed first lease must be disposed before reconnecting.");
        Require(
            second.RuleGenerations.Single() > first.RuleGenerations.Single(),
            "Generation must remain monotonic across lease reconnection.");
    }

    public static Task ConnectionErrorsHaveExplicitStatesAsync()
    {
        Require(
            DriverSuppressionCoordinator.ClassifyConnectionFailure(new Win32Exception(2)).State ==
            DriverSuppressionState.DriverNotInstalled,
            "ERROR_FILE_NOT_FOUND must mean driver not installed.");
        var access = DriverSuppressionCoordinator.ClassifyConnectionFailure(new Win32Exception(5));
        Require(
            access.State == DriverSuppressionState.AdministratorRequired &&
            access.Message.Contains("管理员启动", StringComparison.Ordinal),
            "ERROR_ACCESS_DENIED must explicitly explain the current administrator requirement.");
        Require(
            DriverSuppressionCoordinator.ClassifyConnectionFailure(new IOException("broken")).State ==
            DriverSuppressionState.FailOpen,
            "Other transport failures must be classified fail-open.");
        return Task.CompletedTask;
    }

    public static async Task InputTrackingLossRefusesLeaseAsync()
    {
        var fake = new FakeDriverClient
        {
            CapabilitiesState = DriverStateFlags.FailOpen | DriverStateFlags.InputTrackingLost
        };
        var statusSource = new TaskCompletionSource<DriverSuppressionStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = CreateCoordinator(() => fake, reconnect: TimeSpan.FromSeconds(5));
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.State == DriverSuppressionState.FailOpen &&
                status.Message.Contains("跟踪丢失", StringComparison.Ordinal))
            {
                statusSource.TrySetResult(status);
            }
        };
        coordinator.ApplyConfiguration(Configuration(
            Mapping("tracking", KeyboardSource(0x1E, 0, false))));
        coordinator.Start();

        await statusSource.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(fake.RequestedLease == TimeSpan.Zero, "Tracking loss must be rejected before lease acquisition.");
        Require(fake.Disposed, "Tracking-loss capability must close the transport.");
    }

    private static DriverSuppressionCoordinator CreateCoordinator(
        Func<IDriverSuppressionClient> factory,
        TimeSpan? reconnect = null) =>
        new(
            factory,
            heartbeatInterval: TimeSpan.FromMilliseconds(50),
            readInterval: TimeSpan.FromMilliseconds(5),
            reconnectInterval: reconnect ?? TimeSpan.FromSeconds(1));

    private static KeyPilotConfiguration Configuration(params InputMapping[] mappings)
    {
        var profile = new MappingProfile { Name = "active", Mappings = mappings.ToList() };
        return new KeyPilotConfiguration
        {
            IsMappingEnabled = true,
            ActiveProfileId = profile.Id,
            Profiles = { profile }
        };
    }

    private static InputMapping Mapping(string name, InputSource source) =>
        new()
        {
            Name = name,
            Source = source,
            Trigger = new MappingTrigger { Kind = MappingTriggerKind.KeyDown },
            Action = new SendKeyAction
            {
                Target = new InputControlId { Kind = InputControlKind.VirtualKey, Code = 0x41 }
            }
        };

    private static InputSource KeyboardSource(
        int makeCode,
        ushort? prefix,
        bool isExtended,
        DeviceMatchMode matchMode = DeviceMatchMode.AnyOfKind) =>
        new()
        {
            Device = new InputDeviceSelector
            {
                Kind = InputDeviceKind.Keyboard,
                MatchMode = matchMode,
                DeviceId = matchMode == DeviceMatchMode.ExactDevice ? "DEVICE-A" : null
            },
            Control = new InputControlId
            {
                Kind = InputControlKind.KeyboardScanCode,
                Code = makeCode,
                IsExtended = isExtended,
                RawQualifier = prefix.HasValue
                    ? $"RAWKEYBOARD-V1;PREFIX={prefix.Value:X4}"
                    : null
            }
        };

    private static DriverInputEvent DriverEvent(
        ulong generation,
        KeyboardRule rule,
        ulong sequence,
        DriverInputPhase phase)
    {
        var flags = rule.RequiredFlags;
        if (phase == DriverInputPhase.Up)
        {
            flags |= 0x0001;
        }

        return new DriverInputEvent(
            generation,
            rule.RuleId,
            sequence,
            new byte[KeyPilotDriverClient.DeviceHashLength],
            rule.MakeCode,
            flags,
            0,
            phase,
            0);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            try
            {
                await Task.Delay(5, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class SyntheticDriverDispatchException : Exception;
}

internal sealed class FakeDriverClient : IDriverSuppressionClient
{
    public ConcurrentQueue<DriverInputEvent> Events { get; } = new();

    public ConcurrentQueue<ulong> RuleGenerations { get; } = new();

    public ConcurrentQueue<IReadOnlyList<KeyboardRule>> RuleSets { get; } = new();

    public ConcurrentQueue<ulong> HeartbeatAcks { get; } = new();

    public TimeSpan RequestedLease { get; private set; }

    public int HeartbeatCount { get; private set; }

    public bool ThrowOnHeartbeat { get; init; }

    public bool ThrowOnRead { get; init; }

    public bool Disposed { get; private set; }

    public DriverStateFlags CapabilitiesState { get; init; } = DriverStateFlags.FailOpen;

    private ulong _lastDeliveredSequence;

    private ulong _lastAcknowledgedSequence;

    public DriverCapabilities GetCapabilities() =>
        new(1, 0, KeyPilotDriverClient.MaximumRules, CapabilitiesState, 0);

    public TimeSpan AcquireLease(TimeSpan requested)
    {
        RequestedLease = requested;
        return requested;
    }

    public void ReplaceRules(IReadOnlyList<KeyboardRule> rules, ulong generation)
    {
        RuleGenerations.Enqueue(generation);
        RuleSets.Enqueue(rules.ToArray());
    }

    public void Heartbeat(ulong lastSuccessfullyDispatchedSequence)
    {
        HeartbeatCount++;
        if (ThrowOnHeartbeat)
        {
            throw new IOException("synthetic heartbeat failure");
        }

        if (lastSuccessfullyDispatchedSequence < _lastAcknowledgedSequence ||
            lastSuccessfullyDispatchedSequence > _lastDeliveredSequence)
        {
            throw new InvalidDataException("Synthetic ACK is outside the delivered sequence window.");
        }

        _lastAcknowledgedSequence = lastSuccessfullyDispatchedSequence;
        HeartbeatAcks.Enqueue(lastSuccessfullyDispatchedSequence);
    }

    public IReadOnlyList<DriverInputEvent> ReadEvents(int maximumEvents = 64)
    {
        if (ThrowOnRead)
        {
            throw new IOException("synthetic read failure");
        }

        var result = new List<DriverInputEvent>();
        while (result.Count < maximumEvents && Events.TryDequeue(out var input))
        {
            if (input.Sequence != _lastDeliveredSequence + 1)
            {
                throw new InvalidDataException("Synthetic driver event sequence is not contiguous.");
            }

            _lastDeliveredSequence = input.Sequence;
            result.Add(input);
        }

        return result;
    }

    public void Dispose() => Disposed = true;
}
